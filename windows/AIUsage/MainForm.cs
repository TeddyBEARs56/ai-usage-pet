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

    readonly UsageHistory history = new();
    readonly LimitAlerts alerts = new();
    readonly NotifyIcon notification = new();
    readonly System.Windows.Forms.Timer balloonTimer = new() { Interval = 12_000 };
    SourceScan scanned = new();
    UsageAccount? activeUser;
    TrendForm? trend;
    bool scanning, rescanOnRefresh;
    readonly BalloonPresenter balloons;
    bool showModels = Settings.Read(Settings.ShowModelLimits) == 1;
    bool modelAlerts = Settings.Read(Settings.ModelLimitAlerts) == 1;

    List<UsageEvent> LiveRows() => UsageEvent.LiveRows(claudeSnap,codex);

    void SyncUsage(bool allowScan = true)
    {
        var n = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var user = UsageAccount.Read(n);
        var changed = user != activeUser;
        activeUser = user;
        history.RecordLive(LiveRows(),user,n);
        if (trend != null)
        {
            trend.SetData(TrendResult.Calculate(history.VisibleRows,user,n),n,order);
            if (allowScan && (changed || rescanOnRefresh)) ScanHistory();
        }
        SendAlert();
    }

    async void ScanHistory(bool retryAccountChange = true)
    {
        if (scanning) return;
        scanning = true;
        rescanOnRefresh = false;
        bool accountChanged = false;
        var n = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var user = UsageAccount.Read(n);
        var next = scanned.Copy();
        try
        {
            var rows = await Task.Run(() => next.Read(user,n));
            if (IsDisposed) return;
            n = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            accountChanged = UsageAccount.Read(n) != user;
            if (!accountChanged && history.Merge(rows,n)) scanned = next;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { scanning = false; }
        if (IsDisposed || trend == null) return;
        SyncUsage(allowScan: false);
        if (accountChanged || activeUser != user)
        {
            if (retryAccountChange) ScanHistory(retryAccountChange: false);
            else rescanOnRefresh = true;
        }
    }
    void OpenTrend()
    {
        if (trend == null)
        {
            trend = new TrendForm();
            trend.FormClosed += (_,_) => trend = null;
            trend.Show();
        }
        if (trend.WindowState == FormWindowState.Minimized) trend.WindowState = FormWindowState.Normal;
        trend.Activate();
        SyncUsage();
        ScanHistory();
    }
    void EndBalloon()
    {
        if (!balloons.Dismiss()) return;
        balloonTimer.Stop();
        SendAlert();
    }
    void SendAlert()
    {
        if (IsDisposed) return;
        var enabled = Settings.LimitAlerts;
        if (balloons.Busy)
        {
            alerts.ApplyPreferences(enabled, modelAlerts);
            return;
        }
        var n = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var c = alerts.NextLive(claudeSnap, codex, n, enabled, modelAlerts);
        if (c == null) return;
        if (!Settings.LimitAlerts || (c.Notice.Model != null && !modelAlerts) ||
            UsageAccount.Read(n).Keys().GetValueOrDefault(c.Notice.Provider) != c.Notice.Account) return;
        var before = alerts.State;
        if (!alerts.Save(c.State)) return;
        try
        {
            balloons.Show(c.Notice);
            balloonTimer.Start();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            alerts.Save(before);
        }
    }
    void ToggleAlerts()
    {
        Settings.LimitAlerts = !Settings.LimitAlerts;
        if (Settings.LimitAlerts) SendAlert();
        else { alerts.Cancel(); balloonTimer.Stop(); balloons.Dismiss(); }
    }

    void ToggleModelAlerts()
    {
        if (!Settings.LimitAlerts) return;
        modelAlerts = Settings.Save(Settings.ModelLimitAlerts, modelAlerts ? 0 : 1, modelAlerts ? 1 : 0) == 1;
        SendAlert();
    }

    void ToggleModelDisplay()
    {
        showModels = Settings.Save(Settings.ShowModelLimits, showModels ? 0 : 1, showModels ? 1 : 0) == 1;
        FitSize();
        Redraw();
    }

    readonly Mutex mutex;
    readonly bool afterInstall;
    readonly ClaudeReader claude = new();
    readonly CodexReader codex = new();
    readonly System.Windows.Forms.Timer localTimer = new() { Interval = 15_000 };
    readonly System.Windows.Forms.Timer serverTimer = new() { Interval = 300_000 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)Updater.FirstDelay.TotalMilliseconds };
    // Claude Code 가 응답하면(대화 기록이 바뀌면) 곧 `claude /usage` 를 묻고, 그 밖에는 5분마다 — 5초마다 때인지 본다
    readonly System.Windows.Forms.Timer usageTimer = new() { Interval = 5_000 };
    readonly ClaudeActivityWatcher activity = new();
    readonly ContextMenuStrip menu = new();
    ProviderSnapshot claudeSnap = new(), codexSnap = new();
    Pane[] order = Settings.Order;
    DateTimeOffset now = DateTimeOffset.Now;
    DateTimeOffset? usageAskedAt;
    HashSet<string> measuredNames = new(StringComparer.Ordinal);
    bool fetching, updating, useRegion, askingUsage;
    Font? fontName, fontSmall, fontRow, fontPercent;

    int backgroundLevel = Settings.Read(Settings.Background);
    int windowPercent = Settings.Read(Settings.WindowSize);
    int textPercent = Settings.Read(Settings.TextSize);
    int savedBackground;
    bool layeredFailed, dragging, wasMinimized;
    Point? anchor;
    int labelWidth, percentWidth, resetWidth;

    public MainForm(Mutex mutex, bool afterInstall)
    {
        savedBackground = backgroundLevel;
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
        notification.Icon = Icon ?? SystemIcons.Application;
        balloons = new BalloonPresenter(visible => notification.Visible = visible,
            notice => notification.ShowBalloonTip(10_000,notice.Title,notice.Body ?? " ",ToolTipIcon.Info));
        notification.BalloonTipClosed += (_,_) => EndBalloon();
        balloonTimer.Tick += (_,_) => EndBalloon();
        ContextMenuStrip = menu;
        menu.Opening += OnMenuOpening;
        MouseDown += OnMouseDownDrag;
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RefreshAll(); };
        localTimer.Tick += (_, _) => RefreshLocal();
        serverTimer.Tick += (_, _) => RefreshServer();
        usageTimer.Tick += (_, _) => CheckClaudeUsage();
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = (int)Updater.Interval.TotalMilliseconds;  // 처음은 켠 뒤 1분, 그 뒤 6시간마다
            CheckForUpdate(manual: false);
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00040000;  // WS_EX_NOACTIVATE(초점 안 뺏음) | WS_EX_APPWINDOW(작업 표시줄 단추)
            if (backgroundLevel != 0) cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
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
        Redraw();
        localTimer.Start();
        serverTimer.Start();
        updateTimer.Start();
        usageTimer.Start();
        RefreshServer();
        CheckClaudeUsage();
        if (afterInstall) BeginInvoke(new Action(() => ClaudeLink.Ask(fromMenu: false)));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        localTimer.Dispose();
        serverTimer.Dispose();
        updateTimer.Dispose();
        usageTimer.Dispose();
        activity.Dispose();
        balloonTimer.Dispose(); notification.Dispose(); trend?.Close();
        foreach (var font in new[] { fontName, fontSmall, fontRow, fontPercent }) font?.Dispose();
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
            if (IsDisposed) return;
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
            if (IsDisposed) return;
            Updater.Launch(exe);
            Close();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or System.Text.Json.JsonException or Win32Exception)
        {
            Updater.Log($"실패: {e.Message}");
            if (manual && !IsDisposed)
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
        var codexSwitched = codex.ReadLog(now);
        codexSnap = codex.Current;
        SyncUsage();
        FitSize();
        Redraw();
        if (codexSwitched) RefreshServer();  // ChatGPT 계정이 바뀌었다: 5분 기다리지 않고 새 계정 값을 묻는다
    }

    async void RefreshServer(bool retry = true)
    {
        if (fetching) return;
        fetching = true;
        try
        {
            var owner = CodexReader.CurrentAccount();
            var result = await Task.Run(CodexReader.FetchServerAsync);
            if (IsDisposed) return;
            if (!codex.ApplyServer(result,owner))
            {
                codexSnap = codex.Current;
                SyncUsage();
                FitSize();
                Redraw();
                if (retry) BeginInvoke(new Action(() => RefreshServer(false)));
                return;
            }
        }
        finally
        {
            fetching = false;
        }
        if (IsDisposed) return;
        codexSnap = codex.Current;
        SyncUsage();
        FitSize();
        Redraw();
    }

    void RefreshAll()
    {
        RefreshLocal();
        RefreshServer();
        AskClaudeUsage("새로고침");
    }

    /// 지금 `claude /usage` 를 물을 때인지 본다(처음·Claude Code 응답 직후·5분마다)
    void CheckClaudeUsage()
    {
        if (ClaudeUsageCommand.DisabledReason() != null) return;
        activity.Ensure();
        if (ClaudeActivityWatcher.Due(DateTimeOffset.Now, usageAskedAt, activity.Pending()) is { } reason) AskClaudeUsage(reason);
    }

    async void AskClaudeUsage(string reason)
    {
        if (askingUsage || ClaudeUsageCommand.DisabledReason() != null) return;
        askingUsage = true;
        usageAskedAt = DateTimeOffset.Now;
        activity.Take();
        try
        {
            await Task.Run(() => ClaudeUsageCommand.FetchAsync(reason));
        }
        finally
        {
            askingUsage = false;
        }
        if (!IsDisposed) RefreshLocal();
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && IsHandleCreated) BeginInvoke(new Action(RefreshAll));  // 절전에서 깨면 바로 새로
    }

    // MARK: 크기·위치

    int S(double value) => (int)Math.Round(value * DeviceDpi / 96.0);

    int Content(double points) => S(points * windowPercent / 100);
    int TextSize(double points) => Content(points * textPercent / 100);
    int DividerHeight => windowPercent == 100 ? 1 : Math.Max(1, Content(1));
    int HeaderHeight => TextSize(HeaderH);
    int RowHeight => TextSize(RowH);

    int Rows(ProviderSnapshot snap)
    {
        int count = snap.VisibleWindows(showModels).Count();
        return count == 0 ? 2 : count;
    }
    int SectionHeight(ProviderSnapshot snap) => HeaderHeight + Rows(snap) * (Content(Gap) + RowHeight);

    void FitSize()
    {
        if (WindowState == FormWindowState.Minimized || fontName == null) return;
        MeasureColumns();
        int width = Content(BaseWidth) + labelWidth - Content(LabelW)
            + percentWidth - Content(PercentW) + resetWidth - Content(ResetW);
        var size = new Size(width, Content(PadY) * 2 + SectionHeight(claudeSnap)
            + Content(SectionGap) * 2 + DividerHeight + SectionHeight(codexSnap));
        bool resized = size != Size;
        var location = Location;
        if (!dragging && anchor is Point point)
        {
            var probe = new Point(point.X + S(10), point.Y + S(10));
            var area = Screen.FromPoint(probe).WorkingArea;
            location = new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - size.Width)),
                Math.Max(area.Top, Math.Min(point.Y, area.Bottom - size.Height)));
        }
        var bounds = new Rectangle(location, size);
        if (Bounds != bounds) Bounds = bounds;
        if (resized && useRegion) ApplyRegion();
    }

    void PlaceWindow(bool reset)
    {
        if (reset) Settings.ClearPosition();
        if (!reset && Settings.LoadPosition() is { } saved
            && Screen.AllScreens.Any(s => s.WorkingArea.Contains(saved.X + S(10), saved.Y + S(10))))
            anchor = saved;
        else
        {
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            anchor = new Point(area.Right - Width - S(16), area.Top + S(16));
        }
        FitSize();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated) BeginInvoke(new Action(() => { FitSize(); Redraw(); }));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (fontName != null)
        {
            MakeFonts();
            FitSize();
            Redraw();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (fontName != null)
            BeginInvoke(new Action(() =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                ApplyCorners();
                FitSize();
                Redraw();
            }));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool restored = wasMinimized && WindowState != FormWindowState.Minimized;
        wasMinimized = WindowState == FormWindowState.Minimized;
        if (restored) { FitSize(); Redraw(); }
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void OnMouseDownDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
        var start = Location;
        var pointer = Cursor.Position;
        dragging = true;
        try
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, IntPtr.Zero); // WM_NCLBUTTONDOWN + HTCAPTION, returns on release
        }
        finally { dragging = false; }
        if (Location != start && Cursor.Position != pointer)
        {
            anchor = Location;
            Settings.SavePosition(Location);
        }
        FitSize();
    }

    void ApplyCorners()
    {
        if (backgroundLevel != 0)
        {
            useRegion = false;
            Region = null;
            int square = 1, noBorder = unchecked((int)0xfffffffe);
            DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int));
            DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            return;
        }
        var round = 2;  // DWMWCP_ROUND: Windows 11 은 모서리를 부드럽게 둥글린다
        useRegion = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) != 0;  // Windows 10 은 직접 깎는다
        var border = ColorTranslator.ToWin32(Palette.Background);  // Windows 11 의 밝은 테두리 대신 배경색
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        if (useRegion) ApplyRegion();
    }

    void ApplyRegion()
    {
        using var path = Rounded(new RectangleF(0, 0, Width, Height), Content(10));
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
        fontName = new Font(face, TextSize(13), FontStyle.Bold, GraphicsUnit.Pixel);
        fontSmall = new Font(face, TextSize(11), FontStyle.Regular, GraphicsUnit.Pixel);
        fontRow = new Font(face, TextSize(12), FontStyle.Regular, GraphicsUnit.Pixel);
        fontPercent = new Font(face, TextSize(12), FontStyle.Bold, GraphicsUnit.Pixel);
        MeasureColumns(force: true);
    }

    static StringFormat LayerTextFormat(TextFormatFlags flags)
    {
        using var template = StringFormat.GenericTypographic;
        var format = (StringFormat)template.Clone();
        format.Alignment = flags.HasFlag(TextFormatFlags.Right) ? StringAlignment.Far : StringAlignment.Near;
        format.LineAlignment = flags.HasFlag(TextFormatFlags.VerticalCenter) ? StringAlignment.Center : StringAlignment.Near;
        format.FormatFlags &= ~(StringFormatFlags.NoClip | StringFormatFlags.NoWrap);
        if (flags.HasFlag(TextFormatFlags.SingleLine)) format.FormatFlags |= StringFormatFlags.NoWrap;
        format.Trimming = flags.HasFlag(TextFormatFlags.EndEllipsis) ? StringTrimming.EllipsisCharacter : StringTrimming.None;
        return format;
    }

    static int MeasureLayerText(Graphics g, string text, Font font)
    {
        using var format = LayerTextFormat(TextFlags);
        return (int)Math.Ceiling(g.MeasureString(text, font, PointF.Empty, format).Width);
    }

    void MeasureColumns(bool force = false)
    {
        var names = claudeSnap.VisibleWindows(showModels).Concat(codexSnap.VisibleWindows(showModels))
            .Select(w => w.Label).ToHashSet(StringComparer.Ordinal);
        if (!force && measuredNames.SetEquals(names)) return;
        measuredNames = names;
        using var g = CreateGraphics();
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int Column(int nominal, Font font, IEnumerable<string> samples)
        {
            int width = TextSize(nominal);
            foreach (var text in samples)
            {
                int gdi = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags).Width;
                width = Math.Max(width, Math.Max(gdi, MeasureLayerText(g, text, font)));
            }
            return width;
        }
        var baseline = Column(LabelW, fontRow!, [T("5시간"), T("주간")]);
        var visibleWidth = names.Count == 0 ? 0 : Column(0, fontRow!, names) + TextSize(2);
        labelWidth = Math.Clamp(visibleWidth, baseline, baseline + TextSize(64));
        percentWidth = Column(PercentW, fontPercent!, [T("{0}% 남음", 100)]);
        var resets = L10n.Culture.DateTimeFormat.AbbreviatedDayNames.Select(day => $"↺ {day} 23:59");
        resetWidth = Column(ResetW, fontSmall!, resets.Append("↺ 12/31").Append(T("초기화됨")));
    }

    int MeasureText(Graphics g, string text, Font font) => backgroundLevel == 0
        ? TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags).Width : MeasureLayerText(g, text, font);

    void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags)
    {
        if (rect.Width <= 0) return;
        if (backgroundLevel == 0)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags);
            return;
        }
        using var format = LayerTextFormat(flags);
        if (backgroundLevel == 3)
        {
            var shadow = rect;
            shadow.Offset(0, Math.Max(1, Content(1)));
            using var shade = new SolidBrush(Color.FromArgb(150, Color.Black));
            g.DrawString(text, font, shade, shadow, format);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format);
    }

    void Redraw()
    {
        if (!IsHandleCreated || IsDisposed || anchor == null || fontName == null || WindowState == FormWindowState.Minimized) return;
        if (backgroundLevel != 0)
        {
            byte alpha = backgroundLevel switch { 1 => 179, 2 => 122, _ => 66 };
            var sample = new Point(Width / 2, Content(PadY) / 2);
            if (LayeredSurface.Present(Handle, Size, sample, alpha, g =>
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var brush = new SolidBrush(Color.FromArgb(alpha, Palette.Background));
                using var path = Rounded(new RectangleF(0, 0, Width, Height), Content(10));
                g.FillPath(brush, path);
                DrawContent(g);
            })) return;

            layeredFailed = true;
            backgroundLevel = 0; // Keep the registry preference for the next launch.
            UpdateStyles();
            ApplyCorners();
        }
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (backgroundLevel == 0) base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (backgroundLevel == 0 && fontName != null) DrawContent(e.Graphics);
    }

    void DrawContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var y = Content(PadY);
        for (var i = 0; i < order.Length; i++)  // 고른 순서대로(「칸 순서」 메뉴), 칸 사이에 구분선
        {
            if (i > 0)
            {
                y += Content(SectionGap);
                using (var pen = new Pen(Palette.Divider, DividerHeight)) g.DrawLine(pen, Content(PadX), y, Width - Content(PadX), y);
                y += DividerHeight + Content(SectionGap);
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
        int left = Content(PadX), right = Width - Content(PadX), header = HeaderHeight;
        using (var brush = new SolidBrush(dot))
            g.FillEllipse(brush, left, top + (header - Content(DotSize)) / 2f, Content(DotSize), Content(DotSize));
        var x = left + Content(DotSize) + Content(6);
        var nameWidth = MeasureText(g, name, fontName!);
        DrawText(g, name, fontName!, new Rectangle(x, top, nameWidth, header), Palette.Text, TextFlags);
        x += nameWidth + Content(6);

        var age = Fmt.AgeText(snap.ObservedAt, now);
        var ageWidth = MeasureText(g, age, fontSmall!);
        DrawText(g, age, fontSmall!, new Rectangle(right - ageWidth, top, ageWidth, header), Palette.Tertiary, TextFlags | TextFormatFlags.Right);
        if (!string.IsNullOrEmpty(detail))
            DrawText(g, detail, fontSmall!, new Rectangle(x, top, Math.Max(0, right - ageWidth - Content(6) - x), header),
                                  Palette.Secondary, TextFlags | TextFormatFlags.EndEllipsis);

        var y = top + header;
        var windows = snap.VisibleWindows(showModels).ToList();
        if (windows.Count == 0)
        {
            y += Content(Gap);
            var height = RowHeight * 2 + Content(Gap);
            DrawText(g, snap.Message ?? T("값이 없습니다"), fontRow!, new Rectangle(left, y, right - left, height),
                                  Palette.Secondary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak
                                  | TextFormatFlags.EndEllipsis);
            return y + height;
        }
        foreach (var window in windows)
        {
            y += Content(Gap);
            DrawRow(g, window, left, right, y);
            y += RowHeight;
        }
        return y;
    }

    /// 「5시간 ▬▬▬── 41% 남음 ↺ 20:20」
    void DrawRow(Graphics g, LimitWindow window, int left, int right, int y)
    {
        var height = RowHeight;
        var leftPercent = window.LeftPercent(now);
        var tint = Palette.Status(leftPercent);
        DrawText(g, window.Label, fontRow!, new Rectangle(left, y, labelWidth, height), Palette.Secondary, TextFlags | TextFormatFlags.EndEllipsis);

        int resetX = right - resetWidth, percentX = resetX - Content(ColGap) - percentWidth;
        int barX = left + labelWidth + Content(ColGap), barWidth = Math.Max(Content(10), percentX - Content(ColGap) - barX);
        float barY = y + (height - Content(BarH)) / 2f;
        using (var track = new SolidBrush(Palette.Track)) FillCapsule(g, track, barX, barY, barWidth, Content(BarH));
        using (var fill = new SolidBrush(tint)) FillCapsule(g, fill, barX, barY, Math.Max(Content(3), barWidth * leftPercent / 100f), Content(BarH));

        DrawText(g, T("{0}% 남음", leftPercent), fontPercent!, new Rectangle(percentX, y, percentWidth, height), tint,
                              TextFlags | TextFormatFlags.Right);
        var reset = Fmt.ResetText(window.ResetsAt, now);
        if (reset.Length > 0 && reset != T("초기화됨")) reset = "↺ " + reset;
        DrawText(g, reset, fontSmall!, new Rectangle(resetX, y, resetWidth, height), Palette.Secondary,
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
        menu.Items.Add(T("사용 추이…"), null, (_, _) => OpenTrend());
        menu.Items.Add(T("위치 초기화"), null, (_, _) => PlaceWindow(reset: true));
        var orderMenu = new ToolStripMenuItem(T("칸 순서"));
        foreach (var choice in Settings.Orders)
            orderMenu.DropDownItems.Add(new ToolStripMenuItem(string.Join(" → ", choice.Select(Settings.Title)), null, (_, _) => ChooseOrder(choice))
            {
                Checked = choice.SequenceEqual(order),
            });
        menu.Items.Add(orderMenu);
        menu.Items.Add(new ToolStripMenuItem(T("모델별 한도 보이기 (Fable 등)"), null, (_, _) => ToggleModelDisplay()) { Checked = showModels });
        menu.Items.Add(AppearanceMenu());
        menu.Items.Add(LanguageMenu());
        menu.Items.Add(new ToolStripMenuItem(T("한도 알림"), null, (_, _) => ToggleAlerts()) { Checked = Settings.LimitAlerts });
        menu.Items.Add(new ToolStripMenuItem(T("모델별 한도도 알림"), null, (_, _) => ToggleModelAlerts())
            { Enabled = Settings.LimitAlerts, Checked = Settings.LimitAlerts && modelAlerts });
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

    ToolStripMenuItem AppearanceMenu()
    {
        var root = new ToolStripMenuItem(T("모양"));
        void AddGroup(Settings.PetSetting setting, string title, string[] labels, int current)
        {
            if (root.DropDownItems.Count != 0) root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem(title) { Enabled = false });
            for (int i = 0; i < labels.Length; i++)
            {
                int value = setting.Values[i];
                root.DropDownItems.Add(new ToolStripMenuItem(labels[i], null, (_, _) => ChooseAppearance(setting, value))
                    { Checked = value == current });
            }
        }
        string[] sizes = [T("작게"), T("보통"), T("크게"), T("아주 크게")];
        AddGroup(Settings.Background, T("배경 투명도"), [T("없음"), T("조금"), T("중간"), T("많이")], backgroundLevel);
        AddGroup(Settings.WindowSize, T("창 크기"), sizes, windowPercent);
        AddGroup(Settings.TextSize, T("글자 크기"), sizes, textPercent);
        return root;
    }

    void ChooseAppearance(Settings.PetSetting setting, int value)
    {
        if (setting == Settings.Background)
        {
            if ((layeredFailed && value != 0) || (value == backgroundLevel && value == savedBackground)) return;
            SetBackground(value);
            if (backgroundLevel != value) return; // Failed alpha check or submission.
            savedBackground = Settings.Save(setting, value, savedBackground);
            if (savedBackground != value && (!layeredFailed || savedBackground == 0)) SetBackground(savedBackground);
            return;
        }

        int previous = setting == Settings.WindowSize ? windowPercent : textPercent;
        if (value == previous) return;
        SetScale(setting, value);
        int stored = Settings.Save(setting, value, previous);
        if (stored != value) SetScale(setting, stored);
    }

    void SetBackground(int level)
    {
        backgroundLevel = level;
        UpdateStyles();
        ApplyCorners();
        Redraw();
    }

    void SetScale(Settings.PetSetting setting, int value)
    {
        if (setting == Settings.WindowSize) windowPercent = value;
        else textPercent = value;
        MakeFonts();
        FitSize();
        Redraw();
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
        MeasureColumns(force: true);
        RefreshAll();  // 값에 붙은 안내 글도 새 언어로 다시 만든다
    }

    void ChooseOrder(Pane[] choice)
    {
        order = choice;
        Settings.Order = choice;
        if (trend != null) SyncUsage();
        Redraw();
    }

    IEnumerable<string> DetailLines()
    {
        foreach (var (title, snap) in order.Select(p => p == Pane.Codex
                     ? (T("ChatGPT (Codex 한도)"), codexSnap)
                     : ($"Claude ({claudeSnap.Source ?? T("Claude Code 상태 표시줄")})", claudeSnap)))
        {
            yield return T("{0} · 마지막 값 {1}", title, snap.ObservedAt is { } at ? Fmt.FullTime(at) : T("없음"));
            foreach (var w in snap.VisibleWindows(showModels))
                yield return "    " + T("{0} {1}% 남음 · 초기화 {2}", w.Label, w.LeftPercent(now), w.ResetsAt is { } r ? Fmt.FullTime(r) : "-");
            // Claude 값이 오래됐고 데스크톱 앱 기록도 멈췄으면 다시 받게 하는 법을 알린다
            if (snap == claudeSnap && (snap.ObservedAt is not { } seen || now - seen >= ClaudeDesktopUsage.StaleAfter)
                && ClaudeDesktopUsage.StaleHint(now) is { } hint)
                yield return "    " + hint;
        }
        var usageError = ClaudeUsageCommand.DisabledReason() ?? ClaudeUsageCommand.LastError;
        yield return T("Claude /usage 확인 {0}", ClaudeUsageCommand.LastCheckedAt is { } u ? Fmt.Clock(u) : T("전"))
                     + (usageError != null ? " — " + usageError : "");
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
