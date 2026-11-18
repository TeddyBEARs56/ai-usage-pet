// AI 사용량(Windows) — 값 읽기·Claude Code 상태 표시줄 처리. 맥판(AIUsagePet.swift)과 같은 규칙.
// 화면 코드와 나눠 두어 AIUsageStatus(콘솔)로 맥에서도 돌려 볼 수 있다.
//
// 값의 출처
//   Claude  : Claude Code 상태 표시줄(AIUsageStatus --statusline)이 세션마다 남기는 ~/.cache/ai-usage/claude/<세션>.json
//             + Claude 데스크톱 앱이 남기는 plan-usage-history.json(초기화 시각 없음). 둘 중 나중 값을 쓴다
//             (Claude Code 로그인 토큰은 읽지 않는다)
//   ChatGPT : `codex app-server` 의 account/rateLimits/read(5분마다) + ~/.codex/sessions 기록에 찍히는 최신 한도

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIUsage;

public sealed record LimitWindow(string Id, string Label, int Minutes, double UsedPercent, DateTimeOffset? ResetsAt)
{
    /// 상태 표시줄과 같은 계산(100 - 사용률 내림). 초기화 시각이 지났으면 100.
    public int LeftPercent(DateTimeOffset now) =>
        ResetsAt is { } reset && reset <= now ? 100 : Math.Clamp(100 - (int)Math.Floor(UsedPercent), 0, 100);
}

public sealed class ProviderSnapshot
{
    public List<LimitWindow> Windows { get; } = new();
    public DateTimeOffset? ObservedAt { get; set; }
    public string? Plan { get; set; }
    public string? Message { get; set; }
    public string? Source { get; set; }  // 오른쪽 클릭 메뉴에 보일 값의 출처
}

public static class Paths
{
    /// 시험할 때는 AIUSAGE_HOME 으로 홈 폴더를 바꾼다
    public static string Home =>
        Environment.GetEnvironmentVariable("AIUSAGE_HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ClaudeCache => Path.Combine(Home, ".cache", "ai-usage", "claude");
    public static string CodexSessions => Path.Combine(Home, ".codex", "sessions");
    public static string ClaudeSettings => Path.Combine(Home, ".claude", "settings.json");
    public static string ClaudeDir => Path.Combine(Home, ".claude");
    public static string ClaudeAccount => Path.Combine(Home, ".claude.json");
    /// 상태 표시줄이 마지막으로 돈 때와 한도 값이 왔는지(값이 안 나올 때 이유를 말하려고)
    public static string StatusSeen => Path.Combine(Home, ".cache", "ai-usage", "claude-statusline.json");

    /// Claude 데스크톱 앱의 사용량 기록 후보. 설치판은 %APPDATA%\Claude, Microsoft Store 판은 Packages\…Claude…\LocalCache\Roaming\Claude.
    /// 맥에서 돌려 볼 때는 ~/Library/Application Support/Claude.
    public static List<string> ClaudeDesktopHistory()
    {
        const string name = "plan-usage-history.json";
        if (Environment.GetEnvironmentVariable("AIUSAGE_HOME") is { Length: > 0 } || OperatingSystem.IsMacOS())
            return new()
            {
                Path.Combine(Home, "AppData", "Roaming", "Claude", name),
                Path.Combine(Home, "Library", "Application Support", "Claude", name),
            };
        var result = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", name) };
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
                result.AddRange(Directory.EnumerateDirectories(packages, "*Claude_*")
                                         .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", name)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }
}

static class J
{
    public static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
        JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            => t.ToUnixTimeMilliseconds() / 1000.0,
        _ => null,
    };

    public static double? Number(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value) && Number(value) is { } n) return n;
        return null;
    }

    public static JsonElement? Obj(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : null;

    public static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static DateTimeOffset? Epoch(double? seconds) =>
        seconds is { } s && s > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)) : null;
}

