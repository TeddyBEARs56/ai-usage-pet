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
using static AIUsage.L10n;

namespace AIUsage;

/// 화면 글자의 언어 — 맥 판 L10n 과 같은 규칙. 처음엔 Windows 표시 언어(한국어·일본어·중국어면 그 언어, 그 밖엔 영어)를 따르고,
/// 메뉴 「언어」로 고르면 ~/.cache/ai-usage/language 에 남긴다(상태 표시줄 프로그램도 같은 파일을 본다).
/// 번역은 실행 파일 안 strings.json(저장소 i18n/strings.json, 맥 판과 같은 파일): 한국어 문장이 열쇠, {0} {1} 은 끼워 넣을 값.
/// 시험: AIUSAGE_LANG=en 으로 언어를 정한다.
public static class L10n
{
    public static readonly string[] Codes = { "ko", "en", "ja", "zh-Hans" };
    public static readonly Dictionary<string, string> NativeNames = new()
    {
        ["ko"] = "한국어", ["en"] = "English", ["ja"] = "日本語", ["zh-Hans"] = "简体中文",
    };
    static readonly object Gate = new();
    static string? resolved;
    static Dictionary<string, Dictionary<string, string>>? table;

    static string ChoiceFile => Path.Combine(Paths.Home, ".cache", "ai-usage", "language");

