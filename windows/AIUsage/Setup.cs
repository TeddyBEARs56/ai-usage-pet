// 설치(시작 메뉴 등록)·자동 실행·위치 기억·Claude Code 연결

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIUsage;

static class Installer
{
    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AIUsage");
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
            ? "설치된 AI 사용량을 지금 이 파일로 바꿀까요?"
            : "AI 사용량을 이 PC에 설치할까요?\n\n· 시작 메뉴에 「AI 사용량」이 생깁니다(작업 표시줄에 고정할 수 있습니다)\n"
              + $"· 설치 위치: {Dir}\n\n「아니요」를 누르면 설치하지 않고 이 파일로 실행합니다.";
        if (MessageBox.Show(question, "AI 사용량", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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
            Extract("AIUsage.ico", IconFile);
            Extract("AIUsageStatus.exe", StatusExe);
            CreateShortcut(Shortcut, InstalledExe, IconFile);
            if (AutoStart.IsOn) AutoStart.Set(true, InstalledExe);  // 자동 실행도 설치된 쪽을 가리키게
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            MessageBox.Show($"설치하지 못했습니다: {e.Message}\n\n설치하지 않고 이 파일로 실행합니다.", "AI 사용량",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        Process.Start(new ProcessStartInfo(InstalledExe, "--after-install") { UseShellExecute = true });
        return true;
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
                           ?? throw new InvalidOperationException($"{resource} 가 실행 파일 안에 없습니다");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        // 내용이 같으면 그대로 둔다(상태 표시줄이 쓰는 중이라 덮어쓸 수 없을 수 있다)
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(target, bytes);
    }

    static void CreateShortcut(string link, string target, string icon)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 을 찾지 못했습니다");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = icon + ",0";
            shortcut.Description = "Claude·ChatGPT 사용 한도를 화면 위에 띄워 둔다";
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
}

/// Claude 값을 받으려면 Claude Code 상태 표시줄을 AIUsageStatus 로 걸어야 한다
static class ClaudeLink
{
    public static void Ask(bool fromMenu)
    {
        var current = ClaudeSettings.CurrentCommand();
        if (ClaudeSettings.IsConnected(current))
        {
            if (fromMenu) MessageBox.Show($"이미 연결돼 있습니다.\n\n{current}", "AI 사용량", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 설치 직후 자동으로 물을 때는 Claude Code 를 쓰는 PC 에서만
        if (!fromMenu && !Directory.Exists(Path.GetDirectoryName(Paths.ClaudeSettings))) return;

        var question = "Claude 값을 받으려면 Claude Code 상태 표시줄을 이 프로그램과 연결해야 합니다.\n\n"
            + (current is null ? "" : $"지금 쓰는 상태 표시줄({current})은 AI 사용량의 상태 표시줄로 바뀝니다. 원래 설정은 settings.json.bak-날짜 파일로 남깁니다.\n\n")
            + "연결할까요?";
        if (MessageBox.Show(question, "AI 사용량", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            ClaudeSettings.Connect(Installer.EnsureStatusExe());
            MessageBox.Show("연결했습니다. Claude Code 에서 한 번 대화하면 Claude 값이 나타납니다.", "AI 사용량",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show($"연결하지 못했습니다: {e.Message}", "AI 사용량", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
