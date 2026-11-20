using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static AIUsage.L10n;

namespace AIUsage;

/// 세션 한 줄: 「● 이름 ………… ◌ 작업 중 12분」. 떠 있는 창과 작업 표시줄 위 패널이 같이 쓴다
static class SessionRow
{
    public const int MaxRows = 8;

    public static Color Tint(SessionState state) => state switch
    {
        SessionState.Waiting => Palette.Waiting,
        SessionState.Working => Palette.Working,
        SessionState.Done => Palette.Done,
        _ => Palette.Tertiary,
    };

    /// 보일 줄: 8개까지, 넘으면 마지막 줄에 「외 N개」
    /// 마우스가 올라간 줄: 둥근 밝은 바탕
    public static void Highlight(Graphics g, Rectangle row, float radius)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
        using var path = new GraphicsPath();
        float d = Math.Max(1, radius * 2);
        path.AddArc(row.X, row.Y, d, d, 180, 90);
        path.AddArc(row.Right - d, row.Y, d, d, 270, 90);
        path.AddArc(row.Right - d, row.Bottom - d, d, d, 0, 90);
        path.AddArc(row.X, row.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    public static (List<AgentSession> Shown, int Hidden) Visible(List<AgentSession> sessions) =>
        sessions.Count <= MaxRows ? (sessions, 0) : (sessions.Take(MaxRows - 1).ToList(), sessions.Count - MaxRows + 1);

    /// text(글, 글꼴, 칸, 색, 정렬) 와 measure(글, 글꼴) 는 그리는 쪽(떠 있는 창은 투명 배경일 때 GDI+)에 맞춘다
    public static void Draw(Graphics g, AgentSession s, Rectangle row, DateTimeOffset now, float spin, Font nameFont, Font statusFont,
                            Action<string, Font, Rectangle, Color, TextFormatFlags> text, Func<string, Font, int> measure)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        float unit = row.Height / 16f;
        int dot = Math.Max(4, (int)Math.Round(6 * unit));
        using (var brush = new SolidBrush(s.Tool == SessionTool.Claude ? Palette.Claude : Palette.ChatGPT))
            g.FillEllipse(brush, row.Left, row.Top + (row.Height - dot) / 2f, dot, dot);

        var tint = Tint(s.State);
        var status = s.StatusText(now);
        int statusWidth = measure(status, statusFont);
        int icon = (int)Math.Round(11 * unit), gap = (int)Math.Round(4 * unit);
        int statusX = row.Right - statusWidth;
        int iconX = statusX - gap - icon;
        text(status, statusFont, new Rectangle(statusX, row.Top, statusWidth, row.Height), tint, flags | TextFormatFlags.Right);

        var box = new RectangleF(iconX + unit, row.Top + (row.Height - icon) / 2f + unit, icon - 2 * unit, icon - 2 * unit);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        switch (s.State)
        {
            case SessionState.Working:  // 도는 고리
                using (var pen = new Pen(tint, Math.Max(1.5f, 1.6f * unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, box, spin, 270);
                break;
            case SessionState.Waiting:  // 주황 동그라미 안 「!」
                using (var brush = new SolidBrush(tint)) g.FillEllipse(brush, box);
                text("!", statusFont, Rectangle.Round(box), Palette.Background, flags | TextFormatFlags.HorizontalCenter);
                break;
            case SessionState.Done:     // 체크
                using (var pen = new Pen(tint, Math.Max(1.5f, 1.7f * unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, [new PointF(box.Left + box.Width * 0.1f, box.Top + box.Height * 0.55f),
                                      new PointF(box.Left + box.Width * 0.4f, box.Top + box.Height * 0.85f),
                                      new PointF(box.Right - box.Width * 0.05f, box.Top + box.Height * 0.2f)]);
                break;
        }
        g.SmoothingMode = old;

        int nameX = row.Left + dot + (int)Math.Round(6 * unit);
        text(s.Name, nameFont, new Rectangle(nameX, row.Top, Math.Max(0, iconX - gap * 2 - nameX), row.Height), Palette.Text,
             flags | TextFormatFlags.EndEllipsis);
    }
}

/// 세션 줄을 누르면 그 대화를 연다: 데스크톱 앱 세션은 앱의 링크(claude://…, codex://threads/…)로 그 대화를,
/// 링크가 없는 터미널 세션은 그 프로세스를 띄운 터미널 창을 앞으로(Windows Terminal 은 어느 탭인지까지는 못 고른다)
static class SessionOpener
{
    public static void Open(AgentSession s)
    {
        if (s.Link is { } link)
        {
            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
        if (s.Pid is { } pid && TerminalWindow(pid) is { } window)
        {
            if (IsIconic(window)) ShowWindow(window, 9);  // SW_RESTORE
            SetForegroundWindow(window);
        }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr HeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    /// 그 프로세스와 부모들 가운데 보이는 창이 있는 첫 프로세스의 창(터미널). 8대까지 올라간다
    static IntPtr? TerminalWindow(int pid)
    {
        var parents = Parents();
        for (int i = 0, p = pid; i < 8 && p > 4; i++)
        {
            try
            {
                using var process = Process.GetProcessById(p);
                if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle)) return process.MainWindowHandle;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
            if (!parents.TryGetValue(p, out var parent) || parent == p) return null;
            p = parent;
        }
        return null;
    }

    static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);  // TH32CS_SNAPPROCESS
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
}

/// 간단 모드에서 게이지를 한 번 누르면 작업 표시줄 바로 위로 올라오는 세션 패널. 줄을 누르면 그 대화를 열고 닫힌다. 바깥을 누르거나 Esc 로 닫는다
sealed class SessionPopup : Form
{
    const int PadX = 12, PadY = 10, HeaderH = 18, RowH = 18, Gap = 4, Width0 = 360;

    List<AgentSession> sessions = [];
    DateTimeOffset now = DateTimeOffset.Now;
    float spin;
    int dpi = 96;
    Rectangle anchor;
    int taskbarTop;
    Font? fontName, fontRow, fontSmall;
    DateTimeOffset closedAt = DateTimeOffset.MinValue;
    readonly System.Windows.Forms.Timer slide = new() { Interval = 15 };
    int slideStep;
    const int SlideSteps = 8;
    readonly List<(Rectangle Row, AgentSession Session)> hits = [];

    AgentSession? SessionAt(Point client) => hits.FirstOrDefault(h => h.Row.Contains(client)).Session;
    string? hoverId;  // 마우스가 올라간 세션 줄

    void HoverRow(Point? at)
    {
        var id = at is { } p ? SessionAt(p)?.Id : null;
        Cursor = id != null ? Cursors.Hand : Cursors.Default;
        if (id == hoverId) return;
        hoverId = id;
        Invalidate();
    }

    public SessionPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += (_, _) => Close();
        slide.Tick += (_, _) => Slide();
        MouseMove += (_, e) => HoverRow(e.Location);
        MouseLeave += (_, _) => HoverRow(null);
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || SessionAt(e.Location) is not { } session) return;
            Close();
            SessionOpener.Open(session);
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW(작업 표시줄·Alt+Tab 에 안 보임)
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    /// 닫기 = 숨기기(다시 열 때 그대로 쓴다)
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None)
        {
            e.Cancel = true;
            if (Visible) { Hide(); closedAt = DateTimeOffset.Now; }
        }
        base.OnFormClosing(e);
    }

    /// 게이지를 누르는 바람에 방금 닫혔으면(바깥 클릭으로 닫힘) 다시 열지 않는다
    public bool JustClosed => DateTimeOffset.Now - closedAt < TimeSpan.FromMilliseconds(400);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { slide.Dispose(); foreach (var f in new[] { fontName, fontRow, fontSmall }) f?.Dispose(); }
        base.Dispose(disposing);
    }