    /// 고른 값: "auto"(시스템 언어) 또는 언어 코드
    public static string Choice
    {
        get
        {
            if (Environment.GetEnvironmentVariable("AIUSAGE_LANG") is { } forced && Codes.Contains(forced)) return forced;
            try
            {
                var saved = File.ReadAllText(ChoiceFile).Trim();
                if (Codes.Contains(saved)) return saved;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return "auto";
        }
    }

    public static string Current
    {
        get
        {
            lock (Gate)
            {
                if (resolved is null)
                {
                    var choice = Choice;
                    resolved = choice == "auto" ? SystemLanguage() : choice;
                }
                return resolved;
            }
        }
    }

    public static void Choose(string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)!);
        File.WriteAllText(ChoiceFile, value);
        lock (Gate) resolved = null;
    }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current switch
    {
        "ko" => "ko-KR", "ja" => "ja-JP", "zh-Hans" => "zh-CN", _ => "en-US",
    });

    static string SystemLanguage()
    {
        if (OperatingSystem.IsWindows())
        {
            return (GetUserDefaultUILanguage() & 0x3FF) switch  // 주 언어 번호
            {
                0x12 => "ko", 0x11 => "ja", 0x04 => "zh-Hans", _ => "en",
            };
        }
        var name = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        return name.StartsWith("ko") ? "ko" : name.StartsWith("ja") ? "ja" : name.StartsWith("zh") ? "zh-Hans" : "en";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    static Dictionary<string, Dictionary<string, string>> Table
    {
        get
        {
            lock (Gate) return table ??= Load();
        }
    }

    static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        try
        {
            using var stream = typeof(L10n).Assembly.GetManifestResourceStream("strings.json");
            if (stream is null) return result;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var languages = new Dictionary<string, string>();
                foreach (var language in entry.Value.EnumerateObject())
                    if (language.Value.ValueKind == JsonValueKind.String) languages[language.Name] = language.Value.GetString()!;
                result[entry.Name] = languages;
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// 한국어 문장 → 고른 언어(번역이 없으면 영어, 그것도 없으면 한국어 그대로). {0} {1} … 에 값을 차례로 넣는다.
    public static string T(string korean, params object[] values)
    {
        var language = Current;
        var text = korean;
        if (language != "ko" && Table.TryGetValue(korean, out var translated))
            text = translated.GetValueOrDefault(language) ?? translated.GetValueOrDefault("en") ?? korean;
        for (var i = 0; i < values.Length; i++)
            text = text.Replace("{" + i + "}", Convert.ToString(values[i], CultureInfo.InvariantCulture));
        return text;
    }
}

public sealed record LimitWindow(string Id, int Minutes, double UsedPercent, DateTimeOffset? ResetsAt)
{
    /// 「5시간」「주간」 — 고른 언어로(언어를 바꾸면 바로 바뀌게 그때그때 만든다)
    public string Label => Fmt.WindowLabel(Minutes);

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

    /// Claude 데스크톱 앱의 사용량 기록 후보(있는 파일만). 앱의 데이터 폴더 이름은 Claude-Data·Claude·Claude-3p 이고
    /// %LOCALAPPDATA% 나 %APPDATA% 밑에 있다. 요즘 판(Microsoft Store 판 포함)은 %LOCALAPPDATA%\Claude-Data\claude_<id>\ 에 두고,
    /// 옛 자리(%APPDATA%\Claude, Packages\…Claude…\LocalCache\Roaming\Claude)에는 옮겼다는 빈 표시 파일만 남긴다.
    /// 맥에서 돌려 볼 때(AIUSAGE_HOME)는 Home 밑의 AppData\Local·AppData\Roaming·Library/Application Support 를 본다.
    public static List<string> ClaudeDesktopHistory()
    {
        var bases = new List<string>();
        if (Environment.GetEnvironmentVariable("AIUSAGE_HOME") is { Length: > 0 } || OperatingSystem.IsMacOS())
        {
            bases.Add(Path.Combine(Home, "AppData", "Local"));
            bases.Add(Path.Combine(Home, "AppData", "Roaming"));
            bases.Add(Path.Combine(Home, "Library", "Application Support"));
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            bases.Add(local);
            bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            try
            {
                var packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages))
                    bases.AddRange(Directory.EnumerateDirectories(packages, "*Claude_*").Select(dir => Path.Combine(dir, "LocalCache", "Roaming")));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        const string name = "plan-usage-history.json";
        var result = new List<string>();
        foreach (var root in bases.Where(b => b.Length > 0))
            foreach (var folder in new[] { "Claude-Data", "Claude", "Claude-3p" })
            {
                var dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                result.Add(Path.Combine(dir, name));
                try
                {
                    // Claude-Data 는 설치마다 하위 폴더(claude_<id>)를 둔다
                    result.AddRange(Directory.EnumerateDirectories(dir).Select(sub => Path.Combine(sub, name)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return result.Where(File.Exists).ToList();
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
    public static string WindowLabel(int minutes) => minutes switch
    {
        300 => T("5시간"),
        10_080 => T("주간"),
        > 0 when minutes % 1_440 == 0 => T("{0}일", minutes / 1_440),
        > 0 when minutes % 60 == 0 => T("{0}시간", minutes / 60),
        > 0 => T("{0}분", minutes),
        _ => T("한도"),
    };

    public static string? PlanName(string? raw) => raw switch
    {
        null or "" or "unknown" => null,
        "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro", "prolite" => "Pro Lite", "promax" => "Pro Max",
        "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", "edu" => "Edu",
        _ => raw,
    };

    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", L10n.Culture);
    public static string FullTime(DateTimeOffset at) => at.ToLocalTime().ToString(T("M월 d일(ddd) HH:mm"), L10n.Culture);

    public static string ResetText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        if (t <= now) return T("초기화됨");
        var local = t.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return local.ToString("HH:mm", L10n.Culture);
        return local.ToString((t - now).TotalDays < 6 ? "ddd HH:mm" : "M'/'d HH:mm", L10n.Culture);
    }

    public static string AgeText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        var seconds = Math.Max(0, (now - t).TotalSeconds);
        return seconds switch
        {
            < 60 => T("방금"),
            < 3_600 => T("{0}분 전", (int)(seconds / 60)),
            < 86_400 => T("{0}시간 전", (int)(seconds / 3_600)),
            _ => T("{0}일 전", (int)(seconds / 86_400)),
        };
    }

    public static string Describe(string title, ProviderSnapshot snap, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            T("{0} — 마지막 값 {1}", title + (snap.Plan is { } plan ? $" ({plan})" : ""), snap.ObservedAt is { } at ? FullTime(at) : T("없음")),
        };
        if (snap.Windows.Count == 0) lines.Add("  " + (snap.Message ?? T("값 없음")));
        foreach (var w in snap.Windows)
            lines.Add("  " + T("{0}: 사용 {1}% → {2}% 남음, 초기화 {3}", w.Label, w.UsedPercent, w.LeftPercent(now), w.ResetsAt is { } r ? FullTime(r) : "-"));
        return string.Join("\n", lines);
    }
}

// MARK: Claude

/// 세션마다 마지막으로 받은 한도를 모아, 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다.
/// 같은 창 안에서 사용률은 줄지 않으므로, 오래 쉬던 세션이 옛 값을 다시 써도 표시가 뒤로 가지 않는다.
/// 지금 로그인한 계정 값만 쓴다: 기록에 적힌 계정(account)이 다르면 빼고, 계정이 안 적힌 옛 기록은 계정을 바꾼 뒤 쓰인 것만(맥 판과 같다).
public sealed class ClaudeReader
{
    readonly Dictionary<string, (DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits, string? Account)> cache = new();

    public ProviderSnapshot Read(DateTimeOffset now)
    {
        var entries = new List<(DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits, string? Account)>();
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
                string? owner;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    limits = FromRateLimits(doc.RootElement);
                    owner = J.Str(doc.RootElement, "account");
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { continue; }
                cache[path] = (mtime, limits, owner);
                entries.Add((mtime, limits, owner));
            }
        }
        foreach (var stale in cache.Keys.Where(k => !alive.Contains(k)).ToList()) cache.Remove(stale);

        var me = ClaudeCodeAccount.Identity();
        var since = AccountSwitch.Since("claude", me, now);
        // 지금 계정이 적힌 기록이 있으면 계정이 안 적힌 옛 기록(이 판 전에 쓰인 것)은 쓰지 않는다
        var tagged = me is not null && entries.Any(e => e.Account == me);
        entries = entries.Where(e => e.Account is { } account
            ? account == me  // 로그아웃이면 어느 계정 것도 아니다
            : !tagged && new DateTimeOffset(e.Mtime, TimeSpan.Zero) >= since).ToList();

        // Claude Code 를 안 쓰는 동안에는 데스크톱 앱 기록이 더 새것일 수 있다(같은 계정일 때만)
        DateTimeOffset? statusAt = entries.Count > 0 ? new DateTimeOffset(entries.Max(e => e.Mtime), TimeSpan.Zero) : null;
        var desktop = ClaudeDesktopUsage.Latest(ClaudeCodeAccount.Organization(), now);
        var snap = new ProviderSnapshot();
        bool usedStatus = false, usedDesktop = false;
        foreach (var (key, minutes) in new[] { ("five_hour", 300), ("seven_day", 10_080) })
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
            snap.Windows.Add(new LimitWindow(key, minutes, u, J.Epoch(reset)));
        }
        var seen = new List<DateTimeOffset>();
        if (usedStatus && statusAt is { } s) seen.Add(s);
        if (usedDesktop && desktop is { } dd) seen.Add(dd.At);
        snap.ObservedAt = seen.Count > 0 ? seen.Max() : null;
        snap.Source = usedStatus && usedDesktop ? T("Claude Code·데스크톱 앱") : usedDesktop ? T("Claude 데스크톱 앱 기록") : T("Claude Code 상태 표시줄");
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
    /// 후보가 여럿이면(옛 자리에 남은 파일, 여러 설치) 모두 보고 가장 새 기록을 쓴다
    public static DesktopUsageSample? Latest(string? org, DateTimeOffset now)
    {
        DesktopUsageSample? best = null;
        foreach (var path in Paths.ClaudeDesktopHistory())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) continue;
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
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return best;
    }

    /// 가장 최근에 쓰인 기록 파일과 그 시각(값이 왜 멈췄는지 알리려고). 없으면 null
    public static (string Path, DateTimeOffset At)? LastWrite()
    {
        (string Path, DateTimeOffset At)? newest = null;
        foreach (var path in Paths.ClaudeDesktopHistory())
        {
            try
            {
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (newest is null || at > newest.Value.At) newest = (path, at);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return newest;
    }

    /// 데스크톱 앱은 트레이 아이콘의 사용량 메뉴를 한동안 열지 않으면 사용량을 받지 않아 기록이 멈춘다(앱 설정에 따라 다름).
    /// 기록이 이보다 오래 안 바뀌었으면 그렇게 알린다.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public static string? StaleHint(DateTimeOffset now) =>
        LastWrite() is { } last && now - last.At >= StaleAfter
            ? T("Claude 데스크톱 앱 기록이 {0} 뒤로 멈춤 — 작업 표시줄 알림 영역의 Claude 아이콘을 눌러 메뉴를 열면 다시 받습니다", Fmt.FullTime(last.At))
            : null;
}

/// Claude Code 가 로그인한 조직(~/.claude.json 의 oauthAccount.organizationUuid, 비밀값 아님).
/// 데스크톱 앱이 다른 계정이면 그 기록은 쓰지 않으려고 본다. 파일이 바뀔 때만 다시 읽는다.
public static class ClaudeCodeAccount
{
    static (DateTime Mtime, string? Org, string? Identity)? cached;

    public static string? Organization() => Read()?.Org;

    /// Claude Code 에 로그인한 계정 — 「사용자 id|조직 id」. 한도는 계정마다 따로라 이것으로 값을 가른다(로그인 정보가 없으면 null)
    public static string? Identity() => Read()?.Identity;

    static (string? Org, string? Identity)? Read()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeAccount)) return null;
            var mtime = File.GetLastWriteTimeUtc(Paths.ClaudeAccount);
            if (cached is { } hit && hit.Mtime == mtime) return (hit.Org, hit.Identity);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.ClaudeAccount));
            string? org = null, identity = null;
            if (J.Obj(doc.RootElement, "oauthAccount") is { } account)
            {
                org = J.Str(account, "organizationUuid");
                var user = J.Str(account, "accountUuid");
                if (user is not null || org is not null) identity = $"{user}|{org}";
            }
            cached = (mtime, org, identity);
            return (org, identity);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// 계정을 바꾼 때를 기억한다(~/.cache/ai-usage/accounts.json, 맥 판과 같은 파일 모양). 어느 계정 값인지 적혀 있지 않은 기록은
