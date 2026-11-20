// 설치(시작 메뉴 등록)·자동 실행·위치 기억·Claude Code 연결

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static AIUsage.L10n;

namespace AIUsage;

static class Installer
{
    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    internal static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AIUsage");
    public static string InstalledExe => Path.Combine(Dir, "AIUsage.exe");
    public static string StatusExe => Path.Combine(Dir, "AIUsageStatus.exe");
    static string IconFile => Path.Combine(Dir, "AIUsage.ico");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AI 사용량.lnk");

    public static bool IsInstalled =>
        string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static Icon? LoadIcon()
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream("AIUsage.ico");
        return stream is null ? null : new Icon(stream);
    }

    /// 설치되지 않은 곳에서 켜면 설치를 권한다(이미 설치돼 있으면 새 파일로 바꿀지 묻는다). 설치하고 다시 띄웠으면 true.
    public static bool OfferOnStart(Mutex mutex)
    {
        if (IsInstalled) return false;
        var update = File.Exists(InstalledExe);
        if (!update && Settings.InstallDeclined) return false;
        var question = update
            ? T("설치된 AI 사용량을 지금 이 파일로 바꿀까요?")
            : T("AI 사용량을 이 PC에 설치할까요?\n\n· 시작 메뉴에 「AI 사용량」이 생깁니다(작업 표시줄에 고정할 수 있습니다)\n· 설치 위치: {0}\n\n「아니요」를 누르면 설치하지 않고 이 파일로 실행합니다.", Dir);
        if (MessageBox.Show(question, T("AI 사용량"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            if (!update) Settings.InstallDeclined = true;
            return false;
        }
        return InstallAndRelaunch(mutex);
    }

    /// 실행 파일·아이콘·상태 표시줄 프로그램을 설치 폴더에 두고, 시작 메뉴에 등록한 뒤 설치된 쪽을 띄운다
    public static bool InstallAndRelaunch(Mutex mutex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.Copy(ExePath, InstalledExe, overwrite: true);
            ExtractFiles();
            CreateShortcut(Shortcut, InstalledExe, IconFile);
            if (AutoStart.IsOn) AutoStart.Set(true, InstalledExe);  // 자동 실행도 설치된 쪽을 가리키게
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            MessageBox.Show(T("설치하지 못했습니다: {0}\n\n설치하지 않고 이 파일로 실행합니다.", e.Message), T("AI 사용량"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        Process.Start(new ProcessStartInfo(InstalledExe, "--after-install") { UseShellExecute = true });
        return true;
    }

    /// 아이콘·상태 표시줄 프로그램을 설치 폴더에 꺼내 둔다. 상태 표시줄 프로그램이 마침 돌고 있어 잠겨 있으면 잠시 기다렸다 다시 한다.
    internal static void ExtractFiles()
    {
        Extract("AIUsage.ico", IconFile);
        Updater.Retry(() => Extract("AIUsageStatus.exe", StatusExe));
    }

    /// 상태 표시줄 프로그램을 설치 폴더에 꺼내 두고 경로를 돌려준다(설치하지 않고 쓸 때도 같은 곳)
    public static string EnsureStatusExe()
    {
        Directory.CreateDirectory(Dir);
        Extract("AIUsageStatus.exe", StatusExe);
        return StatusExe;
    }

    static void Extract(string resource, string target)
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException(T("{0} 가 실행 파일 안에 없습니다", resource));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        // 내용이 같으면 그대로 둔다(상태 표시줄이 쓰는 중이라 덮어쓸 수 없을 수 있다)
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(target, bytes);
    }

    static void CreateShortcut(string link, string target, string icon)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(T("WScript.Shell 을 찾지 못했습니다"));
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = icon + ",0";
            shortcut.Description = T("Claude·ChatGPT 사용 한도를 화면 위에 띄워 둔다");
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// Windows 를 켜고 로그인할 때 자동으로 연다(HKCU ... \Run)
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AIUsage";

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on, string? exe = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, $"\"{exe ?? Installer.ExePath}\"");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// 창 위치 등 작은 설정(HKCU\Software\AIUsage)
static class Settings
{
    const string Key = @"Software\AIUsage";

    public sealed record PetSetting(string Key, int Default, int[] Values);
    public static readonly PetSetting Background = new("petBackgroundLevel", 0, [0, 1, 2, 3]);
    public static readonly PetSetting WindowSize = new("petWindowPercent", 100, [85, 100, 115, 130]);
    public static readonly PetSetting TextSize = new("petTextPercent", 100, [90, 100, 115, 130]);
    public static readonly PetSetting ShowModelLimits = new("ShowModelLimits", 1, [0, 1]);
    public static readonly PetSetting ModelLimitAlerts = new("ModelLimitAlerts", 0, [0, 1]);

    public static bool TryRead(PetSetting setting, out int value)
    {
        value = setting.Default;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue(setting.Key) is int n && setting.Values.Contains(n)) value = n;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static int Read(PetSetting setting)
    {
        TryRead(setting, out int value);
        return value;
    }

    public static int Save(PetSetting setting, int value, int previous)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(setting.Key, value, RegistryValueKind.DWord);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return TryRead(setting, out int stored) ? stored : previous;
        }
    }

    public static void ClearPosition()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        key?.DeleteValue("Left", false);
        key?.DeleteValue("Top", false);
    }

    public static Point? LoadPosition()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("Left") is int x && key.GetValue("Top") is int y ? new Point(x, y) : null;
    }

    public static void SavePosition(Point point)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("Left", point.X, RegistryValueKind.DWord);
        key.SetValue("Top", point.Y, RegistryValueKind.DWord);
    }

    public static bool LimitAlerts
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("LimitAlerts") is not int v || v != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("LimitAlerts",value ? 1 : 0,RegistryValueKind.DWord);
        }
    }

    public static bool InstallDeclined
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("InstallDeclined") is int v && v == 1;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("InstallDeclined", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// 자동 업데이트(값 AutoUpdate). 값이 없으면 켬, 0 이면 끔.
    public static bool AutoUpdate
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("AutoUpdate") is not int v || v != 0;
        }
    }

    /// 「칸 순서」 메뉴에 나오는 순서 전부. 맨 앞이 기본(맥판의 세션 칸은 Windows 에 없다).
    public static readonly Pane[][] Orders = { new[] { Pane.Claude, Pane.Codex }, new[] { Pane.Codex, Pane.Claude } };

    public static string Title(Pane pane) => pane == Pane.Codex ? "ChatGPT" : "Claude";

    /// 창 안 칸 순서(값 Order = "claude,codex"). 모르는 값이면 기본 순서.
    public static Pane[] Order
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            var saved = key?.GetValue("Order") as string;
            return Orders.FirstOrDefault(o => string.Join(",", o.Select(Name)) == saved) ?? Orders[0];
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("Order", string.Join(",", value.Select(Name)), RegistryValueKind.String);
        }
    }

    static string Name(Pane pane) => pane == Pane.Codex ? "codex" : "claude";  // 맥판 paneOrder 와 같은 이름
}

