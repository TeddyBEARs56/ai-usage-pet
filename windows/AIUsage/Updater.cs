// 자동 업데이트 — GitHub 저장소 Releases 의 windows-v<버전> 에 올린 AIUsage.exe 를 받아 설치된 것과 바꾼다.
// 켠 뒤 1분, 그 뒤 6시간마다 확인하고, 설치된 곳(%LOCALAPPDATA%\Programs\AIUsage)에서 켰을 때만 바꾼다.
// 끄기: HKCU\Software\AIUsage 의 AutoUpdate(DWORD)를 0 으로.
// 바꾸는 순서: 받은 exe 를 「--apply-update <지금 pid>」로 띄움 → 지금 창이 꺼짐 → 받은 exe 가 설치 폴더에 자신을 복사 → 설치된 것을 다시 띄움
// (돌고 있는 exe 는 덮어쓸 수 없어서 새 exe 가 바꾼다)

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using static AIUsage.L10n;

namespace AIUsage;

static class Updater
{
    const string Repo = "TeddyBEARs56/ai-usage-widget";
    const string TagPrefix = "windows-v";
    const string AssetName = "AIUsage.exe";

    public sealed record Release(string Version, Uri Url, string? Sha256);

    /// 시험할 때는 AIUSAGE_UPDATE_DELAY 로 첫 확인까지 초를, AIUSAGE_UPDATE_FEED 로 Releases 목록 주소를 바꾼다
    public static TimeSpan FirstDelay => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("AIUSAGE_UPDATE_DELAY"), out var seconds) ? seconds : 60);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    static string FeedUrl => Environment.GetEnvironmentVariable("AIUSAGE_UPDATE_FEED") is { Length: > 0 } feed
        ? feed : $"https://api.github.com/repos/{Repo}/releases?per_page=30";
    static string Folder => Path.Combine(Installer.Dir, "update");
    public static string LogFile => Path.Combine(Paths.Home, ".cache", "ai-usage", "update.log");

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsage-updater");
        return client;
    }

    /// 지금 판(프로젝트 파일의 Version, 예: 1.1.0)
    public static string Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// 올라온 Windows 판 가운데 가장 높은 것(초안·미리 보기 판은 뺀다). 없으면 null.
    public static async Task<Release?> LatestAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(T("GitHub 응답 {0}", (int)response.StatusCode)
                                           + (response.StatusCode == HttpStatusCode.NotFound ? T("(저장소가 비공개거나 없음)") : ""));
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return Pick(doc.RootElement);
    }

    internal static Release? Pick(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException(T("Releases 목록을 읽지 못함"));
        Release? best = null;
        foreach (var item in list.EnumerateArray())
        {
            if (IsTrue(item, "draft") || IsTrue(item, "prerelease")) continue;
            if (J.Str(item, "tag_name") is not { } tag || !tag.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            var version = tag[TagPrefix.Length..];
            if (Parts(version) is null || (best is not null && Compare(version, best.Version) <= 0)) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (J.Str(asset, "name") != AssetName || J.Str(asset, "browser_download_url") is not { } link
                    || !Uri.TryCreate(link, UriKind.Absolute, out var url)) continue;
                var digest = J.Str(asset, "digest");
                best = new Release(version, url,
                                   digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToLowerInvariant() : null);
                break;
            }
        }
        return best;
    }

    static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// 받아서 검사한 새 exe 의 경로. 올라온 SHA-256 과 다르거나 파일 버전이 태그와 다르면 버린다.
    public static async Task<string> DownloadAsync(Release release, CancellationToken token = default)
    {
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, $"AIUsage-{release.Version}.exe");
        var partial = target + ".part";
        using (var request = new HttpRequestMessage(HttpMethod.Get, release.Url))
        {
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(T("내려받기 응답 {0}", (int)response.StatusCode));
            await using var file = File.Create(partial);
            await response.Content.CopyToAsync(file, token);
        }
        if (release.Sha256 is { } expected)
        {
            string actual;
            await using (var stream = File.OpenRead(partial))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (actual != expected)
            {
                File.Delete(partial);
                throw new InvalidDataException(T("받은 파일이 올라온 것과 다릅니다(SHA-256)"));
            }
        }
        // Windows 에서는 exe 의 파일 버전이 읽힌다. 태그와 다르면(잘못 올린 판) 바꾸고 나서 또 받는 일이 되풀이되므로 버린다.
        // (못 읽거나 숫자가 아니면 이 검사는 건너뛴다 — 내용은 위 SHA-256 으로 이미 확인했다)
        var fileVersion = FileVersionInfo.GetVersionInfo(partial).FileVersion;
        if (fileVersion is not null && Parts(fileVersion) is not null && Compare(fileVersion, release.Version) != 0)
        {
            File.Delete(partial);
            throw new InvalidDataException(T("받은 exe 의 버전({0})이 {1} 이 아닙니다", fileVersion, release.Version));
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// 받은 exe 를 띄운다. 그 exe 는 이 프로세스가 끝나길 기다렸다가 설치 폴더에 자신을 복사하고 설치된 것을 띄운다.
    public static void Launch(string exe) =>
        Process.Start(new ProcessStartInfo(exe, $"--apply-update {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();

    /// 「--apply-update <pid>」로 띄워졌으면 그 pid(바꿀 차례), 아니면 null
    public static int? ApplyTarget(string[] args)
    {
        var i = Array.IndexOf(args, "--apply-update");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) ? pid : null;
    }

    public static void WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException) { }  // 이미 끝났다
    }

    /// 설치 폴더에 이 exe 를 복사하고(옛 것이 잠깐 잠겨 있을 수 있어 기다리며 다시 한다) 설치된 것을 띄운다.
    /// 바꾸지 못해도 설치된 옛 판을 다시 띄워 창이 사라진 채로 남지 않게 한다.
    public static void ApplyAndRelaunch(Mutex mutex)
    {
        try
        {
            Retry(() => File.Copy(Installer.ExePath, Installer.InstalledExe, overwrite: true));
            Installer.ExtractFiles();
            Log($"{Current} 으로 바꿈");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"바꾸지 못함: {e.Message}");
        }
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true })?.Dispose();
    }

    /// 지난번에 받아 둔 exe 를 지운다(바꾸는 중인 exe 는 아직 잠겨 있을 수 있어 못 지우면 다음에)
    public static void CleanUp()
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Retry(Action action, int times = 20)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < times && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 100_000) File.Delete(LogFile);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Current} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static int[]? Parts(string version)
    {
        var parts = version.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }

    /// 1.1.0 과 1.1.0.0 은 같다
    internal static int Compare(string a, string b)
    {
        var x = Parts(a) ?? [];
        var y = Parts(b) ?? [];
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : 0;
            var r = i < y.Length ? y[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }
}