    int S(double v) => (int)Math.Round(v * dpi / 96.0);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    public void ShowAbove(Rectangle gauge, int barTop, int screenDpi, List<AgentSession> list, DateTimeOffset at)
    {
        anchor = gauge;
        taskbarTop = barTop;
        if (screenDpi != dpi || fontName == null)
        {
            dpi = screenDpi;
            foreach (var f in new[] { fontName, fontRow, fontSmall }) f?.Dispose();
            fontName = new Font("Malgun Gothic", S(13), FontStyle.Bold, GraphicsUnit.Pixel);
            fontRow = new Font("Malgun Gothic", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            fontSmall = new Font("Malgun Gothic", S(11), FontStyle.Regular, GraphicsUnit.Pixel);
        }
        hoverId = null;
        SetSessions(list, at, spin);
        // 아래에서 조금 올라오며 나타난다
        slideStep = 0;
        Opacity = 0;
        Location = new Point(Left, Top + S(14));
        Show();
        var round = 2;  // Windows 11 둥근 모서리
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        SetForegroundWindow(Handle);
        Activate();
        slide.Start();
    }

    void Slide()
    {
        slideStep++;
        var t = Math.Min(1.0, slideStep / (double)SlideSteps);
        var ease = 1 - Math.Pow(1 - t, 3);
        Opacity = ease;
        Location = new Point(Left, FinalTop + (int)Math.Round(S(14) * (1 - ease)));
        if (t >= 1) slide.Stop();
    }

    int FinalTop => Math.Min(anchor.Top, taskbarTop) - S(8) - Height;

    /// 3초마다 새 목록, 그리고 도는 고리
    public void SetSessions(List<AgentSession> list, DateTimeOffset at, float angle)
    {
        sessions = list;
        now = at;
        spin = angle;
        var (shown, hidden) = SessionRow.Visible(sessions);
        int rows = Math.Max(1, shown.Count + (hidden > 0 ? 1 : 0));
        var size = new Size(S(Width0), S(PadY) * 2 + S(HeaderH) + rows * (S(Gap) + S(RowH)));
        var area = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Right - size.Width, area.Left, Math.Max(area.Left, area.Right - size.Width));
        // 아래 끝(작업 표시줄 바로 위)을 그대로 두고 위로 늘어난다. 올라오는 중이면 높이는 Slide 가 맞춘다
        var bounds = new Rectangle(x, slide.Enabled ? Top : Math.Min(anchor.Top, taskbarTop) - S(8) - size.Height, size.Width, size.Height);
        if (Bounds != bounds) Bounds = bounds;
        Invalidate();
    }