/// 마지막으로 계정이 바뀐 뒤에 쓰인 것만 지금 계정 값으로 본다. 처음 보는 계정이면 바뀐 때를 모르니 다 쓴다.
public static class AccountSwitch
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(Paths.Home, ".cache", "ai-usage", "accounts.json");

    public static DateTimeOffset Since(string kind, string? id, DateTimeOffset now)
    {
        if (id is null) return DateTimeOffset.MinValue;
        lock (Gate)
        {
            JsonObject root;
            try { root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { root = new JsonObject(); }
            var saved = root[kind] as JsonObject;
            if (saved is not null && saved["id"]?.GetValueKind() == JsonValueKind.String && (string?)saved["id"] == id)
            {
                var at = saved["since"]?.GetValueKind() == JsonValueKind.Number ? (double)saved["since"]! : 0;
                return at > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(at * 1000)) : DateTimeOffset.MinValue;
            }
            root[kind] = new JsonObject { ["id"] = id, ["since"] = saved is null ? 0 : now.ToUnixTimeMilliseconds() / 1000.0 };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, root.ToJsonString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return saved is null ? DateTimeOffset.MinValue : now;  // 처음이면 바뀐 때를 모른다
        }
    }
}

// MARK: ChatGPT(Codex)

public sealed class CodexReader
{
    string lastLogKey = "";
    string? account;
    bool accountKnown;
    readonly Dictionary<string, string?> logAccounts = new();  // 기록 파일 → 만든 계정(파일마다 한 번만 읽는다)