public static class Fmt
{
    static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public static string WindowLabel(int minutes) => minutes switch
    {
        300 => "5시간",
        10_080 => "주간",
        > 0 when minutes % 1_440 == 0 => $"{minutes / 1_440}일",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}시간",
        > 0 => $"{minutes}분",
        _ => "한도",
    };

    public static string? PlanName(string? raw) => raw switch
    {
        null or "" or "unknown" => null,
        "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro", "prolite" => "Pro Lite", "promax" => "Pro Max",
        "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", "edu" => "Edu",
        _ => raw,
    };

    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", Ko);
    public static string FullTime(DateTimeOffset at) => at.ToLocalTime().ToString("M월 d일(ddd) HH:mm", Ko);

    public static string ResetText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        if (t <= now) return "초기화됨";
        var local = t.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return local.ToString("HH:mm", Ko);
        return local.ToString((t - now).TotalDays < 6 ? "ddd HH:mm" : "M'/'d HH:mm", Ko);
    }

    public static string AgeText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        var seconds = Math.Max(0, (now - t).TotalSeconds);
        return seconds switch
        {
            < 60 => "방금",
            < 3_600 => $"{(int)(seconds / 60)}분 전",
            < 86_400 => $"{(int)(seconds / 3_600)}시간 전",
            _ => $"{(int)(seconds / 86_400)}일 전",
        };
    }

    public static string Describe(string title, ProviderSnapshot snap, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"{title}{(snap.Plan is { } plan ? $" ({plan})" : "")} — 마지막 값 {(snap.ObservedAt is { } at ? FullTime(at) : "없음")}",
        };
        if (snap.Windows.Count == 0) lines.Add($"  {snap.Message ?? "값 없음"}");
        foreach (var w in snap.Windows)
            lines.Add($"  {w.Label}: 사용 {w.UsedPercent.ToString(CultureInfo.InvariantCulture)}% → {w.LeftPercent(now)}% 남음, 초기화 {(w.ResetsAt is { } r ? FullTime(r) : "-")}");
        return string.Join("\n", lines);
    }
}

// MARK: Claude

/// 세션마다 마지막으로 받은 한도를 모아, 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다.
/// 같은 창 안에서 사용률은 줄지 않으므로, 오래 쉬던 세션이 옛 값을 다시 써도 표시가 뒤로 가지 않는다.
public sealed class ClaudeReader
{
    readonly Dictionary<string, (DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits)> cache = new();