    public void Spin(float angle)
    {
        spin = angle;
        if (Visible && sessions.Any(s => s.State == SessionState.Working)) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (fontName == null) return;
        var g = e.Graphics;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        int left = S(PadX), right = Width - S(PadX), y = S(PadY);
        TextRenderer.DrawText(g, T("세션"), fontName, new Rectangle(left, y, right - left, S(HeaderH)), Palette.Text, flags);
        var counts = string.Join(" · ", new[]
        {
            (SessionState.Waiting, T("입력 필요")), (SessionState.Working, T("작업 중")),
        }.Select(p => (p.Item2, sessions.Count(s => s.State == p.Item1))).Where(p => p.Item2 > 0).Select(p => $"{p.Item1} {p.Item2}"));
        TextRenderer.DrawText(g, counts, fontSmall!, new Rectangle(left, y, right - left, S(HeaderH)), Palette.Tertiary, flags | TextFormatFlags.Right);
        y += S(HeaderH);
        var (shown, hidden) = SessionRow.Visible(sessions);
        hits.Clear();
        if (shown.Count == 0)
        {
            y += S(Gap);
            TextRenderer.DrawText(g, T("열린 세션이 없습니다"), fontRow!, new Rectangle(left, y, right - left, S(RowH)), Palette.Secondary, flags);
            return;
        }
        foreach (var s in shown)
        {
            y += S(Gap);
            hits.Add((new Rectangle(0, y - S(Gap) / 2, Width, S(RowH) + S(Gap)), s));
            if (s.Id == hoverId) SessionRow.Highlight(g, new Rectangle(S(5), y - S(Gap) / 2, Width - S(10), S(RowH) + S(Gap)), S(5));
            SessionRow.Draw(g, s, new Rectangle(left, y, right - left, S(RowH)), now, spin, fontRow!, fontSmall!,
                (text, font, rect, color, f) => TextRenderer.DrawText(g, text, font, rect, color, f),
                (text, font) => TextRenderer.MeasureText(g, text, font, Size.Empty, flags).Width);
            y += S(RowH);
        }
        if (hidden > 0)
            TextRenderer.DrawText(g, T("외 {0}개", hidden), fontSmall!, new Rectangle(left, y + S(Gap), right - left, S(RowH)), Palette.Tertiary, flags);
    }
}
