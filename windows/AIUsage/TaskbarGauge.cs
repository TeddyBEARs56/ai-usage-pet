using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static AIUsage.L10n;

namespace AIUsage;

/// 간단 모드: 작업 표시줄의 알림 영역(트레이·시계) 바로 왼쪽에 붙는 동그란 게이지 줄.
/// 테두리 길이가 남은 비율, 가운데 숫자가 남은 %. 회사마다 앞에 색 점(주황 Claude, 초록 ChatGPT).
/// Windows 11 은 작업 표시줄에 창을 넣는 공식 방법(DeskBand)이 없어서 작업 표시줄 창(Shell_TrayWnd)의
/// 자식 창으로 붙인다 — 작업 표시줄과 함께 숨고(자동 숨김·전체 화면) 함께 움직인다.
/// 자식 창의 per-pixel 투명(WS_EX_LAYERED)은 Windows 8 이상이고 app.manifest 의 supportedOS 가 있어야 된다.
sealed class TaskbarGauge : Form
{
    /// 원 하나: 마우스를 올리면 보일 글과 남은 %(값이 없으면 null)
    public readonly record struct Ring(string Tip, int? Left);
    /// 회사 하나: 앞의 색 점과 원들
    public readonly record struct Group(Color Dot, IReadOnlyList<Ring> Rings);

    // 96 DPI 기준 픽셀. RingSize 는 「크게」의 원 지름(작업 표시줄 높이 48 에서 위아래 3씩 남김), 「작게」는 그 75%
    const int RingSize = 42, Pad = 6, TrayGap = 6;

    public IntPtr Taskbar { get; }
    /// 오른쪽 두 번 클릭: 떠 있는 창으로 바꾼다
    public event Action? FloatRequested;
    /// 오른쪽 한 번 클릭: 메뉴
    public event Action<Point>? MenuRequested;
    /// 왼쪽 한 번 클릭: 세션 패널(게이지의 화면 위치, 작업 표시줄 위 끝, 화면 DPI)
    public event Action<Rectangle, int, int>? SessionsRequested;

    IReadOnlyList<Group> groups = [];
    int sizePercent = 100;
    int waiting;  // 입력이 필요한 세션 수 — 있으면 맨 앞에 주황 「!」
    // 오른쪽 한 번 클릭은 두 번째 클릭이 오는지 볼 시간만큼 기다렸다가 메뉴를 연다
    readonly System.Windows.Forms.Timer rightTimer = new() { Interval = SystemInformation.DoubleClickTime };
    Point rightAt;
    (uint Dpi, bool Light, int BarHeight) drawnFor;
    readonly List<(Rectangle Bounds, string Tip)> hits = [];
    int hover = -1;
    bool hovering, pressed;
    readonly System.Windows.Forms.Timer leaveTimer = new() { Interval = 100 };
    string? broken;  // 한 번 그리지 못하면 이 창은 포기한다(떠 있는 창이 대신 보인다)
    readonly TipForm tip = new();
    readonly System.Windows.Forms.Timer tipTimer = new() { Interval = 400 };