    public ProviderSnapshot Read(DateTimeOffset now)
    {
        var entries = new List<(DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits)>();
        var alive = new HashSet<string>();
        if (Directory.Exists(Paths.ClaudeCache))
        {
            foreach (var path in Directory.EnumerateFiles(Paths.ClaudeCache, "*.json"))
            {
                if (Path.GetFileName(path).StartsWith('.')) continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(path); } catch (IOException) { continue; }
                if (now.UtcDateTime - mtime > TimeSpan.FromDays(8))  // 주간 창보다 오래된 세션 파일은 정리
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    continue;
                }
                alive.Add(path);
                if (cache.TryGetValue(path, out var hit) && hit.Mtime == mtime)
                {
                    entries.Add(hit);
                    continue;
                }
                Dictionary<string, (double Used, double Reset)> limits;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    limits = FromRateLimits(doc.RootElement);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { continue; }
                cache[path] = (mtime, limits);
                entries.Add((mtime, limits));
            }
        }
        foreach (var stale in cache.Keys.Where(k => !alive.Contains(k)).ToList()) cache.Remove(stale);

        // Claude Code 를 안 쓰는 동안에는 데스크톱 앱 기록이 더 새것일 수 있다(같은 계정일 때만)
        DateTimeOffset? statusAt = entries.Count > 0 ? new DateTimeOffset(entries.Max(e => e.Mtime), TimeSpan.Zero) : null;
        var desktop = ClaudeDesktopUsage.Latest(ClaudeCodeAccount.Organization(), now);
        var snap = new ProviderSnapshot();
        bool usedStatus = false, usedDesktop = false;
        foreach (var (key, label, minutes) in new[] { ("five_hour", "5시간", 300), ("seven_day", "주간", 10_080) })
        {
            var found = entries.Where(e => e.Limits.ContainsKey(key)).Select(e => e.Limits[key]).ToList();
            double? used = null;
            double reset = 0;
            if (found.Count > 0)
            {
                reset = found.Max(f => f.Reset);
                var latest = reset;
                used = found.Where(f => f.Reset >= latest - 600).Max(f => f.Used);
            }
            var fromDesktop = false;
            if (desktop is { } d && d.Used.TryGetValue(key, out var value) && (used is null || d.At > (statusAt ?? DateTimeOffset.MinValue)))
            {
                if (used is { } current && reset > d.At.ToUnixTimeMilliseconds() / 1000.0)
                    used = Math.Max(current, value);  // 상태 표시줄이 본 창이 그때도 열려 있었다: 같은 창이라 초기화 시각을 이어 쓴다
                else
                {
                    // 데스크톱 앱 기록에는 초기화 시각이 없다. 창 길이보다 오래된 기록이면 그 창은 이미 끝났다.
                    used = (now - d.At).TotalMinutes >= minutes ? 0 : value;
                    reset = 0;
                }
                fromDesktop = true;
            }
            if (used is not { } u) continue;
            if (fromDesktop) usedDesktop = true; else usedStatus = true;
            snap.Windows.Add(new LimitWindow(key, label, minutes, u, J.Epoch(reset)));
        }
        var seen = new List<DateTimeOffset>();
        if (usedStatus && statusAt is { } s) seen.Add(s);
        if (usedDesktop && desktop is { } dd) seen.Add(dd.At);
        snap.ObservedAt = seen.Count > 0 ? seen.Max() : null;
        snap.Source = usedStatus && usedDesktop ? "Claude Code·데스크톱 앱" : usedDesktop ? "Claude 데스크톱 앱 기록" : "Claude Code 상태 표시줄";
        if (snap.Windows.Count == 0) snap.Message = ClaudeSettings.EmptyMessage();
        return snap;
    }

    static Dictionary<string, (double Used, double Reset)> FromRateLimits(JsonElement limits)
    {
        var result = new Dictionary<string, (double Used, double Reset)>();
        foreach (var key in new[] { "five_hour", "seven_day" })
            if (J.Obj(limits, key) is { } w && J.Number(w, "used_percentage") is { } used)
                result[key] = (used, J.Number(w, "resets_at") ?? 0);
        return result;
    }
}

public sealed record DesktopUsageSample(DateTimeOffset At, Dictionary<string, double> Used);

/// Claude 데스크톱 앱이 사용량을 서버에서 받을 때마다 남기는 기록(로그인 정보 없이 읽는다).
/// 앱을 쓰는 동안에만 쌓이고, 4.5분에 한 번까지만 남으며, 초기화 시각은 없다. 공식 문서에 없는 파일이라 바뀔 수 있다.
public static class ClaudeDesktopUsage
{
    /// 가장 최근 기록. Claude Code 계정(조직)을 알면 그 조직 것만, 8일보다 오래된 것은 뺀다.
    public static DesktopUsageSample? Latest(string? org, DateTimeOffset now)
    {
        foreach (var path in Paths.ClaudeDesktopHistory())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) continue;
                DesktopUsageSample? best = null;
                foreach (var sample in samples.EnumerateArray())
                {
                    if (J.Number(sample, "t") is not { } t) continue;
                    var at = DateTimeOffset.FromUnixTimeMilliseconds((long)t);
                    if (now - at >= TimeSpan.FromDays(8) || (best is not null && at <= best.At)) continue;
                    if (org is not null && J.Str(sample, "org") is { } sampleOrg && sampleOrg != org) continue;
                    var u = J.Obj(sample, "u") ?? sample;  // 1판은 fh·sd 가 바로 밑에 있다
                    var used = new Dictionary<string, double>();
                    if (J.Number(u, "fh") is { } fh) used["five_hour"] = fh;
                    if (J.Number(u, "sd") is { } sd) used["seven_day"] = sd;
                    if (used.Count > 0) best = new DesktopUsageSample(at, used);
                }
                if (best is not null) return best;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// Claude Code 가 로그인한 조직(~/.claude.json 의 oauthAccount.organizationUuid, 비밀값 아님).
/// 데스크톱 앱이 다른 계정이면 그 기록은 쓰지 않으려고 본다. 파일이 바뀔 때만 다시 읽는다.
public static class ClaudeCodeAccount
{
    static (DateTime Mtime, string? Org)? cached;

