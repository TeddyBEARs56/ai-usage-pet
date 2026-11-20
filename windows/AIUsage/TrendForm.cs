using System.Drawing.Drawing2D;
using System.Globalization;
using static AIUsage.L10n;

namespace AIUsage;

sealed class TrendForm : Form
{
    readonly RadioButton week = new(), month = new();
    readonly FlowLayoutPanel buttons = new() { AutoSize = true, WrapContents = false };
    Dictionary<string,TrendResult> data = new();
    Pane[] order = Settings.Order;
    long now;

    public TrendForm()
    {
        Text = T("사용 추이");
        BackColor = Color.FromArgb(35,35,38);
        ForeColor = Palette.Text;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        foreach (var b in new[] { week, month })
        {
            b.Appearance = Appearance.Button;
            b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Palette.Track;
            b.FlatAppearance.CheckedBackColor = Color.FromArgb(72,72,78);
            b.CheckedChanged += (_,_) => Invalidate();
            buttons.Controls.Add(b);
        }
        Controls.Add(buttons);
        week.Checked = true;
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplyDpi(DeviceDpi, new Size(630,490));
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        var logical = Size.Round(new SizeF(ClientSize.Width * 96f / e.DeviceDpiOld, ClientSize.Height * 96f / e.DeviceDpiOld));
        base.OnDpiChanged(e);
        ApplyDpi(e.DeviceDpiNew, logical);
    }

    void ApplyDpi(int dpi, Size logicalClient)
    {
        int ScalePixels(int value) => (int)Math.Round(value * dpi / 96.0);
        // MinimumSize includes the non-client frame. The two charts need 470 logical client pixels.
        MinimumSize = SizeFromClientSize(new Size(ScalePixels(560),ScalePixels(470)));
        if (WindowState == FormWindowState.Normal)
            ClientSize = new Size(ScalePixels(logicalClient.Width),ScalePixels(logicalClient.Height));
        buttons.Location = new Point(ScalePixels(18),ScalePixels(14));
        Invalidate();
    }