    public TaskbarGauge(IntPtr taskbar)
    {
        Taskbar = taskbar;
        TopLevel = false;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        MouseMove += (_, e) => Hover(e.Location);
        MouseLeave += (_, _) => Hover(null);
        MouseDown += (_, _) => { pressed = true; Hover(PointToClient(Cursor.Position)); };  // 누르면 강조를 바로 끈다
        // 오른쪽 클릭: 한 번이면(두 번째가 안 오면) 메뉴, 두 번이면 떠 있는 창으로
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            HideTip();
            if (rightTimer.Enabled) { rightTimer.Stop(); FloatRequested?.Invoke(); }
            else { rightAt = Cursor.Position; rightTimer.Start(); }
        };
        // 왼쪽 클릭 한 번: 세션 패널을 바로 열고 닫는다
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            HideTip();
            GetWindowRect(Taskbar, out var bar);
            SessionsRequested?.Invoke(RectangleToScreen(ClientRectangle), bar.Top, (int)drawnFor.Dpi);
        };
        // 작업 표시줄 안의 창이라 마우스가 떠났다는 알림이 늦거나 빠질 때가 있어, 올라 있는 동안 0.1초마다 직접 본다
        leaveTimer.Tick += (_, _) =>
        {
            if (!IsHandleCreated || !ClientRectangle.Contains(PointToClient(Cursor.Position))) { leaveTimer.Stop(); Hover(null); }
            else if (!hovering && !pressed) leaveTimer.Stop();
        };
        rightTimer.Tick += (_, _) =>
        {
            rightTimer.Stop();
            MenuRequested?.Invoke(rightAt);
        };
        tipTimer.Tick += (_, _) => { tipTimer.Stop(); ShowTip(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = 0x40000000 | 0x04000000 | 0x02000000;  // WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN(처음엔 숨김)
            cp.ExStyle = 0x00080000 | 0x08000000 | 0x00000080; // WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.Parent = Taskbar;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnPaintBackground(PaintEventArgs e) { }  // UpdateLayeredWindow 로만 그린다

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tipTimer.Dispose(); rightTimer.Dispose(); leaveTimer.Dispose(); tip.Dispose(); }
        base.Dispose(disposing);
    }

    // MARK: 작업 표시줄 찾기·붙어 있는지

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    /// 주 화면의 작업 표시줄 창(없으면 0 — 탐색기가 다시 켜지는 중 등)
    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    /// 아직 그 작업 표시줄에 붙어 있나(탐색기가 다시 켜지면 작업 표시줄이 새로 생기고 이 창은 함께 없어진다)
    public bool Attached(IntPtr taskbar) =>
        taskbar == Taskbar && IsWindow(Taskbar) && IsHandleCreated && IsWindow(Handle) && GetParent(Handle) == Taskbar;

    static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;  // 작업 표시줄은 「Windows 모드」 색을 따른다
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    (uint Dpi, bool Light, int BarHeight) Surroundings()
    {
        var dpi = GetDpiForWindow(Taskbar);
        GetWindowRect(Taskbar, out var bar);
        return (dpi == 0 ? 96 : dpi, LightTaskbar(), Math.Min(bar.Bottom - bar.Top, bar.Right - bar.Left));
    }

    // MARK: 값 받기·자리 잡기

    /// 새 값으로 다시 그린다. 그리지 못하면(자식 창 투명을 못 쓰는 Windows 등) 이유를 돌려준다
    public string? SetData(IReadOnlyList<Group> next, int size, int waitingSessions)
    {
        groups = next;
        sizePercent = size;
        waiting = waitingSessions;
        return Render();
    }

    /// 1초마다: 화면 배율·밝은/어두운 모드가 바뀌었으면 다시 그리고, 알림 영역 왼쪽에 자리를 다시 잡는다.
    /// 붙일 수 없으면 숨기고 이유를 돌려준다
    public string? Tick()
    {
        if (broken != null) return Conceal(broken);
        if (Surroundings() != drawnFor && Render() is { } failed) return failed;
        return Reposition();
    }

    string? Reposition()
    {
        if (!GetWindowRect(Taskbar, out var bar)) return Conceal(T("작업 표시줄을 찾지 못했습니다"));
        if (bar.Bottom - bar.Top > bar.Right - bar.Left) return Conceal(T("세로 작업 표시줄은 지원하지 않습니다"));
        var notify = FindWindowEx(Taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out var tray)) return Conceal(T("작업 표시줄을 찾지 못했습니다"));
        int scale(int px) => (int)Math.Round(px * drawnFor.Dpi / 96.0);
        // 가운데(또는 왼쪽) 정렬된 시작 단추·앱 아이콘의 오른쪽 끝
        int icons = bar.Left;
        foreach (var cls in new[] { "Start", "ReBarWindow32" })
            if (FindWindowEx(Taskbar, IntPtr.Zero, cls, null) is var w && w != IntPtr.Zero && GetWindowRect(w, out var r) && r.Right > r.Left)
                icons = Math.Max(icons, r.Right);
        int x = tray.Left - scale(TrayGap) - Width;
        if (x < icons + scale(TrayGap)) return Conceal(T("작업 표시줄에 자리가 없습니다"));
        int y = (bar.Bottom - bar.Top - Height) / 2;
        // HWND_TOP: 작업 표시줄의 XAML 내용 창보다 위에. SWP_NOACTIVATE | SWP_SHOWWINDOW
        SetWindowPos(Handle, IntPtr.Zero, x - bar.Left, y, Width, Height, 0x0010 | 0x0040);
        return null;
    }

    string Conceal(string reason)
    {
        if (IsHandleCreated) ShowWindow(Handle, 0);
        HideTip();
        return reason;
    }

    // MARK: 그리기

    static Color Status(int left, bool light) => !light ? Palette.Status(left)
        : left >= 50 ? Color.FromArgb(0x1F, 0x9D, 0x47) : left >= 20 ? Color.FromArgb(0xB8, 0x86, 0x00) : Color.FromArgb(0xD9, 0x2D, 0x20);

    string? Render()
    {
        if (!IsHandleCreated) return null;
        var env = Surroundings();
        drawnFor = env;
        double unit = env.Dpi / 96.0;
        int px(double v) => (int)Math.Round(v * unit);
        int ring = Math.Max(px(16), Math.Min((int)Math.Round(RingSize * unit * sizePercent / 100), env.BarHeight - px(6)));
        int pad = px(Pad), gap = Math.Max(2, (int)Math.Round(ring * 0.22)), dot = Math.Max(4, (int)Math.Round(ring * 0.2));
        int separator = Math.Max(1, px(1)), stroke = Math.Max(2, (int)Math.Round(ring * 0.1));
        int height = Math.Min(env.BarHeight, ring + pad * 2);

        // 자리: 점·원·원 | 점·원
        hits.Clear();
        var layout = new List<(int X, Color Color, string? Kind, Ring Ring)>();  // Kind: "badge" / "dot" / "sep" / null(원)
        int x = pad;
        int badge = Math.Max(px(10), (int)Math.Round(ring * 0.5));
        if (waiting > 0)  // 입력이 필요한 세션이 있으면 맨 앞에 주황 「!」
        {
            layout.Add((x, Palette.Waiting, "badge", default));
            hits.Add((new Rectangle(x, (height - badge) / 2, badge, badge), T("입력이 필요한 세션 {0}개", waiting)));
            x += badge + gap;
        }
        foreach (var group in groups)
        {
            if (layout.Count > 0 && layout[^1].Kind != "badge")
            {
                x += gap;
                layout.Add((x, Color.Empty, "sep", default));
                x += separator + gap;
            }
            layout.Add((x, group.Dot, "dot", default));
            x += dot + gap;
            for (int i = 0; i < group.Rings.Count; i++)
            {
                if (i > 0) x += gap;
                layout.Add((x, Color.Empty, null, group.Rings[i]));
                hits.Add((new Rectangle(x, (height - ring) / 2, ring, ring), group.Rings[i].Tip));
                x += ring;
            }
        }
        var size = new Size(Math.Max(x + pad, pad * 2 + ring), height);
        if (Size != size) Size = size;

        var text = env.Light ? Color.FromArgb(0x1E, 0x1E, 0x20) : Palette.Text;
        var track = env.Light ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255);
        var ok = LayeredSurface.Present(Handle, size, new Point(0, 0), 1, g =>
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // 알파 1: 눈에 안 보이지만 빈 곳도 마우스를 받는다(알파 0 은 클릭이 뒤로 빠진다)
            // (FillRectangle 은 안티앨리어싱 때문에 가장자리가 반만 칠해져 알파 0 이 된다)
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            if (hovering)
            {
                using var wash = new SolidBrush(env.Light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255));
                using var path = Rounded(new RectangleF(1, 1, size.Width - 2, size.Height - 2), px(5));
                g.FillPath(wash, path);
            }
            float mid = height / 2f;
            foreach (var (left, color, kind, item) in layout)
            {
                if (kind == "badge")
                {
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, left, mid - badge / 2f, badge, badge);
                    using var font = new Font("Segoe UI", badge * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var format = new StringFormat(StringFormat.GenericTypographic)
                        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using var ink = new SolidBrush(Palette.Background);
                    g.DrawString("!", font, ink, new RectangleF(left, mid - badge / 2f, badge, badge), format);
                }
                else if (kind == "sep")
                {
                    using var pen = new SolidBrush(track);
                    g.FillRectangle(pen, left, mid - ring * 0.3f, separator, ring * 0.6f);
                }
                else if (kind == "dot")
                {
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, left, mid - dot / 2f, dot, dot);
                }
                else
                {
                    var box = new RectangleF(left + stroke / 2f, mid - ring / 2f + stroke / 2f, ring - stroke, ring - stroke);
                    using (var pen = new Pen(track, stroke)) g.DrawEllipse(pen, box);
                    if (item.Left is int value && value > 0)
                    {
                        using var pen = new Pen(Status(value, env.Light), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawArc(pen, box, -90, 360f * Math.Min(100, value) / 100f);
                    }
                    var label = item.Left?.ToString() ?? "–";
                    float fontPx = ring * 0.36f * (label.Length >= 3 ? 0.8f : 1f);
                    using var font = new Font("Segoe UI", Math.Max(6f, fontPx), FontStyle.Bold, GraphicsUnit.Pixel);
                    using var format = new StringFormat(StringFormat.GenericTypographic)
                        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    using var brush = new SolidBrush(text);
                    g.DrawString(label, font, brush, new RectangleF(left, mid - ring / 2f, ring, ring), format);
                }
            }
        });
        if (!ok) return Conceal(broken = T("작업 표시줄에 그리지 못했습니다") + $" ({LayeredSurface.LastFailure})");
        if (hover >= hits.Count) Hover(null);
        return null;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // MARK: 마우스를 올리면 그 원의 이름·남은 %·초기화 시각

    /// at: 마우스 자리(null 이면 떠남). 바탕 강조는 마우스가 올라 있는 동안만, 누른 뒤에는 떠날 때까지 끈다
    void Hover(Point? at)
    {
        bool wasHovering = hovering;
        hovering = at != null && !pressed;
        if (at == null) pressed = false;
        if (hovering) leaveTimer.Start();
        int index = at is { } p ? hits.FindIndex(h => h.Bounds.Contains(p)) : -1;
        if (index != hover)
        {
            hover = index;
            HideTip();
            if (index >= 0) tipTimer.Start();
        }
        if (wasHovering != hovering) Render();
    }

    void ShowTip()
    {
        if (hover < 0 || hover >= hits.Count || !IsHandleCreated) return;
        GetWindowRect(Taskbar, out var bar);
        var ring = RectangleToScreen(hits[hover].Bounds);
        tip.ShowAbove(hits[hover].Tip, ring, bar.Top, (int)drawnFor.Dpi);
    }

    void HideTip()
    {
        tipTimer.Stop();
        if (tip.Visible) tip.Hide();
    }

    /// 작업 표시줄 위에 뜨는 작은 설명 창(초점을 뺏지 않는다)
    sealed class TipForm : Form
    {
        string message = "";
        Font? font;

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Palette.Background;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;  // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public void ShowAbove(string text, Rectangle target, int taskbarTop, int dpi)
        {
            message = text;
            font?.Dispose();
            font = new Font("Malgun Gothic", 12f * dpi / 96, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(message, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int padX = 10 * dpi / 96, padY = 6 * dpi / 96, gap = 8 * dpi / 96;
            var size = new Size(measured.Width + padX * 2, measured.Height + padY * 2);
            var area = Screen.FromRectangle(target).Bounds;
            int x = Math.Clamp(target.Left + target.Width / 2 - size.Width / 2, area.Left, area.Right - size.Width);
            int y = Math.Max(area.Top, Math.Min(target.Top, taskbarTop) - gap - size.Height);
            Bounds = new Rectangle(new Point(x, y), size);
            if (!Visible) Show();
            var round = 2;  // Windows 11 둥근 모서리
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (font == null) return;
            TextRenderer.DrawText(e.Graphics, message, font, ClientRectangle, Palette.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) font?.Dispose();
            base.Dispose(disposing);
        }
    }
}