    public static string? Organization()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeAccount)) return null;
            var mtime = File.GetLastWriteTimeUtc(Paths.ClaudeAccount);
            if (cached is { } hit && hit.Mtime == mtime) return hit.Org;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.ClaudeAccount));
            var org = J.Obj(doc.RootElement, "oauthAccount") is { } account ? J.Str(account, "organizationUuid") : null;
            cached = (mtime, org);
            return org;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

// MARK: ChatGPT(Codex)

public sealed class CodexReader
{
    string lastLogKey = "";

    public ProviderSnapshot? FromLog { get; private set; }
    public ProviderSnapshot? FromServer { get; private set; }
    public string? ServerError { get; private set; }
    public DateTimeOffset? ServerCheckedAt { get; private set; }

    /// 기록과 서버 가운데 더 나중에 확인한 값을 쓴다
    public ProviderSnapshot Current =>
        new[] { FromLog, FromServer }.OfType<ProviderSnapshot>().OrderByDescending(s => s.ObservedAt ?? DateTimeOffset.MinValue).FirstOrDefault()
        ?? new ProviderSnapshot { Message = ServerError ?? "Codex 한도를 읽는 중…" };

    /// Codex 를 쓰는 동안 기록 파일에 찍히는 최신 한도(파일이 바뀌었을 때만 다시 읽는다)
    public void ReadLog()
    {
        var files = RecentRolloutFiles(DateTimeOffset.Now);
        if (files.Count == 0) return;
        var key = $"{files[0].Path}|{files[0].Mtime.Ticks}";
        if (key == lastLogKey) return;
        lastLogKey = key;
        foreach (var file in files.Take(5))
        {
            if (LastLimits(file.Path, file.Mtime) is { } snap)
            {
                FromLog = snap;
                return;
            }
        }
    }

    public void ApplyServer((ProviderSnapshot? Snap, string? Error) result)
    {
        ServerCheckedAt = DateTimeOffset.Now;
        if (result.Snap is { } snap)
        {
            FromServer = snap;
            ServerError = null;
        }
        else
        {
            ServerError = result.Error;
        }
    }

