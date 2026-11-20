using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using static AIUsage.L10n;

namespace AIUsage;

/// 사용 추이 창 — Windows 기본 창틀 대신 맥 판처럼 어두운 창(시안 안 B): 어두운 제목 줄, 오른쪽 위 —·✕(✕ 에 마우스를 올리면 빨강),
/// 둥근 모서리, 크기 고정(테두리를 끌어 키우거나 최대화할 수 없다). 제목 줄을 끌어 옮긴다.
sealed class TrendForm : Form
{
    const int TitleH = 36, ButtonW = 46;   // 논리 픽셀(96 DPI 기준)
    static readonly Color TitleBack = Color.FromArgb(28,28,31);
    enum Hot { None, Minimize, Close }
    Hot hot = Hot.None, pressed = Hot.None;
    bool useRegion;
    readonly RadioButton week = new(), month = new();
    readonly FlowLayoutPanel buttons = new() { AutoSize = true, WrapContents = false };
    Dictionary<string,TrendResult> data = new();
    Pane[] order = Settings.Order.Where(p => p != Pane.Sessions).ToArray();  // 세션 칸은 추이가 없다
    long now;

    public TrendForm()
    {
        Text = T("사용 추이");
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        ShowInTaskbar = true;
        KeyPreview = true;
        if (Installer.LoadIcon() is { } icon) Icon = icon;
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

    /// 틀 없는 창이지만 그림자는 있게(CS_DROPSHADOW), 작업 표시줄에서 최소화·복원이 되게(WS_MINIMIZEBOX·WS_SYSMENU)
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;
            cp.Style |= 0x20000 | 0x80000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2;  // DWMWCP_ROUND: Windows 11 은 모서리를 둥글린다
        useRegion = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) != 0;  // Windows 10 은 직접 깎는다
        var border = ColorTranslator.ToWin32(Color.FromArgb(58,58,62));
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        if (useRegion) ApplyRegion();
    }

    void ApplyRegion()
    {
        if (!useRegion || Width <= 0 || Height <= 0) return;
        float r = 8 * DeviceDpi / 96f, w = Width, h = Height;
        using var path = new GraphicsPath();
        path.AddArc(0,0,2*r,2*r,180,90); path.AddArc(w-2*r,0,2*r,2*r,270,90);
        path.AddArc(w-2*r,h-2*r,2*r,2*r,0,90); path.AddArc(0,h-2*r,2*r,2*r,90,90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRegion();
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplyDpi(DeviceDpi, new Size(630,490+TitleH));
        base.OnLoad(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    // MARK: 제목 줄(끌어 옮기기 · — · ✕)

    float Dpi => DeviceDpi / 96f;
    Hot HitTest(Point p)
    {
        float x = p.X / Dpi, y = p.Y / Dpi, width = ClientSize.Width / Dpi;
        if (y < 0 || y >= TitleH) return Hot.None;
        if (x >= width - ButtonW) return Hot.Close;
        if (x >= width - 2 * ButtonW) return Hot.Minimize;
        return Hot.None;
    }

    void SetHot(Hot next)
    {
        if (hot == next) return;
        hot = next;
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)Math.Ceiling(TitleH * Dpi)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        pressed = Hot.None;
        SetHot(Hot.None);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var target = HitTest(e.Location);
        if (target != Hot.None) { pressed = target; return; }
        if (e.Y < TitleH * Dpi)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);  // WM_NCLBUTTONDOWN · HTCAPTION: 제목 줄을 끌어 옮긴다
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var target = HitTest(e.Location);
        var clicked = pressed != Hot.None && pressed == target;
        var which = pressed;
        pressed = Hot.None;
        if (!clicked) return;
        if (which == Hot.Close) Close();
        else if (which == Hot.Minimize) WindowState = FormWindowState.Minimized;
    }

    void DrawTitleBar(Graphics g, float width)
    {
        using (var back = new SolidBrush(TitleBack)) g.FillRectangle(back, 0, 0, width, TitleH);
        using (var line = new Pen(Color.FromArgb(18,Color.White))) g.DrawLine(line, 0, TitleH - 0.5f, width, TitleH - 0.5f);
        using var titleFont = new Font(Font.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.FromArgb(201,201,207));
        var size = g.MeasureString(Text, titleFont);
        g.DrawString(Text, titleFont, titleBrush, 14, (TitleH - size.Height) / 2);
        foreach (var (kind, x) in new[] { (Hot.Minimize, width - 2 * ButtonW), (Hot.Close, width - ButtonW) })
        {
            if (hot == kind)
            {
                using var fill = new SolidBrush(kind == Hot.Close ? Color.FromArgb(196,43,28) : Color.FromArgb(22,Color.White));
                g.FillRectangle(fill, x, 0, ButtonW, TitleH - 1);
            }
            using var glyph = new Pen(hot == kind && kind == Hot.Close ? Color.White : Color.FromArgb(201,201,207), 1f);
            float cx = x + ButtonW / 2f, cy = TitleH / 2f;
            if (kind == Hot.Minimize) g.DrawLine(glyph, cx - 5, cy, cx + 5, cy);
            else { g.DrawLine(glyph, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(glyph, cx - 5, cy + 5, cx + 5, cy - 5); }
        }
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpi(e.DeviceDpiNew, new Size(630,490+TitleH));
    }

    void ApplyDpi(int dpi, Size logicalClient)
    {
        int ScalePixels(int value) => (int)Math.Round(value * dpi / 96.0);
        // 크기 고정: 테두리가 없으니 끌어 키울 수 없고, 화면 배율이 바뀔 때만 같은 논리 크기로 다시 맞춘다
        var fixedSize = new Size(ScalePixels(logicalClient.Width),ScalePixels(logicalClient.Height));
        MinimumSize = MaximumSize = Size.Empty;
        if (WindowState == FormWindowState.Normal) ClientSize = fixedSize;
        MinimumSize = MaximumSize = Size;
        buttons.Location = new Point(ScalePixels(18),ScalePixels(14+TitleH));
        ApplyRegion();
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
        float width = ClientSize.Width/scale, y = 64+TitleH;
        DrawTitleBar(g, width);
        using var normal = new Font(Font.FontFamily,13,FontStyle.Regular,GraphicsUnit.Pixel);
        using var bold = new Font(Font.FontFamily,14,FontStyle.Bold,GraphicsUnit.Pixel);
        using var small = new Font(Font.FontFamily,10,FontStyle.Regular,GraphicsUnit.Pixel);
        using var secondary = new SolidBrush(Palette.Secondary);
        using var grid = new Pen(Color.FromArgb(62,62,66));
        foreach (var pane in order)
        {
            if (pane == Pane.Sessions || !data.TryGetValue(pane == Pane.Codex ? "codex" : "claude",out var r)) continue;
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