    internal void SetData(Dictionary<string,TrendResult> values, long current, Pane[] panes)
    {
        data = values; now = current; order = panes;
        Text = T("사용 추이"); week.Text = T("이번 주"); month.Text = T("지난 4주");
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var scale = DeviceDpi/96f;
        g.ScaleTransform(scale,scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float width = ClientSize.Width/scale, y = 64;
        using var normal = new Font(Font.FontFamily,13,FontStyle.Regular,GraphicsUnit.Pixel);
        using var bold = new Font(Font.FontFamily,14,FontStyle.Bold,GraphicsUnit.Pixel);
        using var small = new Font(Font.FontFamily,10,FontStyle.Regular,GraphicsUnit.Pixel);
        using var secondary = new SolidBrush(Palette.Secondary);
        using var grid = new Pen(Color.FromArgb(62,62,66));
        foreach (var pane in order)
        {
            if (!data.TryGetValue(pane == Pane.Codex ? "codex" : "claude",out var r)) continue;
            var tint = pane == Pane.Codex ? Palette.ChatGPT : Palette.Claude;
            using var color = new SolidBrush(tint);
            g.DrawLine(grid,18,y-9,width-18,y-9);
            g.FillEllipse(color,19,y+6,8,8);
            var title = Settings.Title(pane);
            g.DrawString(title,bold,Brushes.White,33,y);
            g.DrawString(month.Checked ? T("하루에 쓴 주간 한도") : T("주간 한도"),normal,secondary,
                         39+g.MeasureString(title,bold).Width,y+1);
            var w = r.Week;
            var summary = month.Checked
                ? r.Average is { } avg ? T("최근 {0}일 하루 평균 {1}%",r.AverageDays,Round(avg)) : ""
                : w != null ? T("{0}% 씀 · ↺ {1}",Round(w.Used),Date(w.ResetMs).ToString("M'/'d(ddd) HH:mm",Culture)) : "";
            g.DrawString(summary,normal,secondary,width-18-g.MeasureString(summary,normal).Width,y+1);
            float left = 55, right = width-26, top = y+35, bottom = top+100;
            var max = month.Checked ? Math.Max(60,Math.Ceiling((r.Days.Max(d => d.Used) ?? 0)/30)*30) : 100;
            float Y(double v) => bottom-(float)(v/max)*(bottom-top);
            void Label(string s,float x,float at) => g.DrawString(s,small,secondary,x-g.MeasureString(s,small).Width/2,at);
            for (var v=0; v <= max; v += month.Checked ? 30 : 50)
            {
                g.DrawLine(grid,left,Y(v),right,Y(v));
                var text = v+"%"; g.DrawString(text,small,secondary,left-5-g.MeasureString(text,small).Width,Y(v)-7);
            }
            var footer = "";
            var warn = false;
            if (month.Checked)
            {
                var cell = (right-left)/28;
                var missing = r.Days.TakeWhile(d => d.Used == null).Count();
                if (missing > 0)
                {
                    using var empty = new SolidBrush(Color.FromArgb(12,Color.White));
                    g.FillRectangle(empty,left,top,missing*cell,bottom-top);
                    Label(T("기록 없음"),left+missing*cell/2,Y(max/2)-6);
                }
                using var rough = new SolidBrush(Color.FromArgb(90,tint));
                using var marker = new Pen(Palette.Tertiary) { DashStyle = DashStyle.Dot };
                for (var i=0; i<r.Days.Count; i++)
                {
                    var day = r.Days[i];
                    var d = DateTime.ParseExact(day.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture);
                    var x = left+i*cell;
                    if (d.DayOfWeek == DayOfWeek.Monday) Label(d.ToString("M'/'d",Culture),x+cell/2,bottom+4);
                    if (day.Reset) { g.DrawLine(marker,x,top,x,bottom); Label("↺",x+4,top-12); }
                    if (day.Used is not > 0) continue;
                    var bar = Y(day.Used.Value);
                    g.FillRectangle(day.Rough ? rough : color,x+2,bar,Math.Max(1,cell-4),bottom-bar);
                    if (day.Date == r.Best?.Date)
                    {
                        Label(Round(day.Used.Value)+"%",x+cell/2,bar-13);
                        footer = T("가장 많이 쓴 날 {0} {1}%",d.ToString("M'/'d(ddd)",Culture),Round(day.Used.Value));
                    }
                }
            }
            else if (w is null) footer = T("아직 이번 주 기록이 없습니다");
            else
            {
                float X(long t) => left+(right-left)*(float)((double)(t-w.StartMs)/UsageHistory.Week);
                for (var day=Date(w.StartMs).Date.AddDays(1); day < Date(w.ResetMs); day=day.AddDays(1))
                {
                    var midnight = new DateTimeOffset(day).ToUnixTimeMilliseconds();
                    var x = X(midnight);
                    g.DrawLine(grid,x,top,x,bottom);
                    var middle = midnight + UsageHistory.Day/2;
                    if (middle < w.ResetMs) Label(day.ToString("ddd",Culture),X(middle),bottom+4);
                }
                using var even = new Pen(Palette.Tertiary) { DashStyle = DashStyle.Dash };
                g.DrawLine(even,left,bottom,right,top);
                if (w.EndMs is { } end && w.EndUsed is { } endUsed)
                {
                    using var future = new Pen(Color.FromArgb(120,tint),1.5f) { DashStyle = DashStyle.Dot };
                    g.DrawLine(future,X(now),Y(w.Used),X(end),Y(endUsed));
                    warn = end < w.ResetMs;
                    if (warn) g.FillEllipse(Brushes.Orange,X(end)-3,Y(endUsed)-3,6,6);
                    footer = warn ? T("이 속도면 {0} {1}시쯤 다 씁니다 — 초기화 {2}시간 전",Date(end).ToString("M'/'d(ddd)",Culture),
                                  Date(end).Hour,Round((w.ResetMs-end)/3600000.0))
                                  : T("이 속도면 초기화 때 약 {0}% 씁니다 — 넉넉합니다",Round(endUsed));
                }
                var first = w.Points[0];
                var pts = new List<PointF> { new(X(first.AtMs),Y(first.Used)) };
                foreach (var p in w.Points.Skip(1))
                {
                    pts.Add(new(X(p.AtMs),pts[^1].Y)); pts.Add(new(X(p.AtMs),Y(p.Used)));
                }
                var current = new PointF(X(now),Y(w.Used));
                pts.Add(current);
                using var fill = new SolidBrush(Color.FromArgb(42,tint));
                g.FillPolygon(fill,pts.Concat(new[] { new PointF(current.X,bottom),new PointF(pts[0].X,bottom) }).ToArray());
                using var line = new Pen(tint,2);
                g.DrawLines(line,pts.ToArray());
                g.FillEllipse(color,current.X-3,current.Y-3,6,6);
                g.DrawString(T("지금"),small,Brushes.White,Math.Min(right-24,current.X+6),current.Y-15);
            }
            g.DrawString(footer,normal,warn ? Brushes.Orange : secondary,18,bottom+27);
            y += 201;
        }
    }
    static int Round(double v) => (int)Math.Floor(v+0.5);
    static DateTime Date(long t) => DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime;
}