    static List<LimitWindow> WindowsFrom(JsonElement limits)
    {
        var windows = new List<LimitWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (J.Obj(limits, key) is not { } w || J.Number(w, "usedPercent", "used_percent") is not { } used) continue;
            var minutes = (int)(J.Number(w, "windowDurationMins", "window_minutes") ?? 0);
            windows.Add(new LimitWindow(key, Fmt.WindowLabel(minutes), minutes, used, J.Epoch(J.Number(w, "resetsAt", "resets_at"))));
        }
        return windows.OrderBy(w => w.Minutes).ToList();
    }

    /// 오늘·어제 날짜 폴더의 rollout 기록, 최근 것부터
    static List<(string Path, DateTime Mtime)> RecentRolloutFiles(DateTimeOffset now)
    {
        var files = new List<(string Path, DateTime Mtime)>();
        for (var back = 0; back <= 1; back++)
        {
            var day = now.LocalDateTime.Date.AddDays(-back);
            var dir = Path.Combine(Paths.CodexSessions, day.ToString("yyyy", CultureInfo.InvariantCulture),
                                   day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
            {
                try { files.Add((path, File.GetLastWriteTimeUtc(path))); } catch (IOException) { }
            }
        }
        return files.OrderByDescending(f => f.Mtime).ToList();
    }

    static readonly byte[] Marker = "\"rate_limits\""u8.ToArray();

    static ProviderSnapshot? LastLimits(string path, DateTime mtime)
    {
        byte[] data;
        try
        {
            // Codex 가 쓰는 중인 파일이라 함께 열기를 허용한다
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 2_000_000);
            stream.Seek(start, SeekOrigin.Begin);
            data = new byte[stream.Length - start];
            stream.ReadExactly(data);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var end = data.Length;
        for (var i = data.Length - 1; i >= -1; i--)
        {
            if (i >= 0 && data[i] != (byte)'\n') continue;
            var line = data.AsMemory(i + 1, end - i - 1);
            end = i;
            if (line.Span.IndexOf(Marker) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (J.Obj(doc.RootElement, "payload") is not { } payload || J.Obj(payload, "rate_limits") is not { } limits) continue;
                if (J.Str(limits, "limit_id") is { } id && id != "codex") continue;
                var windows = WindowsFrom(limits);
                if (windows.Count == 0) continue;
                var snap = new ProviderSnapshot
                {
                    ObservedAt = J.Epoch(J.Number(doc.RootElement, "timestamp")) ?? new DateTimeOffset(mtime, TimeSpan.Zero),
                    Plan = Fmt.PlanName(J.Str(limits, "plan_type")),
                };
                snap.Windows.AddRange(windows);
                return snap;
            }
            catch (JsonException) { }
        }
        return null;
    }

    public static string? FindCodex()
    {
        var home = Paths.Home;
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(home, ".codex", "packages", "standalone", "current", "bin", "codex.exe"));
            candidates.Add(Path.Combine(home, ".local", "bin", "codex.exe"));
            // npm 으로 깐 경우 codex.cmd 보다 안쪽의 실제 실행 파일을 쓴다(cmd·node 를 거치지 않게)
            var vendor = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "vendor");
            if (Directory.Exists(vendor))
            {
                try { candidates.AddRange(Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
        }
        else
        {
            candidates.Add(Path.Combine(home, ".local", "bin", "codex"));
            candidates.Add("/opt/homebrew/bin/codex");
            candidates.Add("/usr/local/bin/codex");
            candidates.Add(Path.Combine(home, ".codex", "packages", "standalone", "current", "bin", "codex"));
        }
        var names = OperatingSystem.IsWindows() ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                candidates.Add(Path.Combine(dir.Trim('"'), name));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// `codex app-server` 를 잠깐 띄워 account/rateLimits/read 로 지금 한도를 묻는다(인증은 codex 가 알아서 한다)
    public static async Task<(ProviderSnapshot? Snap, string? Error)> FetchServerAsync()
    {
        if (FindCodex() is not { } exe) return (null, "codex 명령을 찾지 못했습니다(Codex CLI 설치 필요)");
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.Home,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"\"{exe}\" app-server\"";
        }
        else
        {
            start.FileName = exe;
            start.ArgumentList.Add("app-server");
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "codex 를 실행하지 못했습니다");
        }
        process.ErrorDataReceived += (_, _) => { };  // 오류 출력이 쌓여 멈추지 않게 비워 둔다
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await Send(process, """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"ai-usage-pet","version":"1.0"}}}""");
            using (var init = await Receive(process, 1, timeout.Token))
                if (init is null) return (null, "codex 가 응답하지 않습니다");
            await Send(process, """{"method":"initialized"}""");
            await Send(process, """{"id":2,"method":"account/rateLimits/read"}""");
            using var response = await Receive(process, 2, timeout.Token);
            if (response is null) return (null, "codex 가 응답하지 않습니다");
            if (J.Obj(response.RootElement, "result") is not { } result) return (null, "Codex 한도를 읽지 못했습니다(로그인 확인)");

            var limits = (J.Obj(result, "rateLimitsByLimitId") is { } byId ? J.Obj(byId, "codex") : null)
                         ?? J.Obj(result, "rateLimits");
            var snap = new ProviderSnapshot
            {
                ObservedAt = DateTimeOffset.Now,
                Plan = limits is { } l ? Fmt.PlanName(J.Str(l, "planType")) : null,
            };
            if (limits is { } found) snap.Windows.AddRange(WindowsFrom(found));
            if (snap.Windows.Count == 0) snap.Message = "표시할 한도가 없습니다";
            return (snap, null);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            return (null, "codex 가 응답하지 않습니다");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    static async Task Send(Process process, string line)
    {
        await process.StandardInput.WriteAsync(line + "\n");
        await process.StandardInput.FlushAsync();
    }

    /// 보낸 요청(id)에 대한 응답이 올 때까지 읽는다. 알림·서버 요청은 건너뛴다.
    static async Task<JsonDocument?> Receive(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) return null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("method", out _)
                && root.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                return doc;
            doc.Dispose();
        }
    }
}