    public ProviderSnapshot? FromLog { get; private set; }
    public ProviderSnapshot? FromServer { get; private set; }
    public string? ServerError { get; private set; }
    public DateTimeOffset? ServerCheckedAt { get; private set; }

    /// 기록과 서버 가운데 더 나중에 확인한 값을 쓴다
    public ProviderSnapshot Current =>
        new[] { FromLog, FromServer }.OfType<ProviderSnapshot>().OrderByDescending(s => s.ObservedAt ?? DateTimeOffset.MinValue).FirstOrDefault()
        ?? new ProviderSnapshot { Message = ServerError ?? T("Codex 한도를 읽는 중…") };

    /// Codex 를 쓰는 동안 기록 파일에 찍히는 최신 한도(파일이 바뀌었을 때만 다시 읽는다).
    /// 지금 로그인한 계정(~/.codex/auth.json 의 account_id)이 만든 기록만 쓴다. 계정이 바뀌었으면 true —
    /// 앞 계정 값은 버렸으니 부른 쪽이 서버에 바로 다시 묻는다(맥 판과 같다).
    public bool ReadLog()
    {
        var me = CurrentAccount();
        var switched = false;
        if (!accountKnown || me != account)
        {
            switched = accountKnown && account is not null;  // 다른 계정으로 바꿨거나 로그아웃
            if (switched)
            {
                FromLog = null;
                FromServer = null;
                ServerError = null;
            }
            account = me;
            accountKnown = true;
        }
        var since = AccountSwitch.Since("codex", me, DateTimeOffset.Now);
        var files = RecentRolloutFiles(DateTimeOffset.Now);
        if (files.Count == 0) return switched;
        var key = $"{files[0].Path}|{files[0].Mtime.Ticks}|{me}";
        if (key == lastLogKey) return switched;
        lastLogKey = key;
        foreach (var gone in logAccounts.Keys.Where(k => files.All(f => f.Path != k)).ToList()) logAccounts.Remove(gone);
        foreach (var file in files.Take(10))
        {
            if (!logAccounts.TryGetValue(file.Path, out var owner)) logAccounts[file.Path] = owner = CreatorAccount(file.Path);
            if (owner is not null)
            {
                if (owner != me) continue;  // 다른 계정으로 쓴 기록(로그아웃·API 키면 어느 계정 것도 아니다)
            }
            else if (new DateTimeOffset(file.Mtime, TimeSpan.Zero) < since)
            {
                continue;  // 계정이 안 적힌 기록은 계정을 바꾼 뒤 것만
            }
            if (LastLimits(file.Path, file.Mtime) is { } snap)
            {
                FromLog = snap;
                return switched;
            }
        }
        FromLog = null;
        return switched;
    }

