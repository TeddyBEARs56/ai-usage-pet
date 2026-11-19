using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static AIUsage.L10n;

namespace AIUsage;

/// Codex 펫처럼 늘 다른 창 위에 떠 있는 작은 창. 눌러도 지금 쓰는 프로그램의 초점을 뺏지 않고,
/// 작업 표시줄 단추로 켜고 끈다(오른쪽 클릭 → 창 닫기).
sealed class MainForm : Form
{
    // 96 DPI 기준 픽셀. S() 로 화면 배율을 곱한다.
    const int BaseWidth = 300, PadX = 12, PadY = 10, HeaderH = 18, RowH = 16, Gap = 5, SectionGap = 9;
    const int PercentW = 72, ResetW = 82, ColGap = 8, BarH = 5, DotSize = 8;
    /// 「5시간」「주간」 칸 너비 — 영어(Weekly)는 조금 넓게
    static int LabelW => L10n.Current == "en" ? 44 : 34;
    const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    readonly Mutex mutex;
    readonly bool afterInstall;
    readonly ClaudeReader claude = new();
    readonly CodexReader codex = new();
    readonly System.Windows.Forms.Timer localTimer = new() { Interval = 15_000 };
    readonly System.Windows.Forms.Timer serverTimer = new() { Interval = 300_000 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)Updater.FirstDelay.TotalMilliseconds };
    readonly ContextMenuStrip menu = new();
    ProviderSnapshot claudeSnap = new(), codexSnap = new();
    Pane[] order = Settings.Order;
    DateTimeOffset now = DateTimeOffset.Now;
    bool fetching, updating, useRegion;
    Font? fontName, fontSmall, fontRow, fontPercent;

    public MainForm(Mutex mutex, bool afterInstall)
    {
        this.mutex = mutex;
        this.afterInstall = afterInstall;
        Text = T("AI 사용량");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        TopMost = true;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background;
        DoubleBuffered = true;
        if (Installer.LoadIcon() is { } icon) Icon = icon;
        ContextMenuStrip = menu;
        menu.Opening += OnMenuOpening;
        MouseDown += OnMouseDownDrag;
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RefreshAll(); };
        localTimer.Tick += (_, _) => RefreshLocal();
        serverTimer.Tick += (_, _) => RefreshServer();
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = (int)Updater.Interval.TotalMilliseconds;  // 처음은 켠 뒤 1분, 그 뒤 6시간마다
            CheckForUpdate(manual: false);
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        DpiChanged += (_, _) => { MakeFonts(); FitSize(); Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00040000;  // WS_EX_NOACTIVATE(초점 안 뺏음) | WS_EX_APPWINDOW(작업 표시줄 단추)
            cp.ClassStyle |= 0x00020000;            // CS_DROPSHADOW(그림자)
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MakeFonts();
        ClaudeLink.RepairIfBroken();
        RefreshLocal();
        PlaceWindow(reset: false);
        ApplyCorners();
        localTimer.Start();
        serverTimer.Start();
        updateTimer.Start();
        RefreshServer();
        if (afterInstall) BeginInvoke(new Action(() => ClaudeLink.Ask(fromMenu: false)));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        localTimer.Dispose();
        serverTimer.Dispose();
        updateTimer.Dispose();
        base.OnFormClosed(e);
    }

    // MARK: 자동 업데이트

    /// 새 판이 있으면 받아서 그 exe 에 바꾸기를 맡기고 이 창은 닫는다(새 판이 다시 띄운다).
    /// 자동 확인은 조용히 하고, 메뉴에서 누른 확인만 결과를 알린다.
    async void CheckForUpdate(bool manual)
    {
        if (updating) return;
        if (!Installer.IsInstalled)
        {
            if (manual)
                MessageBox.Show(T("설치한 뒤에 업데이트할 수 있습니다.\n창 오른쪽 클릭 → 「설치(시작 메뉴에 등록)」"), T("AI 사용량"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!manual && !Settings.AutoUpdate) return;
        updating = true;
        try
        {
            Updater.CleanUp();
            var release = await Updater.LatestAsync();
            if (release is null || Updater.Compare(release.Version, Updater.Current) <= 0)
            {
                Updater.Log($"확인: 최신(올라온 최신 {release?.Version ?? "없음"})");
                if (manual)
                    MessageBox.Show(T("최신 버전입니다") + "\n\n" + T("지금 {0}", Updater.Current) + (release is null ? "" : " " + T("· 올라온 최신 {0}", release.Version)),
                                    T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Updater.Log($"새 판 {release.Version} 받기 시작");
            var exe = await Updater.DownloadAsync(release);
            Updater.Launch(exe);
            Close();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or System.Text.Json.JsonException or Win32Exception)
        {
            Updater.Log($"실패: {e.Message}");
            if (manual)
                MessageBox.Show(T("업데이트하지 못했습니다") + "\n\n" + e.Message, T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updating = false;
        }
    }

    // MARK: 값

    void RefreshLocal()
    {
        now = DateTimeOffset.Now;
        claudeSnap = claude.Read(now);
        var codexSwitched = codex.ReadLog();
        codexSnap = codex.Current;
        FitSize();
        Invalidate();
        if (codexSwitched) RefreshServer();  // ChatGPT 계정이 바뀌었다: 5분 기다리지 않고 새 계정 값을 묻는다
    }

    async void RefreshServer()
    {
        if (fetching) return;
        fetching = true;
        try
        {
            codex.ApplyServer(await Task.Run(CodexReader.FetchServerAsync));
        }
        finally
        {
            fetching = false;
        }
        if (IsDisposed) return;
        codexSnap = codex.Current;
        FitSize();
        Invalidate();
    }

    void RefreshAll()
    {
        RefreshLocal();
        RefreshServer();
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && IsHandleCreated) BeginInvoke(new Action(RefreshAll));  // 절전에서 깨면 바로 새로
    }

    // MARK: 크기·위치

    int S(double value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// 값이 없을 때 이유 글은 두 줄까지 접는다
    static int Rows(ProviderSnapshot snap) => snap.Windows.Count == 0 ? 2 : snap.Windows.Count;

    int SectionHeight(ProviderSnapshot snap) => S(HeaderH) + Rows(snap) * (S(Gap) + S(RowH));

    /// 내용 줄 수가 바뀌면 왼쪽 위 모서리를 그대로 두고 높이를 맞춘다
    void FitSize()
    {
        var size = new Size(S(BaseWidth), S(PadY) * 2 + SectionHeight(claudeSnap) + S(SectionGap) * 2 + 1 + SectionHeight(codexSnap));
        if (Size == size) return;
        Size = size;
        if (useRegion) ApplyRegion();
    }

    void PlaceWindow(bool reset)
    {
        if (!reset && Settings.LoadPosition() is { } saved
            && Screen.AllScreens.Any(s => s.WorkingArea.Contains(saved.X + S(10), saved.Y + S(10))))
        {
            Location = saved;
            return;
        }
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Location = new Point(area.Right - Width - S(16), area.Top + S(16));
        if (reset) Settings.SavePosition(Location);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void OnMouseDownDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 2, IntPtr.Zero);  // WM_NCLBUTTONDOWN + HTCAPTION: 제목 표시줄처럼 끌기(놓을 때 돌아온다)
        Settings.SavePosition(Location);
    }

    void ApplyCorners()
    {
        var round = 2;  // DWMWCP_ROUND: Windows 11 은 모서리를 부드럽게 둥글린다
        useRegion = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) != 0;  // Windows 10 은 직접 깎는다
        var border = ColorTranslator.ToWin32(Palette.Background);  // Windows 11 의 밝은 테두리 대신 배경색
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        if (useRegion) ApplyRegion();
    }

    void ApplyRegion()
    {
        using var path = Rounded(new RectangleF(0, 0, Width, Height), S(10));
        Region = new Region(path);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // MARK: 그리기

    void MakeFonts()
    {
        foreach (var font in new[] { fontName, fontSmall, fontRow, fontPercent }) font?.Dispose();
        const string face = "Malgun Gothic";
        fontName = new Font(face, S(13), FontStyle.Bold, GraphicsUnit.Pixel);
        fontSmall = new Font(face, S(11), FontStyle.Regular, GraphicsUnit.Pixel);
        fontRow = new Font(face, S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        fontPercent = new Font(face, S(12), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (fontName is null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var y = S(PadY);
        for (var i = 0; i < order.Length; i++)  // 고른 순서대로(「칸 순서」 메뉴), 칸 사이에 구분선
        {
            if (i > 0)
            {
                y += S(SectionGap);
                using (var pen = new Pen(Palette.Divider)) g.DrawLine(pen, S(PadX), y, Width - S(PadX), y);
                y += 1 + S(SectionGap);
            }
            if (order[i] == Pane.Codex)
            {
                var detail = string.Join(" · ", new[] { "Codex", codexSnap.Plan }.Where(s => !string.IsNullOrEmpty(s)));
                y = DrawSection(g, "ChatGPT", detail, Palette.ChatGPT, codexSnap, y);
            }
            else
            {
                y = DrawSection(g, "Claude", null, Palette.Claude, claudeSnap, y);
            }
        }
    }

    int DrawSection(Graphics g, string name, string? detail, Color dot, ProviderSnapshot snap, int top)
    {
        int left = S(PadX), right = Width - S(PadX), header = S(HeaderH);
        using (var brush = new SolidBrush(dot))
            g.FillEllipse(brush, left, top + (header - S(DotSize)) / 2f, S(DotSize), S(DotSize));
        var x = left + S(DotSize) + S(6);
        var nameWidth = TextRenderer.MeasureText(g, name, fontName, Size.Empty, TextFlags).Width;
        TextRenderer.DrawText(g, name, fontName, new Rectangle(x, top, nameWidth, header), Palette.Text, TextFlags);
        x += nameWidth + S(6);

        var age = Fmt.AgeText(snap.ObservedAt, now);
        var ageWidth = TextRenderer.MeasureText(g, age, fontSmall, Size.Empty, TextFlags).Width;
        TextRenderer.DrawText(g, age, fontSmall, new Rectangle(right - ageWidth, top, ageWidth, header), Palette.Tertiary, TextFlags | TextFormatFlags.Right);
        if (!string.IsNullOrEmpty(detail))
            TextRenderer.DrawText(g, detail, fontSmall, new Rectangle(x, top, Math.Max(0, right - ageWidth - S(6) - x), header),
                                  Palette.Secondary, TextFlags | TextFormatFlags.EndEllipsis);

        var y = top + header;
        if (snap.Windows.Count == 0)
        {
            y += S(Gap);
            var height = S(RowH) * 2 + S(Gap);
            TextRenderer.DrawText(g, snap.Message ?? T("값이 없습니다"), fontRow, new Rectangle(left, y, right - left, height),
                                  Palette.Secondary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak
                                  | TextFormatFlags.EndEllipsis);
            return y + height;
        }
        foreach (var window in snap.Windows)
        {
            y += S(Gap);
            DrawRow(g, window, left, right, y);
            y += S(RowH);
        }
        return y;
    }

    /// 「5시간 ▬▬▬── 41% 남음 ↺ 20:20」
    void DrawRow(Graphics g, LimitWindow window, int left, int right, int y)
    {
        var height = S(RowH);
        var leftPercent = window.LeftPercent(now);
        var tint = Palette.Status(leftPercent);
        TextRenderer.DrawText(g, window.Label, fontRow, new Rectangle(left, y, S(LabelW), height), Palette.Secondary, TextFlags);

        int resetX = right - S(ResetW), percentX = resetX - S(ColGap) - S(PercentW);
        int barX = left + S(LabelW) + S(ColGap), barWidth = Math.Max(S(10), percentX - S(ColGap) - barX);
        float barY = y + (height - S(BarH)) / 2f;
        using (var track = new SolidBrush(Palette.Track)) FillCapsule(g, track, barX, barY, barWidth, S(BarH));
        using (var fill = new SolidBrush(tint)) FillCapsule(g, fill, barX, barY, Math.Max(S(3), barWidth * leftPercent / 100f), S(BarH));

        TextRenderer.DrawText(g, T("{0}% 남음", leftPercent), fontPercent, new Rectangle(percentX, y, S(PercentW), height), tint,
                              TextFlags | TextFormatFlags.Right);
        var reset = Fmt.ResetText(window.ResetsAt, now);
        if (reset.Length > 0 && reset != T("초기화됨")) reset = "↺ " + reset;
        TextRenderer.DrawText(g, reset, fontSmall, new Rectangle(resetX, y, S(ResetW), height), Palette.Secondary,
                              TextFlags | TextFormatFlags.Right);
    }

    static void FillCapsule(Graphics g, Brush brush, float x, float y, float width, float height)
    {
        width = Math.Max(width, height);
        using var path = new GraphicsPath();
        path.AddArc(x, y, height, height, 90, 180);
        path.AddArc(x + width - height, y, height, height, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // MARK: 메뉴(창 오른쪽 클릭)

    void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        menu.Items.Clear();
        foreach (var line in DetailLines()) menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("지금 새로고침"), null, (_, _) => RefreshAll());
        menu.Items.Add(T("위치 초기화"), null, (_, _) => PlaceWindow(reset: true));
        var orderMenu = new ToolStripMenuItem(T("칸 순서"));
        foreach (var choice in Settings.Orders)
            orderMenu.DropDownItems.Add(new ToolStripMenuItem(string.Join(" → ", choice.Select(Settings.Title)), null, (_, _) => ChooseOrder(choice))
            {
                Checked = choice.SequenceEqual(order),
            });
        menu.Items.Add(orderMenu);
        menu.Items.Add(LanguageMenu());
        menu.Items.Add(new ToolStripMenuItem(T("Windows 시작할 때 자동으로 열기"), null, (_, _) => AutoStart.Set(!AutoStart.IsOn)) { Checked = AutoStart.IsOn });
        menu.Items.Add(new ToolStripMenuItem(T("Claude Code 상태 표시줄 연결"), null, (_, _) => { ClaudeLink.Ask(fromMenu: true); RefreshLocal(); })
        {
            Checked = ClaudeSettings.State() == ClaudeSettings.LinkState.Connected,
        });
        if (!Installer.IsInstalled)
            menu.Items.Add(T("설치(시작 메뉴에 등록)"), null, (_, _) => { if (Installer.InstallAndRelaunch(mutex)) Close(); });
        menu.Items.Add(new ToolStripMenuItem(T("업데이트 확인 (지금 {0})", Updater.Current), null, (_, _) => CheckForUpdate(manual: true))
        {
            Enabled = !updating,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("종료"), null, (_, _) => Close());
        e.Cancel = false;  // 항목을 여기서 채우므로 열리게 둔다
    }

    /// 「언어」 — 자동(Windows 표시 언어)과 언어 이름(각 언어로 적은 이름)
    ToolStripMenuItem LanguageMenu()
    {
        var item = new ToolStripMenuItem(T("언어 (Language)"));
        var choice = L10n.Choice;
        item.DropDownItems.Add(new ToolStripMenuItem(T("자동 (시스템 언어)"), null, (_, _) => ChooseLanguage("auto")) { Checked = choice == "auto" });
        item.DropDownItems.Add(new ToolStripSeparator());
        foreach (var code in L10n.Codes)
            item.DropDownItems.Add(new ToolStripMenuItem(L10n.NativeNames[code], null, (_, _) => ChooseLanguage(code)) { Checked = choice == code });
        return item;
    }

    void ChooseLanguage(string code)
    {
        if (code == L10n.Choice) return;
        try
        {
            L10n.Choose(code);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Text = T("AI 사용량");
        RefreshAll();  // 값에 붙은 안내 글도 새 언어로 다시 만든다
    }

    void ChooseOrder(Pane[] choice)
    {
        order = choice;
        Settings.Order = choice;
        Invalidate();
    }

    IEnumerable<string> DetailLines()
    {
        foreach (var (title, snap) in order.Select(p => p == Pane.Codex
                     ? (T("ChatGPT (Codex 한도)"), codexSnap)
                     : ($"Claude ({claudeSnap.Source ?? T("Claude Code 상태 표시줄")})", claudeSnap)))
        {
            yield return T("{0} · 마지막 값 {1}", title, snap.ObservedAt is { } at ? Fmt.FullTime(at) : T("없음"));
            foreach (var w in snap.Windows)
                yield return "    " + T("{0} {1}% 남음 · 초기화 {2}", w.Label, w.LeftPercent(now), w.ResetsAt is { } r ? Fmt.FullTime(r) : "-");
        }
        yield return T("Codex 서버 확인 {0}", codex.ServerCheckedAt is { } c ? Fmt.Clock(c) : T("전")) + (codex.ServerError is { } error ? " — " + error : "");
    }
}

static class Palette
{
    public static readonly Color Background = Color.FromArgb(0x1E, 0x1E, 0x20);
    public static readonly Color Text = Color.FromArgb(0xF2, 0xF2, 0xF4);
    public static readonly Color Secondary = Color.FromArgb(0xA1, 0xA1, 0xA6);
    public static readonly Color Tertiary = Color.FromArgb(0x6E, 0x6E, 0x73);
    public static readonly Color Track = Color.FromArgb(0x3A, 0x3A, 0x3D);
    public static readonly Color Divider = Color.FromArgb(0x2E, 0x2E, 0x31);
    public static readonly Color Claude = Color.FromArgb(0xD9, 0x77, 0x57);
    public static readonly Color ChatGPT = Color.FromArgb(0x10, 0xA3, 0x7F);

    /// 맥판과 같은 기준: 50% 이상 초록, 20% 이상 노랑, 그 아래 빨강
    public static Color Status(int left) => left >= 50 ? Color.FromArgb(0x30, 0xD1, 0x58)
        : left >= 20 ? Color.FromArgb(0xFF, 0xD6, 0x0A) : Color.FromArgb(0xFF, 0x45, 0x3A);
}