// MARK: Claude Code 상태 표시줄

public static class StatusLine
{
    static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,128}$");

    /// Claude Code 가 넘겨준 JSON 에서 한도를 세션별 파일로 남기고, 상태 표시줄 글자를 돌려준다
    public static string Handle(string input, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(input);
        SaveLimits(doc.RootElement);
        SaveSeen(doc.RootElement);
        return Render(doc.RootElement);
    }

    /// 상태 표시줄이 돌았다는 표시와 한도 값이 왔는지(구독 로그인이 아니면 rate_limits 가 없다)
    static void SaveSeen(JsonElement root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.StatusSeen)!);
            var seen = new JsonObject
            {
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["limits"] = J.Obj(root, "rate_limits") is not null,
                ["version"] = J.Str(root, "version") ?? "",
            };
            var temp = Paths.StatusSeen + ".tmp";
            File.WriteAllText(temp, seen.ToJsonString());
            File.Move(temp, Paths.StatusSeen, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void SaveLimits(JsonElement root)
    {
        if (J.Str(root, "session_id") is not { } id || !SafeId.IsMatch(id) || J.Obj(root, "rate_limits") is not { } limits) return;
        try
        {
            Directory.CreateDirectory(Paths.ClaudeCache);
            var temp = Path.Combine(Paths.ClaudeCache, $".{id}.tmp");
            File.WriteAllText(temp, limits.GetRawText());
            File.Move(temp, Path.Combine(Paths.ClaudeCache, $"{id}.json"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    const string Reset = "\u001b[0m", Dim = "\u001b[2m", Cyan = "\u001b[36m", White = "\u001b[97m", Sky = "\u001b[38;5;117m";

    // 남은 비율에 따라 초록(여유)·노랑(주의)·빨강(부족) — 맥 상태 표시줄과 같은 기준
    static string Color(int left) => left >= 50 ? "\u001b[32m" : left >= 20 ? "\u001b[33m" : "\u001b[31m";

    static string EffortColor(string level) => level switch
    {
        "low" => "\u001b[90m",
        "medium" => "\u001b[34m",
        "high" => "\u001b[35m",
        "xhigh" => "\u001b[38;5;208m",
        "max" => "\u001b[1;31m",
        _ => "",
    };

    static string K(double n) => n >= 1_000_000
        ? (n / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : $"{(int)(n / 1000)}K";

    static string Render(JsonElement root)
    {
        var lines = new List<string>();

        // 1줄: 저장소 · 워크트리 · PR (git 저장소 밖이면 생략)
        var workspace = J.Obj(root, "workspace");
        var location = new List<string>();
        if (workspace is { } ws && J.Obj(ws, "repo") is { } repo && J.Str(repo, "name") is { Length: > 0 } repoName)
            location.Add($"저장소 {White}{repoName}{Reset}");
        if (workspace is { } ws2 && J.Str(ws2, "git_worktree") is { Length: > 0 } worktree)
            location.Add($"워크트리 {Sky}{worktree}{Reset}");
        if (J.Obj(root, "pr") is { } pr && J.Number(pr, "number") is { } number)
            location.Add($"PR #{(int)number}");
        if (location.Count > 0) lines.Add(string.Join($" {Dim}·{Reset} ", location));

        // 2줄: 모델 · 추론 강도 │ 컨텍스트
        var model = J.Obj(root, "model") is { } m ? J.Str(m, "display_name") : null;
        var line = $"{Cyan}{model ?? "?"}{Reset}";
        if (J.Obj(root, "effort") is { } effort && J.Str(effort, "level") is { Length: > 0 } level)
            line += $" {Dim}·{Reset} 추론 강도 {EffortColor(level)}{level}{Reset}";
        if (J.Obj(root, "context_window") is { } context && J.Number(context, "remaining_percentage") is { } remaining)
        {
            var left = (int)Math.Floor(remaining);
            line += $" {Dim}│{Reset} 컨텍스트 {Color(left)}{left}% 남음{Reset} "
                  + $"{Dim}({K(J.Number(context, "total_input_tokens") ?? 0)}/{K(J.Number(context, "context_window_size") ?? 0)}){Reset}";
        }
        lines.Add(line);

        // 3줄: 사용 한도
        if (J.Obj(root, "rate_limits") is { } limits)
        {
            var parts = new List<string>();
            if (J.Obj(limits, "five_hour") is { } five && J.Number(five, "used_percentage") is { } fiveUsed)
            {
                var left = 100 - (int)Math.Floor(fiveUsed);
                var text = $"5시간 한도 {Color(left)}{left}% 남음{Reset}";
                if (J.Epoch(J.Number(five, "resets_at")) is { } at) text += $" {Dim}({Fmt.Clock(at)} 초기화){Reset}";
                parts.Add(text);
            }
            if (J.Obj(limits, "seven_day") is { } week && J.Number(week, "used_percentage") is { } weekUsed)
            {
                var left = 100 - (int)Math.Floor(weekUsed);
                parts.Add($"주간 한도 {Color(left)}{left}% 남음{Reset}");
            }
            if (parts.Count > 0) lines.Add(string.Join($" {Dim}·{Reset} ", parts));
        }
        return string.Join("\n", lines);
    }
}

/// ~/.claude/settings.json 의 statusLine 을 AIUsageStatus 로 걸고 푼다
public static class ClaudeSettings
{
    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// Claude Code 는 상태 표시줄 명령을 bash 로 돌리므로 / 로 쓰고, 빈칸이 있을 때만 따옴표로 감싼다
    public static string CommandFor(string statusExe)
    {
        var path = statusExe.Replace('\\', '/');
        return (path.Contains(' ') ? $"\"{path}\"" : path) + " --statusline";
    }

    public static string? CurrentCommand()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeSettings)) return null;
            var node = JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings), documentOptions: ReadOptions);
            return node?["statusLine"]?["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsConnected(string? command) =>
        command is not null && command.Contains("AIUsageStatus", StringComparison.OrdinalIgnoreCase);

    public enum LinkState { NoClaudeCode, NotConnected, Broken, Connected }

    /// 걸려 있어도 가리키는 AIUsageStatus.exe 가 없으면 끊긴 것(Broken)
    public static LinkState State()
    {
        if (!Directory.Exists(Paths.ClaudeDir)) return LinkState.NoClaudeCode;
        var command = CurrentCommand();
        if (!IsConnected(command)) return LinkState.NotConnected;
        var exe = command!.Replace("--statusline", "").Trim().Trim('"');
        return File.Exists(exe) ? LinkState.Connected : LinkState.Broken;
    }

    /// Claude 칸에 값이 하나도 없을 때 보일 이유
    public static string EmptyMessage() => State() switch
    {
        LinkState.NoClaudeCode => "Claude Code 나 Claude 데스크톱 앱을 쓰면 표시됩니다",
        LinkState.NotConnected => "Claude Code 에 연결되지 않았습니다 — 오른쪽 클릭 → 「Claude Code 상태 표시줄 연결」",
        LinkState.Broken => "Claude Code 연결이 끊겼습니다 — 오른쪽 클릭 → 「Claude Code 상태 표시줄 연결」",
        _ => LastSeen() is { Limits: false }
            ? "Claude Code 가 한도 값을 보내지 않습니다 — Claude 구독(Pro·Max·Team)으로 로그인했는지, Claude Code 가 최신인지 확인"
            : "Claude Code 에서 한 번 대화하면 표시됩니다",
    };

    public static (DateTimeOffset At, bool Limits, string Version)? LastSeen()
    {
        try
        {
            if (!File.Exists(Paths.StatusSeen)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.StatusSeen));
            var root = doc.RootElement;
            if (J.Epoch(J.Number(root, "at")) is not { } at) return null;
            var limits = root.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.True;
            return (at, limits, J.Str(root, "version") ?? "");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// statusLine 만 바꾸고 나머지 설정은 그대로 둔다. 원래 파일은 settings.json.bak-날짜 로 남긴다.
    public static void Connect(string statusExe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ClaudeSettings)!);
        var root = new JsonObject();
        if (File.Exists(Paths.ClaudeSettings))
        {
            var text = File.ReadAllText(Paths.ClaudeSettings);
            root = JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException("settings.json 이 JSON 객체가 아닙니다");
            File.Copy(Paths.ClaudeSettings, $"{Paths.ClaudeSettings}.bak-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = CommandFor(statusExe) };
        File.WriteAllText(Paths.ClaudeSettings, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }
}

public static class Report
{
    /// 창 없이 값만 확인할 때(AIUsageStatus --print)
    public static async Task<string> PrintAsync()
    {
        var now = DateTimeOffset.Now;
        var codex = new CodexReader();
        codex.ReadLog();
        var fromLog = codex.FromLog;
        codex.ApplyServer(await CodexReader.FetchServerAsync());
        var claude = new ClaudeReader().Read(now);
        var link = ClaudeSettings.State() switch
        {
            ClaudeSettings.LinkState.NoClaudeCode => "Claude Code 없음(~\\.claude 폴더 없음)",
            ClaudeSettings.LinkState.NotConnected => $"안 됨 — 지금 명령: {ClaudeSettings.CurrentCommand() ?? "없음"}",
            ClaudeSettings.LinkState.Broken => $"끊김(가리키는 AIUsageStatus.exe 가 없음) — 지금 명령: {ClaudeSettings.CurrentCommand()}",
            _ => "됨",
        };
        var lines = new List<string>
        {
            Fmt.Describe("Claude", claude, now),
            $"  출처: {(claude.Windows.Count == 0 ? "-" : claude.Source)} · Claude Code 연결: {link}",
        };
        if (ClaudeSettings.LastSeen() is { } seen)
            lines.Add($"  상태 표시줄 마지막 실행 {Fmt.FullTime(seen.At)} · 한도 값 {(seen.Limits ? "옴" : "안 옴")} · Claude Code {seen.Version}");
        return string.Join("\n", lines.Concat(new[]
        {
            Fmt.Describe("ChatGPT · 기록", fromLog ?? new ProviderSnapshot { Message = "최근 기록 없음" }, now),
            Fmt.Describe("ChatGPT · 서버", codex.FromServer ?? new ProviderSnapshot { Message = codex.ServerError ?? "실패" }, now),
            Fmt.Describe("ChatGPT · 표시", codex.Current, now),
        }));
    }
}