    /// 지금 Codex 에 로그인한 ChatGPT 계정(auth.json 의 tokens.account_id 만 읽는다). API 키 로그인이면 null
    static string? CurrentAccount()
    {
        try
        {
            var path = Path.Combine(Paths.Home, ".codex", "auth.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return J.Obj(doc.RootElement, "tokens") is { } tokens ? J.Str(tokens, "account_id") : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static readonly Regex CreatorPattern = new("\"creator_account_id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

    /// 기록 첫 줄(session_meta)의 creator_account_id — 그 기록을 만든 계정. 첫 줄은 길 수 있어 앞 1MB 에서 찾는다
    static string? CreatorAccount(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[(int)Math.Min(stream.Length, 1_000_000)];
            stream.ReadExactly(head);
            var newline = Array.IndexOf(head, (byte)'\n');
            var first = Encoding.UTF8.GetString(head, 0, newline >= 0 ? newline : head.Length);
            return CreatorPattern.Match(first) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
            windows.Add(new LimitWindow(key, minutes, used, J.Epoch(J.Number(w, "resetsAt", "resets_at"))));
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
        if (FindCodex() is not { } exe) return (null, T("codex 명령을 찾지 못했습니다(Codex CLI 설치 필요)"));
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
            return (null, T("codex 를 실행하지 못했습니다"));
        }
        process.ErrorDataReceived += (_, _) => { };  // 오류 출력이 쌓여 멈추지 않게 비워 둔다
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await Send(process, """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"ai-usage-pet","version":"1.0"}}}""");
            using (var init = await Receive(process, 1, timeout.Token))
                if (init is null) return (null, T("codex 가 응답하지 않습니다"));
            await Send(process, """{"method":"initialized"}""");
            await Send(process, """{"id":2,"method":"account/rateLimits/read"}""");
            using var response = await Receive(process, 2, timeout.Token);
            if (response is null) return (null, T("codex 가 응답하지 않습니다"));
            if (J.Obj(response.RootElement, "result") is not { } result) return (null, T("Codex 한도를 읽지 못했습니다(로그인 확인)"));

            var limits = (J.Obj(result, "rateLimitsByLimitId") is { } byId ? J.Obj(byId, "codex") : null)
                         ?? J.Obj(result, "rateLimits");
            var snap = new ProviderSnapshot
            {
                ObservedAt = DateTimeOffset.Now,
                Plan = limits is { } l ? Fmt.PlanName(J.Str(l, "planType")) : null,
            };
            if (limits is { } found) snap.Windows.AddRange(WindowsFrom(found));
            if (snap.Windows.Count == 0) snap.Message = T("표시할 한도가 없습니다");
            return (snap, null);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            return (null, T("codex 가 응답하지 않습니다"));
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
            var saved = JsonNode.Parse(limits.GetRawText()) as JsonObject ?? new JsonObject();
            if (ClaudeCodeAccount.Identity() is { } account) saved["account"] = account;  // 어느 계정 값인지(계정을 바꿔도 섞이지 않게)
            File.WriteAllText(temp, saved.ToJsonString());
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
            location.Add($"{T("저장소")} {White}{repoName}{Reset}");
        if (workspace is { } ws2 && J.Str(ws2, "git_worktree") is { Length: > 0 } worktree)
            location.Add($"{T("워크트리")} {Sky}{worktree}{Reset}");
        if (J.Obj(root, "pr") is { } pr && J.Number(pr, "number") is { } number)
            location.Add($"PR #{(int)number}");
        if (location.Count > 0) lines.Add(string.Join($" {Dim}·{Reset} ", location));

        // 2줄: 모델 · 추론 강도 │ 컨텍스트
        var model = J.Obj(root, "model") is { } m ? J.Str(m, "display_name") : null;
        var line = $"{Cyan}{model ?? "?"}{Reset}";
        if (J.Obj(root, "effort") is { } effort && J.Str(effort, "level") is { Length: > 0 } level)
            line += $" {Dim}·{Reset} {T("추론 강도")} {EffortColor(level)}{level}{Reset}";
        if (J.Obj(root, "context_window") is { } context && J.Number(context, "remaining_percentage") is { } remaining)
        {
            var left = (int)Math.Floor(remaining);
            line += $" {Dim}│{Reset} {T("컨텍스트")} {Color(left)}{T("{0}% 남음", left)}{Reset} "
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
                var text = $"{T("5시간 한도")} {Color(left)}{T("{0}% 남음", left)}{Reset}";
                if (J.Epoch(J.Number(five, "resets_at")) is { } at) text += $" {Dim}{T("({0} 초기화)", Fmt.Clock(at))}{Reset}";
                parts.Add(text);
            }
            if (J.Obj(limits, "seven_day") is { } week && J.Number(week, "used_percentage") is { } weekUsed)
            {
                var left = 100 - (int)Math.Floor(weekUsed);
                parts.Add($"{T("주간 한도")} {Color(left)}{T("{0}% 남음", left)}{Reset}");
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
        LinkState.NoClaudeCode => T("Claude Code 나 Claude 데스크톱 앱을 쓰면 표시됩니다"),
        LinkState.NotConnected => T("Claude Code 에 연결되지 않았습니다 — 오른쪽 클릭 → 「Claude Code 상태 표시줄 연결」"),
        LinkState.Broken => T("Claude Code 연결이 끊겼습니다 — 오른쪽 클릭 → 「Claude Code 상태 표시줄 연결」"),
        _ => LastSeen() is { Limits: false }
            ? T("Claude Code 가 한도 값을 보내지 않습니다 — Claude 구독(Pro·Max·Team)으로 로그인했는지, Claude Code 가 최신인지 확인")
            : T("Claude Code 에서 한 번 대화하면 표시됩니다"),
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
                   ?? throw new InvalidDataException(T("settings.json 이 JSON 객체가 아닙니다"));
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
            ClaudeSettings.LinkState.NoClaudeCode => T("Claude Code 없음(~\\.claude 폴더 없음)"),
            ClaudeSettings.LinkState.NotConnected => T("안 됨 — 지금 명령: {0}", ClaudeSettings.CurrentCommand() ?? T("없음")),
            ClaudeSettings.LinkState.Broken => T("끊김(가리키는 AIUsageStatus.exe 가 없음) — 지금 명령: {0}", ClaudeSettings.CurrentCommand() ?? ""),
            _ => T("됨"),
        };
        var lines = new List<string>
        {
            Fmt.Describe("Claude", claude, now),
            "  " + T("출처: {0} · Claude Code 연결: {1}", claude.Windows.Count == 0 ? "-" : claude.Source ?? "-", link),
        };
        if (ClaudeSettings.LastSeen() is { } seen)
            lines.Add("  " + T("상태 표시줄 마지막 실행 {0} · 한도 값 {1} · Claude Code {2}", Fmt.FullTime(seen.At), seen.Limits ? T("옴") : T("안 옴"), seen.Version));
        lines.Add("  " + (ClaudeDesktopUsage.LastWrite() is { } desktop
            ? T("데스크톱 앱 기록 {0} · 마지막으로 바뀜 {1}", desktop.Path, Fmt.FullTime(desktop.At))
            : T("데스크톱 앱 기록: 못 찾음")));
        if (ClaudeDesktopUsage.StaleHint(now) is { } hint) lines.Add("  " + hint);
        return string.Join("\n", lines.Concat(new[]
        {
            Fmt.Describe(T("ChatGPT · 기록"), fromLog ?? new ProviderSnapshot { Message = T("최근 기록 없음") }, now),
            Fmt.Describe(T("ChatGPT · 서버"), codex.FromServer ?? new ProviderSnapshot { Message = codex.ServerError ?? T("실패") }, now),
            Fmt.Describe(T("ChatGPT · 표시"), codex.Current, now),
        }));
    }
}