enum Pane { Claude, Codex }

/// Claude 값을 받으려면 Claude Code 상태 표시줄을 AIUsageStatus 로 걸어야 한다
static class ClaudeLink
{
    /// 상태 표시줄이 이 프로그램을 가리키는데 그 파일이 없으면(지웠거나 다른 곳에서 켰거나) 묻지 않고 다시 건다
    public static void RepairIfBroken()
    {
        if (ClaudeSettings.State() != ClaudeSettings.LinkState.Broken) return;
        try { ClaudeSettings.Connect(Installer.EnsureStatusExe()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException) { }
    }

    public static void Ask(bool fromMenu)
    {
        var current = ClaudeSettings.CurrentCommand();
        if (ClaudeSettings.State() == ClaudeSettings.LinkState.Connected)
        {
            if (fromMenu) MessageBox.Show(T("이미 연결돼 있습니다.\n\n{0}", current ?? ""), T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 설치 직후 자동으로 물을 때는 Claude Code 를 쓰는 PC 에서만
        if (!fromMenu && !Directory.Exists(Path.GetDirectoryName(Paths.ClaudeSettings))) return;

        var question = T("Claude 값을 받으려면 Claude Code 상태 표시줄을 이 프로그램과 연결해야 합니다.") + "\n\n"
            + (current is null || ClaudeSettings.IsConnected(current) ? "" : T("지금 쓰는 상태 표시줄({0})은 AI 사용량의 상태 표시줄로 바뀝니다. 원래 설정은 settings.json.bak-날짜 파일로 남깁니다.", current) + "\n\n")
            + T("연결할까요?");
        if (MessageBox.Show(question, T("AI 사용량"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            ClaudeSettings.Connect(Installer.EnsureStatusExe());
            MessageBox.Show(T("연결했습니다. Claude Code 에서 한 번 대화하면 Claude 값이 나타납니다."), T("AI 사용량"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(T("연결하지 못했습니다: {0}", e.Message), T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
