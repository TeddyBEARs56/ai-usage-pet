// AI 사용량(Windows) — 값 읽기·Claude Code 상태 표시줄 처리. 맥판(AIUsagePet.swift)과 같은 규칙.
// 화면 코드와 나눠 두어 AIUsageStatus(콘솔)로 맥에서도 돌려 볼 수 있다.
//
// 값의 출처
//   Claude  : Claude Code 상태 표시줄(AIUsageStatus --statusline)이 세션마다 남기는 ~/.cache/ai-usage/claude/<세션>.json
//             + claude /usage 직접 조회(usage-command.json) + 데스크톱 기록 plan-usage-history.json(초기화 시각 없음)
//             (Claude Code 로그인 토큰은 읽지 않는다)
//   ChatGPT : `codex app-server` 의 account/rateLimits/read(5분마다) + ~/.codex/sessions 기록에 찍히는 최신 한도

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

/// Name 이 있으면 모델별 한도(예: Claude 의 「Fable」 주간) — 그 이름을 칸 이름으로 쓴다
public sealed record LimitWindow(string Id, int Minutes, double UsedPercent, DateTimeOffset? ResetsAt, string? Name = null)
{
    /// 「5시간」「주간」(고른 언어로, 언어를 바꾸면 바로 바뀌게 그때그때 만든다) 또는 모델 이름
    public string Label => Name ?? Fmt.WindowLabel(Minutes);

    /// 상태 표시줄과 같은 계산(100 - 사용률 내림). 초기화 시각이 지났으면 100.
    public int LeftPercent(DateTimeOffset now) =>
        ResetsAt is { } reset && reset <= now ? 100 : Math.Clamp(100 - (int)Math.Floor(UsedPercent), 0, 100);
}

public sealed class ProviderSnapshot
{
    internal List<UsageEvent> Observations { get; } = new();
    // Only the alert evaluator consumes these. History always receives the overall windows.
    internal Dictionary<string, UsageEvent> ModelObservations { get; } = new(StringComparer.Ordinal);
    public List<LimitWindow> Windows { get; } = new();
    public IEnumerable<LimitWindow> VisibleWindows(bool showModels) => Windows.Where(w => showModels || w.Name == null);
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
    public static List<string> ClaudeDesktopHistory() =>
        ClaudeDesktopDataDirs().Select(dir => Path.Combine(dir, "plan-usage-history.json")).Where(File.Exists).ToList();

    /// Claude 데스크톱 앱의 데이터 폴더 후보(있는 것만): Claude-Data·Claude·Claude-3p 와 그 바로 밑 폴더(claude_<id>)
    public static List<string> ClaudeDesktopDataDirs()
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

        var result = new List<string>();
        foreach (var root in bases.Where(b => b.Length > 0))
            foreach (var folder in new[] { "Claude-Data", "Claude", "Claude-3p" })
            {
                var dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                result.Add(dir);
                try
                {
                    // Claude-Data 는 설치마다 하위 폴더(claude_<id>)를 둔다
                    result.AddRange(Directory.EnumerateDirectories(dir));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
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
        seconds is { } s && s > 0 && s * 1000 <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)) : null;
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
        return local.ToString((t - now).TotalDays < 6 ? "ddd HH:mm" : "M'/'d", L10n.Culture);
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
/// `claude /usage` 로 받은 값(ClaudeUsageCommand)도 같은 폴더에 같은 모양으로 남아 함께 합쳐진다.
public sealed class ClaudeReader
{
    readonly Dictionary<string, (DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits, string? Account, bool Command)> cache = new();

    public ProviderSnapshot Read(DateTimeOffset now)
    {
        var entries = new List<(DateTime Mtime, Dictionary<string, (double Used, double Reset)> Limits, string? Account, bool Command)>();
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
                    if (mtime != File.GetLastWriteTimeUtc(path)) continue; // replaced during the read
                    limits = FromRateLimits(doc.RootElement);
                    owner = J.Str(doc.RootElement, "account");
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { continue; }
                var command = ClaudeUsageCommand.IsResultFile(path);
                cache[path] = (mtime, limits, owner, command);
                entries.Add((mtime, limits, owner, command));
            }
        }
        foreach (var stale in cache.Keys.Where(k => !alive.Contains(k)).ToList()) cache.Remove(stale);

        var me = ClaudeCodeAccount.Identity();
        var since = AccountSwitch.Since("claude", me, now);
        // 지금 계정이 적힌 기록이 있으면 계정이 안 적힌 옛 기록(이 판 전에 쓰인 것)은 쓰지 않는다
        var tagged = me is not null && entries.Any(e => e.Account == me);
        entries = entries.Where(e => e.Account is { } account
            ? account == me  // 로그아웃이면 어느 계정 것도 아니다
            : !e.Command && !tagged && new DateTimeOffset(e.Mtime, TimeSpan.Zero) >= since).ToList();

        // Claude Code 를 안 쓰는 동안에는 데스크톱 앱 기록이 더 새것일 수 있다(같은 계정일 때만)
        DateTimeOffset? statusAt = entries.Count > 0 ? new DateTimeOffset(entries.Max(e => e.Mtime), TimeSpan.Zero) : null;
        var desktop = ClaudeDesktopUsage.Latest(ClaudeCodeAccount.Organization(), now);
        var snap = new ProviderSnapshot();
        foreach (var entry in entries)
        {
            foreach (var (key, minutes) in new[] { ("five_hour", 300), ("seven_day", 10080) })
            {
                if (!entry.Limits.TryGetValue(key, out var value)) continue;
                var w = new LimitWindow(key,minutes,value.Used,J.Epoch(value.Reset));
                snap.Observations.AddRange(UsageEvent.From([w], new DateTimeOffset(entry.Mtime), "claude", me,
                    entry.Command ? ClaudeUsageCommand.HistorySource : "claude-session"));
            }
        }
        if (desktop is { Org:not null } sample && sample.Org == ClaudeCodeAccount.Organization() && sample.At >= since)
        {
            foreach (var (key,used) in sample.Used)
            {
                var w = new LimitWindow(key,key == "five_hour" ? 300 : 10080,used,null);
                snap.Observations.AddRange(UsageEvent.From(new[] { w },sample.At,"claude",me,"claude-desktop"));
            }
        }
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
                used = found.Where(f => f.Reset >= latest - Generations.Tolerance / 1000.0).Max(f => f.Used);
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
        // 모델별 주간 한도(예: Fable)는 `claude /usage` 에만 있다: 1시간 안에 받은 가장 새 /usage 기록의 것을 주간 아래에 붙인다
        var latestCommand = entries.Where(e => e.Command).OrderByDescending(e => e.Mtime).FirstOrDefault();
        if (latestCommand.Limits is { } commandLimits && latestCommand.Mtime <= now.UtcDateTime)
        {
            var at = new DateTimeOffset(latestCommand.Mtime);
            foreach (var (key, (used, reset)) in commandLimits.Where(kv => kv.Key.StartsWith(ModelPrefix, StringComparison.Ordinal))
                                                              .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var name = key[ModelPrefix.Length..];
                var window = new LimitWindow(key, 10080, used, J.Epoch(reset), name);
                if (UsageEvent.From([window], at, "claude", me, ClaudeUsageCommand.HistorySource).FirstOrDefault() is { } observation)
                    snap.ModelObservations[name] = observation;
                if (now - at <= TimeSpan.FromHours(1)) snap.Windows.Add(window);
            }
            if (snap.Windows.Any(w => w.Name != null)) seen.Add(at);
        }
        snap.ObservedAt = seen.Count > 0 ? seen.Max() : null;
        // 가장 새 기록이 `claude /usage` 로 받은 것이면 그렇게 적는다
        var statusName = entries.Count > 0 && entries.MaxBy(e => e.Mtime).Command ? T("Claude Code /usage") : T("Claude Code 상태 표시줄");
        snap.Source = usedStatus && usedDesktop ? T("Claude Code·데스크톱 앱") : usedDesktop ? T("Claude 데스크톱 앱 기록") : statusName;
        if (snap.Windows.Count == 0) snap.Message = ClaudeSettings.EmptyMessage();
        return snap;
    }

    /// 모델별 주간 한도의 열쇠 앞머리(「seven_day:Fable」)
    public const string ModelPrefix = "seven_day:";

    static Dictionary<string, (double Used, double Reset)> FromRateLimits(JsonElement limits)
    {
        var result = new Dictionary<string, (double Used, double Reset)>();
        foreach (var key in new[] { "five_hour", "seven_day" })
            if (J.Obj(limits, key) is { } w && J.Number(w, "used_percentage") is { } used)
                result[key] = (used, J.Number(w, "resets_at") ?? 0);
        // `claude /usage` 기록에만 있는 모델별 주간: {"models":{"Fable":{"used_percentage":89,"resets_at":…}}}
        if (J.Obj(limits, "models") is { } models)
            foreach (var model in models.EnumerateObject())
                if (model.Value.ValueKind == JsonValueKind.Object && J.Number(model.Value, "used_percentage") is { } used && model.Name.Length > 0)
                    result[ModelPrefix + model.Name] = (used, J.Number(model.Value, "resets_at") ?? 0);
        return result;
    }
}

// MARK: Claude Code /usage

/// `claude -p "/usage"` 의 결과. ModelCalled 면 /usage 가 모델을 불렀다(한도를 썼다)는 뜻이라 다시 부르지 않는다.
public sealed record ClaudeUsageResult(Dictionary<string, (double Used, double Reset)>? Limits, string? Error, bool ModelCalled = false);

/// `claude -p "/usage"` 로 지금 한도를 묻는다. Codex 의 app-server 처럼 인증은 Claude Code 가 알아서 한다(로그인 토큰을 읽지 않는다).
/// /usage 는 모델을 부르지 않는 Claude Code 안의 명령(local_command)이라 한도를 쓰지 않는다. 혹시 모델을 불렀으면(num_turns·비용 > 0)
/// 다시는 부르지 않게 끄고(~/.cache/ai-usage/claude-usage-disabled) 기록한다.
/// 받은 값은 상태 표시줄 기록과 같은 모양으로 ~/.cache/ai-usage/claude/usage-command.json 에 남겨 같은 규칙(계정·창)으로 합친다.
/// 대화 기록을 남기지 않게 --no-session-persistence 로, 늘 같은 폴더(~/.cache/ai-usage/claude-usage)에서 띄운다.
/// 결과는 사람이 읽는 영어 문장이라 Claude Code 업데이트로 바뀔 수 있다 — 못 읽으면 이 출처만 건너뛰고 claude-usage.log 에 남긴다.
public static class ClaudeUsageCommand
{
    public const string FileName = "usage-command.json";
    internal const string HistorySource = "claude-usage";
    internal static bool IsResultFile(string path) => string.Equals(Path.GetFileName(path), FileName,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static string CacheDir => Path.Combine(Paths.Home, ".cache", "ai-usage");
    public static string ResultPath => Path.Combine(Paths.ClaudeCache, FileName);
    public static string LogPath => Path.Combine(CacheDir, "claude-usage.log");
    public static string DisabledPath => Path.Combine(CacheDir, "claude-usage-disabled");
    static string WorkDir => Path.Combine(CacheDir, "claude-usage");

    /// 대화가 이어질 때 너무 자주 묻지 않게: 두 번 사이 최소 간격
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    /// 이 PC 밖(claude.ai 웹·모바일·데스크톱 앱 Chat)에서 쓴 것은 알 길이 없어 이만큼마다 묻는다
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    static readonly SemaphoreSlim gate = new(1, 1);
    static volatile string? processStopReason;
    static string? lastLoggedError;
    static Dictionary<string, (double Used, double Reset)>? lastLoggedLimits;
    public static DateTimeOffset? LastCheckedAt { get; private set; }
    public static string? LastError { get; private set; }

    /// 꺼져 있으면 그 이유. AIUSAGE_CLAUDE_USAGE=0 이거나, /usage 가 모델을 부른 적이 있어 스스로 껐을 때
    public static string? DisabledReason()
    {
        if (processStopReason is { } reason) return reason;
        if (Environment.GetEnvironmentVariable("AIUSAGE_CLAUDE_USAGE") == "0") return T("꺼 둠(AIUSAGE_CLAUDE_USAGE=0)");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\AIUsage");
                if (key?.GetValue("ClaudeUsage") is 0) return T("꺼 둠(ClaudeUsage=0)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return T("/usage 설정을 읽지 못했습니다: {0}", e.Message);
            }
        }
        if (File.Exists(DisabledPath)) return T("꺼짐 — /usage 가 모델을 불러 멈췄습니다({0} 를 지우면 다시 켜짐)", DisabledPath);
        return null;
    }

    public static string? FindClaude()
    {
        var home = Paths.Home;
        var candidates = new List<string>();
        string[] names;
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));
            names = new[] { "claude.exe", "claude.cmd" };
        }
        else
        {
            candidates.Add(Path.Combine(home, ".local", "bin", "claude"));
            candidates.Add(Path.Combine(home, ".claude", "local", "claude"));
            candidates.Add("/opt/homebrew/bin/claude");
            candidates.Add("/usr/local/bin/claude");
            names = new[] { "claude" };
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                candidates.Add(Path.Combine(dir.Trim('"'), name));
        return candidates.FirstOrDefault(File.Exists) ?? DesktopBundled();
    }

    /// Claude 데스크톱 앱이 Code 탭용으로 품고 있는 Claude Code(claude-code\<버전>\<id>\claude.exe) 중 가장 높은 버전.
    /// Claude Code CLI 를 따로 깔지 않고 데스크톱 앱만 쓰는 사람도 /usage 를 물을 수 있게.
    static string? DesktopBundled()
    {
        var exeName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var found = new List<(Version Version, string Path)>();
        foreach (var dir in Paths.ClaudeDesktopDataDirs())
        {
            var root = Path.Combine(dir, "claude-code");
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var versionDir in Directory.EnumerateDirectories(root))
                {
                    if (!Version.TryParse(Path.GetFileName(versionDir), out var version)) continue;
                    foreach (var build in Directory.EnumerateDirectories(versionDir))
                        if (File.Exists(Path.Combine(build, exeName))) found.Add((version, Path.Combine(build, exeName)));
                    if (File.Exists(Path.Combine(versionDir, exeName))) found.Add((version, Path.Combine(versionDir, exeName)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.Count > 0 ? found.MaxBy(f => f.Version).Path : null;
    }

    /// 묻고 결과를 남긴다. 성공하면 null, 아니면 이유. 이미 묻는 중이면 기다리지 않고 null.
    /// reason 은 기록(claude-usage.log)에 남길 계기(시작·활동·5분·새로고침 등).
    public static async Task<string?> FetchAsync(string reason)
    {
        if (DisabledReason() is { } off)
        {
            LastError = off;
            return off;
        }
        if (!await gate.WaitAsync(0)) return null;
        try
        {
            if (DisabledReason() is { } stopped) return LastError = stopped;
            CleanOldTemps();
            var watch = Stopwatch.StartNew();
            var before = ClaudeCodeAccount.Identity();
            var result = await RunAsync();
            LastCheckedAt = DateTimeOffset.Now;
            var error = result.Error;
            if (result.ModelCalled)
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(DisabledPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} /usage 가 모델을 불렀다(num_turns·비용 > 0). 이 파일을 지우면 다시 묻는다.\n");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error = T("/usage 가 모델을 불러 이 실행에서 중지했습니다. 중지 파일을 저장하지 못했습니다: {0}", e.Message);
                    processStopReason = error;
                }
            }
            else if (error is null && result.Limits is { } limits)
            {
                var after = ClaudeCodeAccount.Identity();
                if (after is null) error = T("Claude Code 에 로그인되어 있지 않습니다");
                else if (after != before) error = T("묻는 동안 계정이 바뀌었습니다");
                else error = Save(limits, after);
            }
            LastError = error;
            Log(reason, result.Limits, error, watch.Elapsed.TotalSeconds);
            return error;
        }
        finally
        {
            gate.Release();
        }
    }

    static async Task<ClaudeUsageResult> RunAsync()
    {
        if (FindClaude() is not { } exe) return new(null, T("claude 명령을 찾지 못했습니다(Claude Code 또는 Claude 데스크톱 앱 필요)"));
        try { Directory.CreateDirectory(WorkDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(null, T("claude 를 실행하지 못했습니다")); }
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"\"{exe}\" -p /usage --output-format json --no-session-persistence\"";
        }
        else
        {
            start.FileName = exe;
            foreach (var arg in new[] { "-p", "/usage", "--output-format", "json", "--no-session-persistence" }) start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(null, T("claude 를 실행하지 못했습니다"));
        }
        process.StandardInput.Close();  // 입력을 기다리지 않게
        process.ErrorDataReceived += (_, _) => { };  // 오류 출력이 쌓여 멈추지 않게 비워 둔다
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(output, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            return new(null, T("claude 가 응답하지 않습니다"));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// `claude -p "/usage" --output-format json` 의 출력을 읽는다(시험할 수 있게 따로 둔다)
    public static ClaudeUsageResult Parse(string output, DateTimeOffset now)
    {
        JsonDocument? doc = null;
        foreach (var raw in output.Split('\n').Reverse())
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try { doc = JsonDocument.Parse(line); break; } catch (JsonException) { }
        }
        if (doc is null) return new(null, T("claude 의 답을 읽지 못했습니다"));
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, T("claude 의 답을 읽지 못했습니다"));
            // 모델을 불렀다면(한도를 썼다면) 값이 맞아도 쓰지 않고 멈춘다
            if ((J.Number(root, "num_turns") ?? 0) > 0 || (J.Number(root, "total_cost_usd") ?? 0) > 0)
                return new(null, T("/usage 가 모델을 불렀습니다 — 한도를 쓰지 않도록 멈춥니다"), ModelCalled: true);
            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (isError || J.Str(root, "local_command") != "usage") return new(null, T("claude 가 /usage 를 실행하지 못했습니다"));
            var text = J.Str(root, "result") ?? "";
            var limits = ParseText(text, now);
            if (limits.Count > 0) return new(limits, null);
            return text.Contains("Current ", StringComparison.Ordinal)
                ? new(null, T("/usage 결과 형식을 읽지 못했습니다(Claude Code 업데이트로 바뀌었을 수 있음)"))
                : new(null, T("/usage 에 한도가 없습니다 — Claude 구독(Pro·Max·Team)으로 로그인했는지 확인"));
        }
    }

    // 「Current session: 8% used · resets Nov 20, 3:10pm (Asia/Seoul)」「Current week (all models): 70% used · resets …」
    // 「Current week (Fable): 89% used · resets …」(모델별 주간 — 옛 판은 「(Sonnet only)」)
    static readonly Regex UsageLine = new(@"^Current (?:(?<session>session)|week \((?<scope>[^)]+)\))\s*:\s*(?<used>\d+(?:\.\d+)?)\s*%\s*used(?:\s*·\s*resets\s+(?<reset>.+?))?\s*$",
                                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// 열쇠: five_hour, seven_day, 모델별 주간은 「seven_day:<모델>」(ClaudeReader.ModelPrefix)
    public static Dictionary<string, (double Used, double Reset)> ParseText(string text, DateTimeOffset now)
    {
        var result = new Dictionary<string, (double Used, double Reset)>();
        foreach (var raw in text.Split('\n'))
        {
            var match = UsageLine.Match(raw.Trim());
            if (!match.Success) continue;
            string key;
            if (match.Groups["session"].Success) key = "five_hour";
            else
            {
                var scope = match.Groups["scope"].Value.Trim();
                if (scope.Equals("all models", StringComparison.OrdinalIgnoreCase)) key = "seven_day";
                else
                {
                    if (scope.EndsWith(" only", StringComparison.OrdinalIgnoreCase)) scope = scope[..^5].TrimEnd();
                    if (scope.Length == 0) continue;
                    key = ClaudeReader.ModelPrefix + scope;
                }
            }
            if (!double.TryParse(match.Groups["used"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var used)
                || !double.IsFinite(used)) continue;
            var reset = match.Groups["reset"].Success ? ParseReset(match.Groups["reset"].Value, now) ?? 0 : 0;
            result[key] = (used, reset);
        }
        return result;
    }

    // 「Nov 20, 3:10pm (Asia/Seoul)」「Nov 22, 10am (Asia/Seoul)」「3:10pm」「Dec 1, 2027, 9:00am」
    static readonly Regex ResetText = new(
        @"^(?:(?<mon>[A-Za-z]{3})[A-Za-z]*\.?\s+(?<day>\d{1,2})(?:,\s*(?<year>\d{4}))?,?\s+(?:at\s+)?)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>[ap]\.?m\.?)?\s*(?:\((?<tz>[^)]+)\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// 초기화 시각 글을 유닉스 초로. 해가 없으면 지금에 가장 가까운 앞날로, 날짜가 없으면 오늘(지났으면 내일)로 본다. 못 읽으면 null.
    public static double? ParseReset(string text, DateTimeOffset now)
    {
        var match = ResetText.Match(text.Trim());
        if (!match.Success) return null;
        var zone = TimeZoneInfo.Local;
        if (match.Groups["tz"].Success)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["tz"].Value.Trim()); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return null;
            var pm = match.Groups["ap"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            hour = hour % 12 + (pm ? 12 : 0);
        }
        if (hour > 23 || minute > 59) return null;

        int year = local.Year, month = local.Month, day = local.Day;
        var hasDate = match.Groups["mon"].Success;
        if (hasDate)
        {
            month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                                    name => name.Length > 0 && name.Equals(match.Groups["mon"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            if (month < 1) return null;
            day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        }
        var hasYear = match.Groups["year"].Success;
        if (hasYear) year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);

        DateTimeOffset? At(int y, int mo, int d)
        {
            if (y is < 1 or > 9999 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
            var wall = new DateTime(y, mo, d, hour, minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }
        if (At(year, month, day) is not { } at) return null;
        if (!hasDate && at < now) at = at.AddDays(1);                                   // 시각만 있으면 다음에 오는 그 시각
        else if (hasDate && !hasYear && at < now - TimeSpan.FromDays(1) && At(year + 1, month, day) is { } next) at = next;  // 12월 → 1월
        return at.ToUnixTimeSeconds();
    }

    /// 상태 표시줄 기록과 같은 모양으로 남긴다(바꿔치기로 써서 읽는 쪽이 반쯤 쓴 파일을 보지 않게)
    static string? Save(Dictionary<string, (double Used, double Reset)> limits, string account)
    {
        var temp = Path.Combine(Paths.ClaudeCache, $".usage-command.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Paths.ClaudeCache);
            var node = new JsonObject();
            var models = new JsonObject();
            foreach (var (key, (used, reset)) in limits)
            {
                var window = new JsonObject { ["used_percentage"] = used, ["resets_at"] = reset };
                if (key.StartsWith(ClaudeReader.ModelPrefix, StringComparison.Ordinal)) models[key[ClaudeReader.ModelPrefix.Length..]] = window;
                else node[key] = window;
            }
            if (models.Count > 0) node["models"] = models;
            node["account"] = account;
            node["source"] = "usage";
            File.WriteAllText(temp, node.ToJsonString());
            if (ClaudeCodeAccount.Identity() != account) return T("묻는 동안 계정이 바뀌었습니다");
            File.Move(temp, ResultPath, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return T("결과를 남기지 못했습니다: {0}", e.Message);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void CleanOldTemps()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(Paths.ClaudeCache, ".usage-command.*.tmp"))
                if (Regex.IsMatch(Path.GetFileName(path), @"^\.usage-command\.[0-9]+\.[0-9a-f]{32}\.tmp$")
                    && File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// 두 결과가 같은 값인지. 서버가 주는 초기화 시각은 같은 창이어도 1분쯤 오가므로(15:09 ↔ 15:10) 2분 안의 차이는 같은 것으로 본다.
    public static bool SameLimits(Dictionary<string, (double Used, double Reset)>? a, Dictionary<string, (double Used, double Reset)>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (key, x) in a)
            if (!b.TryGetValue(key, out var y) || x.Used != y.Used || Math.Abs(x.Reset - y.Reset) > 120) return false;
        return true;
    }

    /// 값이 바뀌었거나 실패했을 때만 한 줄 남긴다(같은 값·같은 실패가 이어지면 남기지 않는다). 256KB 를 넘으면 뒤쪽 절반만 남긴다.
    static void Log(string reason, Dictionary<string, (double Used, double Reset)>? limits, string? error, double seconds)
    {
        if (error is not null ? error == lastLoggedError : lastLoggedError is null && SameLimits(limits, lastLoggedLimits)) return;
        lastLoggedError = error;
        lastLoggedLimits = error is null ? limits : null;
        static string Name(string key) => key == "five_hour" ? "5시간" : key == "seven_day" ? "주간" : key.Replace(ClaudeReader.ModelPrefix, "주간 ");
        var summary = error ?? string.Join(" · ", (limits ?? new()).OrderBy(kv => kv.Key == "five_hour" ? 0 : kv.Key == "seven_day" ? 1 : 2)
                                                                   .ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            $"{Name(kv.Key)} {kv.Value.Used.ToString(CultureInfo.InvariantCulture)}%"
            + (J.Epoch(kv.Value.Reset) is { } r ? $"(초기화 {r.ToLocalTime():MM-dd HH:mm})" : "")));
        try
        {
            Directory.CreateDirectory(CacheDir);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 256 * 1024)
            {
                var text = File.ReadAllText(LogPath);
                var cut = text.IndexOf('\n', text.Length / 2);
                File.WriteAllText(LogPath, cut >= 0 ? text[(cut + 1)..] : "");
            }
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{reason}] {summary} ({seconds.ToString("0.0", CultureInfo.InvariantCulture)}초)\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// Claude Code 가 응답할 때마다 대화 기록(~/.claude/projects/…/*.jsonl)에 바로 쓰는 것을 지켜본다 — CLI·데스크톱 앱 Code 탭 모두.
/// 바뀐 때만 적어 두고, 받는 쪽(창의 타이머)이 Take() 로 가져가 /usage 를 묻는다. 폴더가 아직 없거나 감시가 끊기면 Ensure() 가 다시 건다.
public sealed class ClaudeActivityWatcher : IDisposable
{
    FileSystemWatcher? watcher;
    long firstTicks, lastTicks;  // 아직 가져가지 않은 활동의 처음·마지막(UTC ticks, 0 이면 없음)
    bool broken;

    public void Ensure()
    {
        if (watcher is not null && !broken) return;
        watcher?.Dispose();
        watcher = null;
        broken = false;
        var dir = Path.Combine(Paths.ClaudeDir, "projects");
        if (!Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += (_, _) => Mark();
            w.Created += (_, _) => Mark();
            w.Renamed += (_, _) => Mark();
            w.Error += (_, _) => { broken = true; Mark(); };  // 너무 많이 바뀌어 놓친 것도 활동으로 본다
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException) { }
    }

    void Mark()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.CompareExchange(ref firstTicks, now, 0);
        Interlocked.Exchange(ref lastTicks, now);
    }

    /// 아직 가져가지 않은 활동(처음·마지막). 없으면 null
    public (DateTimeOffset First, DateTimeOffset Last)? Pending()
    {
        var first = Interlocked.Read(ref firstTicks);
        var last = Interlocked.Read(ref lastTicks);
        return first == 0 ? null : (new DateTimeOffset(first, TimeSpan.Zero), new DateTimeOffset(last, TimeSpan.Zero));
    }

    /// 활동을 가져갔다(이제 /usage 를 묻는다)
    public void Take()
    {
        Interlocked.Exchange(ref firstTicks, 0);
        Interlocked.Exchange(ref lastTicks, 0);
    }

    /// 지금 /usage 를 물을 때인지와 그 계기. 처음, 5분이 지났을 때, 또는 활동이 있었고 1분이 지났으며
    /// 활동이 5초 잠잠하거나(응답이 끝남) 30초 넘게 이어질 때(긴 작업 중에도 갱신)
    public static string? Due(DateTimeOffset now, DateTimeOffset? lastAsked, (DateTimeOffset First, DateTimeOffset Last)? pending)
    {
        if (lastAsked is not { } asked) return "시작";
        if (now - asked >= ClaudeUsageCommand.FallbackInterval) return "5분";
        if (pending is { } p && now - asked >= ClaudeUsageCommand.MinInterval
            && (now - p.Last >= TimeSpan.FromSeconds(5) || now - p.First >= TimeSpan.FromSeconds(30)))
            return "활동";
        return null;
    }

    public void Dispose() => watcher?.Dispose();
}

public sealed record DesktopUsageSample(DateTimeOffset At, Dictionary<string, double> Used, string? Org);

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
                    if (used.Count > 0) best = new DesktopUsageSample(at, used, J.Str(sample,"org"));
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

    /// 데스크톱 앱은 트레이 아이콘을 오른쪽 클릭한 뒤 24시간 동안만 사용량을 계속 받고(서버 설정 pollRequiresTrayOpenWithinHours), 그 밖에는 앱을 켤 때 한 번만 받아 기록이 멈춘다.
    /// 기록이 이보다 오래 안 바뀌었으면 그렇게 알린다.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public static string? StaleHint(DateTimeOffset now) =>
        LastWrite() is { } last && now - last.At >= StaleAfter
            ? T("Claude 데스크톱 앱 기록이 {0} 뒤로 멈춤 — 작업 표시줄 알림 영역의 Claude 아이콘을 오른쪽 클릭하면 그 뒤 하루 동안 다시 받습니다", Fmt.FullTime(last.At))
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
    public bool ReadLog(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;
        var me = CurrentAccount();
        var switched = false;
        if (!accountKnown || me != account)
        {
            switched = accountKnown && account is not null;  // 다른 계정으로 바꿨거나 로그아웃
            FromLog = null;
            FromServer = null;
            ServerError = null;
            account = me;
            accountKnown = true;
        }
        var since = AccountSwitch.Since("codex", me, now);
        var files = RecentRolloutFiles(now);
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
            if (LastLimits(file.Path, file.Mtime, owner) is { } snap)
            {
                FromLog = snap;
                return switched;
            }
        }
        FromLog = null;
        return switched;
    }

    /// 지금 Codex 에 로그인한 ChatGPT 계정(auth.json 의 tokens.account_id 만 읽는다). API 키 로그인이면 null
    internal static string? CurrentAccount()
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

    static string? CreatorAccount(string path)
    {
        try
        {
            using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            using var header = JsonDocument.Parse(reader.ReadLine() ?? "");
            return J.Str(header.RootElement,"type") == "session_meta" && J.Obj(header.RootElement,"payload") is { } meta
                ? J.Str(meta,"creator_account_id") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public bool ApplyServer((ProviderSnapshot? Snap, string? Error) result, string? owner)
    {
        if (owner != CurrentAccount()) { ReadLog(); return false; }
        ServerCheckedAt = DateTimeOffset.Now;
        if (result.Snap is { } snap)
        {
            if (snap.ObservedAt is { } at)
                snap.Observations.AddRange(UsageEvent.From(snap.Windows,at,"codex",owner,"codex-server"));
            FromServer = snap;
            ServerError = null;
        }
        else
        {
            ServerError = result.Error;
        }
        return true;
    }

    internal static List<LimitWindow> WindowsFrom(JsonElement limits)
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

    static ProviderSnapshot? LastLimits(string path, DateTime mtime, string? owner)
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
                if (J.Epoch(J.Number(doc.RootElement,"timestamp")) is { } eventAt)
                    snap.Observations.AddRange(UsageEvent.From(windows,eventAt,"codex",owner,"codex-rollout"));
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var owner = CodexReader.CurrentAccount();
            if (codex.ApplyServer(await CodexReader.FetchServerAsync(),owner)) break;
        }
        var fromLog = codex.FromLog;
        var usageWatch = Stopwatch.StartNew();
        var usageError = await ClaudeUsageCommand.FetchAsync("확인(--print)");
        var usageSeconds = usageWatch.Elapsed.TotalSeconds;
        now = DateTimeOffset.Now;
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
            "  " + T("Claude Code /usage: {0}", usageError ?? T("됨 · {0}초 · {1}", usageSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                                                                       ClaudeUsageCommand.FindClaude() ?? "-")),
        };
        if (ClaudeSettings.LastSeen() is { } seen)
            lines.Add("  " + T("상태 표시줄 마지막 실행 {0} · 한도 값 {1} · Claude Code {2}", Fmt.FullTime(seen.At), seen.Limits ? T("옴") : T("안 옴"), seen.Version));
        lines.Add("  " + (ClaudeDesktopUsage.LastWrite() is { } desktop
            ? T("데스크톱 앱 기록 {0} · 마지막으로 바뀜 {1}", desktop.Path, Fmt.FullTime(desktop.At))
            : T("데스크톱 앱 기록: 못 찾음")));
        // Claude 값이 새것이면(예: /usage 로 받음) 데스크톱 기록이 멈춰 있어도 알릴 까닭이 없다
        if ((claude.ObservedAt is not { } observed || now - observed >= ClaudeDesktopUsage.StaleAfter)
            && ClaudeDesktopUsage.StaleHint(now) is { } hint)
            lines.Add("  " + hint);
        return string.Join("\n", lines.Concat(new[]
        {
            Fmt.Describe(T("ChatGPT · 기록"), fromLog ?? new ProviderSnapshot { Message = T("최근 기록 없음") }, now),
            Fmt.Describe(T("ChatGPT · 서버"), codex.FromServer ?? new ProviderSnapshot { Message = codex.ServerError ?? T("실패") }, now),
            Fmt.Describe(T("ChatGPT · 표시"), codex.Current, now),
        }));
    }
}

// Weekly source history. All on-disk timestamps are UTC Unix milliseconds.
internal sealed record UsageAccount(string? Claude, string? Org, string? Codex, long Since)
{
    internal static string? Hash(string provider, string? id) => string.IsNullOrEmpty(id) ? null
        : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\0" + id)));
    internal Dictionary<string, string> Keys()
    {
        var keys = new Dictionary<string, string>();
        if (Hash("claude", Claude) is { } c) keys["claude"] = c;
        if (Hash("codex", Codex) is { } x) keys["codex"] = x;
        return keys;
    }
    internal static UsageAccount Read(long now)
    {
        var id = ClaudeCodeAccount.Identity();
        long since = 0;
        var root = UsageHistory.ReadObject(Path.Combine(UsageHistory.DirectoryPath, "accounts.json"));
        if (root is { } obj && J.Obj(obj, "claude") is { } saved)
            since = J.Str(saved, "id") == id ? (long)((J.Number(saved, "since") ?? 0) * 1000) : now;
        return new(id, ClaudeCodeAccount.Organization(), CodexReader.CurrentAccount(), since);
    }
}

internal sealed record UsageEvent(string Provider, string Account, int Minutes, long AtMs,
                                  [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ResetMs, double Used,
                                  string Source, string Quality, string? Slot = null, int V = 1)
{
    internal bool Valid => V == 1 && Provider is "claude" or "codex" && Account?.Length == 64 && Minutes >= 0 && (Minutes > 0 || ResetMs.HasValue)
        && AtMs is > 0 and <= 253402300799999 && ResetMs is not (<= 0 or > 253402300799999) && Used is >= 0 and <= 100 && Quality is "rough" or "observed";
    internal static List<UsageEvent> LiveRows(ProviderSnapshot claude, CodexReader codex) =>
        claude.Observations.Concat(codex.FromLog?.Observations ?? []).Concat(codex.FromServer?.Observations ?? []).ToList();
    internal UsageEvent Key => this with { Slot = null, Quality = "" };
    internal static IOrderedEnumerable<UsageEvent> Ordered(IEnumerable<UsageEvent> rows) =>
        rows.OrderBy(e => e.AtMs).ThenBy(e => e.Used).ThenBy(e => e.ResetMs ?? long.MaxValue)
            .ThenBy(e => e.Quality != "observed").ThenBy(e => e.Source, StringComparer.Ordinal);
    internal static IEnumerable<UsageEvent> From(IEnumerable<LimitWindow> windows, DateTimeOffset at, string provider, string? id, string source)
    {
        if (UsageAccount.Hash(provider, id) is not { } key) yield break;
        foreach (var w in windows)
        {
            var r = w.ResetsAt?.ToUnixTimeMilliseconds();
            if (source == ClaudeUsageCommand.HistorySource && r > at.ToUnixTimeMilliseconds() + w.Minutes * 60_000L) r = null;
            var e = new UsageEvent(provider, key, w.Minutes, at.ToUnixTimeMilliseconds(), r > 0 ? r : null, w.UsedPercent,
                source, provider == "claude" && r is not > 0 ? "rough" : "observed", w.Id);
            if (e.Valid) yield return e;
        }
    }
}

internal sealed class SourceScan
{
    Dictionary<string, (long Size, long Ticks)> stamps = new();
    UsageAccount? account;
    internal SourceScan Copy() => new() { stamps = new(stamps), account = account };
    internal List<UsageEvent> Read(UsageAccount user, long now)
    {
        if (account != user) { stamps.Clear(); account = user; }
        var rows = new List<UsageEvent>();
        void Scan(string path, Func<FileInfo, bool> read)
        {
            var start = rows.Count;
            var accepted = false;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return;
                var before = (file.Length, file.LastWriteTimeUtc.Ticks);
                if (stamps.TryGetValue(path, out var stamp) && stamp == before) return;
                var complete = read(file);
                file.Refresh();
                accepted = file.Exists && before == (file.Length, file.LastWriteTimeUtc.Ticks);
                if (accepted && complete) stamps[path] = before;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException) { }
            finally { if (!accepted) rows.RemoveRange(start,rows.Count-start); }
        }
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var keys = user.Keys();
        if (keys.TryGetValue("claude", out var key))
        {
            if (Directory.Exists(Paths.ClaudeCache))
                foreach (var path in Directory.EnumerateFiles(Paths.ClaudeCache, "*.json").Where(p => !Path.GetFileName(p).StartsWith('.')))
                    Scan(path, f =>
                    {
                        var at = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        if (at < now - 8 * UsageHistory.Day) return true;
                        if (at > now || UsageHistory.ReadObject(path) is not { } obj) return false;
                        bool command = ClaudeUsageCommand.IsResultFile(path);
                        if (J.Str(obj, "account") is { } id ? id != user.Claude : command || at < user.Since) return true;
                        if (J.Obj(obj, "seven_day") is { } w && J.Number(w, "used_percentage") is { } used)
                        {
                            var window = new LimitWindow("seven_day", 10080, used, J.Epoch(J.Number(w, "resets_at")));
                            rows.AddRange(UsageEvent.From([window], new DateTimeOffset(f.LastWriteTimeUtc), "claude", user.Claude,
                                command ? ClaudeUsageCommand.HistorySource : "claude-session"));
                        }
                        return true;
                    });
            foreach (var path in Paths.ClaudeDesktopHistory())
                Scan(path, _ =>
                {
                    if (UsageHistory.ReadObject(path) is not { } root || !root.TryGetProperty("samples", out var samples)
                        || samples.ValueKind != JsonValueKind.Array) return false;
                    foreach (var s in samples.EnumerateArray())
                    {
                        if (user.Org is null || J.Str(s, "org") != user.Org || J.Number(s, "t") is not { } at || at < user.Since
                            || J.Number(J.Obj(s, "u") ?? s, "sd") is not { } used) continue;
                        rows.Add(new("claude", key, 10080, (long)at, null, used, "claude-desktop", "rough", "seven_day"));
                    }
                    return true;
                });
        }
        if (!string.IsNullOrEmpty(user.Codex) && Directory.Exists(Paths.CodexSessions))
            foreach (var path in Directory.EnumerateFiles(Paths.CodexSessions, "rollout-*.jsonl", options))
                Scan(path, _ =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length == 0) return false;
                    stream.Seek(-1, SeekOrigin.End);
                    var complete = stream.ReadByte() == '\n';
                    stream.Position = 0;
                    using var reader = new StreamReader(stream);
                    using var header = JsonDocument.Parse(reader.ReadLine() ?? "");
                    if (J.Str(header.RootElement, "type") != "session_meta") return false;
                    if (J.Obj(header.RootElement, "payload") is not { } meta || J.Str(meta, "creator_account_id") != user.Codex) return true;
                    while (reader.ReadLine() is { } line)
                    {
                        if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                        if (!complete && reader.EndOfStream) break;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (J.Epoch(J.Number(root, "timestamp")) is not { } t || t.ToUnixTimeMilliseconds() < now - 35 * UsageHistory.Day
                                || J.Obj(root, "payload") is not { } payload || J.Obj(payload, "rate_limits") is not { } limits
                                || J.Str(limits, "limit_id") is not (null or "codex")) continue;
                            rows.AddRange(UsageEvent.From(CodexReader.WindowsFrom(limits).Where(w => w.Minutes == 10080), t, "codex", user.Codex, "codex-rollout"));
                        }
                        catch (JsonException) { }
                    }
                    return complete;
                });
        return rows.Where(e => e.Valid && e.AtMs <= now && e.AtMs >= now - 35 * UsageHistory.Day).ToList();
    }
}

internal static class Generations
{
    internal const long Tolerance = 600_000;
    static long? Nearest(IEnumerable<long> choices, long t) =>
        choices.OrderBy(r => Math.Abs(r-t)).ThenBy(r => r).Cast<long?>().FirstOrDefault();

    internal static Dictionary<long,long> Anchors(IEnumerable<UsageEvent> ordered, long? seed = null)
    {
        var anchors = new List<long>();
        if (seed.HasValue) anchors.Add(seed.Value);
        var mapping = new Dictionary<long,long>();
        foreach (var r in ordered.Where(e => e.ResetMs.HasValue).Select(e => e.ResetMs!.Value).Distinct())
        {
            if (Nearest(anchors,r) is { } a && Math.Abs(a-r) <= Tolerance) mapping[r] = a;
            else { anchors.Add(r); mapping[r] = r; }
        }
        return mapping;
    }

    internal static Dictionary<long, List<UsageEvent>> Group(IEnumerable<UsageEvent> input)
    {
        var events = UsageEvent.Ordered(input).ToList();
        var mapping = Anchors(events);
        var anchors = mapping.Values.Distinct().ToList();
        var result = new Dictionary<long, List<UsageEvent>>();
        long unknown = 0;
        double? previousDesktop = null;
        foreach (var row in events)
        {
            var key = unknown;
            if (row.ResetMs is { } reset) key = mapping[reset];
            else if (row.Provider == "claude" && Nearest(mapping.Keys, row.AtMs) is { } r)
            {
                var end = r + ((long)Math.Floor((double)(row.AtMs-r)/UsageHistory.Week)+1)*UsageHistory.Week;
                key = Nearest(mapping.Keys, end) is { } measured && Math.Abs(measured-end) <= Tolerance ? mapping[measured] : end;
            }
            else if (row.Provider == "claude" && row.Source == "claude-desktop")
            {
                if (previousDesktop - row.Used >= 20) unknown--;
                previousDesktop = row.Used;
                key = unknown;
            }
            else if (row.Provider == "codex")
            {
                var possible = anchors.Where(a => a-UsageHistory.Week <= row.AtMs && row.AtMs < a).ToList();
                if (possible.Count == 1) key = possible[0];
            }
            if (!result.TryGetValue(key, out var group)) result[key] = group = new();
            group.Add(row);
        }
        return result;
    }
}

internal sealed record TrendDay(string Date, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Used, bool Rough = false, bool Reset = false);
internal sealed record TrendPoint(long AtMs, double Used);
internal sealed record TrendWeek(long StartMs, long ResetMs, double Used, List<TrendPoint> Points, long? EndMs, double? EndUsed);
internal sealed record TrendResult([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TrendWeek? Week, List<TrendDay> Days, int AverageDays, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Average, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TrendDay? Best)
{
    internal static Dictionary<string, TrendResult> Calculate(IEnumerable<UsageEvent> rows, UsageAccount user, long now)
    {
        var keys = user.Keys();
        return new[] { "claude", "codex" }.ToDictionary(p => p, p => Build(rows.Where(e => e.Provider == p &&
            e.Account == keys.GetValueOrDefault(p) && e.Minutes == 10080 && e.AtMs >= now-35*UsageHistory.Day && e.AtMs <= now).ToList(), now));
    }
    static TrendResult Build(List<UsageEvent> rows, long now)
    {
        static DateTime Local(long t) => DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime.Date;
        static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var groups = Generations.Group(rows);
        var first = rows.Count == 0 ? DateTime.MaxValue : Local(rows.Min(r => r.AtMs));
        var days = Enumerable.Range(-27,28).Select(i => Local(now).AddDays(i)).Select(d => new TrendDay(Day(d), d >= first ? 0 : null)).ToList();
        var indices = days.Select((d,i) => (d.Date,i)).ToDictionary(t => t.Date,t => t.i);
        var boundaries = new List<(long Key, long? End, long First, bool Known)>();
        foreach (var (key, events) in groups)
        {
            var reset = events.LastOrDefault(e => e.ResetMs.HasValue)?.ResetMs;
            boundaries.Add((key, reset ?? (key > 0 ? key : null), events[0].AtMs, reset.HasValue));
            var confirmed = new Dictionary<DateTime,double>();
            var rough = new HashSet<UsageEvent>();
            foreach (var batch in events.GroupBy(e => e.AtMs).Reverse())
            {
                var d = Local(batch.Key);
                var observed = batch.Where(e => e.Quality == "observed").Select(e => e.Used).ToHashSet();
                foreach (var e in batch.Where(e => e.Quality == "rough" && e.Used > confirmed.GetValueOrDefault(d,-1) && !observed.Contains(e.Used)))
                    rough.Add(e);
                if (observed.Count > 0) confirmed[d] = Math.Max(confirmed.GetValueOrDefault(d),observed.Max());
            }
            double high = 0;
            foreach (var e in events.Where(e => e.Used > high))
            {
                if (indices.TryGetValue(Day(Local(e.AtMs)), out var idx))
                {
                    var d = days[idx];
                    days[idx] = d with { Used = (d.Used ?? 0) + (e.Used-high), Rough = d.Rough || rough.Contains(e) };
                }
                high = e.Used;
            }
        }
        boundaries = boundaries.OrderBy(b => b.Key > 0 ? b.Key-UsageHistory.Week : b.First).ThenBy(b => b.Key).ToList();
        for (var i = 1; i < boundaries.Count; i++)
        {
            var old = boundaries[i-1];
            if (boundaries[i].End is not { } end) continue;
            if (rows[0].Provider == "claude" && old.End is { } prev && Math.Abs(end-prev-UsageHistory.Week) > Generations.Tolerance) continue;
            if (indices.TryGetValue(Day(Local(Math.Min(old.End ?? end,end-UsageHistory.Week))),out var idx))
                days[idx] = days[idx] with { Reset = true };
        }
        var recent = days.TakeLast(7).Where(d => d.Used.HasValue).ToList();
        var best = days.Where(d => !d.Rough && d.Used > 0).OrderByDescending(d => d.Used).FirstOrDefault();
        TrendWeek? week = null;
        var b = boundaries.LastOrDefault(b => b.Known);
        if (b.End is { } r && r > now && r-UsageHistory.Week <= now)
        {
            var start = r-UsageHistory.Week;
            var points = new List<TrendPoint>();
            foreach (var e in groups[b.Key].Where(e => e.AtMs >= start))
                if (points.Count == 0 || e.Used > points[^1].Used) points.Add(new(e.AtMs,e.Used));
            if (points.Count > 0)
            {
                var used = points[^1].Used;
                double? projected = now > start ? used*UsageHistory.Week/(now-start) : null;
                long? end = projected is null ? null : projected > 100 ? start+(long)((double)(now-start)*100/used) : r;
                week = new(start,r,used,points,end,projected is { } v ? Math.Min(100,v) : null);
            }
        }
        return new(week,days,recent.Count,recent.Average(d => d.Used),best);
    }
}

internal sealed class UsageHistory
{
    internal const long Day = 86_400_000, Week = Day*7;
    internal static string DirectoryPath => Path.Combine(Paths.Home, ".cache","ai-usage");
    static string FilePath => Path.Combine(DirectoryPath,"history.jsonl");
    internal List<UsageEvent> Rows { get; private set; } = new();
    HashSet<UsageEvent> keys = new();
    long? lastCleanup;
    bool readable = true;
    internal static JsonElement? ReadObject(string path)
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    List<UsageEvent> retryRows = new();
    internal IEnumerable<UsageEvent> VisibleRows => Rows.Concat(retryRows);
    internal UsageHistory() => Load();
    void Load()
    {
        Rows.Clear(); keys.Clear(); readable = true;
        try
        {
            foreach (var line in File.ReadLines(FilePath))
                try
                {
                    if (JsonSerializer.Deserialize(line, UsageJson.Default.UsageEvent) is { Valid:true, Minutes:10080 } row)
                    { Rows.Add(row); keys.Add(row.Key); }
                }
                catch (JsonException) { }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readable = false; }
    }
    internal static void Atomic(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(DirectoryPath);
        var tmp = Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { write(stream); stream.Flush(true); }
            File.Move(tmp,path,true);
        }
        finally { File.Delete(tmp); }
    }
    internal bool RecordLive(IEnumerable<UsageEvent> events, UsageAccount user, long now)
    {
        var keys = user.Keys();
        return Merge(events.Where(e => keys.GetValueOrDefault(e.Provider) == e.Account),now);
    }
    internal bool Merge(IEnumerable<UsageEvent> input,long now)
    {
        if (!readable) Load();
        var pending = retryRows.Concat(input).ToList();
        if (readable && Persist(pending,now)) { retryRows.Clear(); return true; }
        retryRows = pending.Where(e => e.Valid && e.Minutes == 10080 && e.AtMs >= now-35*Day).DistinctBy(e => e.Key).ToList();
        return false;
    }
    bool Persist(List<UsageEvent> input, long now)
    {
        var fresh = input.Where(e => e.Valid && e.Minutes == 10080 && e.AtMs <= now && e.AtMs >= now-35*Day && !keys.Contains(e.Key)).ToList();
        var cleanup = lastCleanup is null || now-lastCleanup >= Day;
        if (!cleanup && fresh.Count == 0) return true;
        var old = cleanup ? Rows.Where(e => e.AtMs >= now-35*Day).ToList() : Rows;
        var added = new List<UsageEvent>();
        foreach (var incoming in fresh.GroupBy(e => (e.Provider,e.Account)))
        {
            foreach (var group in Generations.Group(old.Where(e => (e.Provider,e.Account) == incoming.Key).Concat(incoming)).Values)
            {
                UsageEvent? previous = null;
                foreach (var e in group.DistinctBy(e => e.Key))
                {
                    if (keys.Contains(e.Key)) { previous = e; continue; }
                    if (previous is { } p && p.Used == e.Used && (e.ResetMs == null || p.ResetMs == e.ResetMs) && !(p.Quality == "rough" && e.Quality == "observed")) continue;
                    previous = e; added.Add(e);
                }
            }
        }
        if (!cleanup && added.Count == 0) return true;
        void Write(Stream stream)
        {
            foreach (var e in UsageEvent.Ordered(cleanup ? old.Concat(added) : added))
            {
                JsonSerializer.Serialize(stream,e,UsageJson.Default.UsageEvent); stream.WriteByte((byte)'\n');
            }
        }
        try
        {
            if (cleanup) Atomic(FilePath,Write);
            else { using var stream = new FileStream(FilePath,FileMode.Append,FileAccess.Write,FileShare.Read); Write(stream); stream.Flush(true); }
            old.AddRange(added); Rows = old;
            keys = Rows.Select(e => e.Key).ToHashSet();
            if (cleanup) lastCleanup = now;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lastCleanup = null; return false; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UsageEvent))]
[JsonSerializable(typeof(Dictionary<string,TrendResult>))]
[JsonSerializable(typeof(AlertFile))]
internal partial class UsageJson : JsonSerializerContext { }

internal sealed record AlertWindow(long? Anchor = null, long? ResetMs = null, long AtMs = 0, string Low = "", bool Pending = false, string? Reset = null, long? LowAtMs = null);
internal sealed record AlertFile(int V, Dictionary<string,AlertWindow> Windows);
internal sealed record AlertNotice(string Kind, string Provider, string Account, int Minutes, string Title, string? Body, string? Model = null);
internal sealed record AlertCandidate(AlertNotice Notice, AlertFile State);
internal sealed class LimitAlerts
{
    const long ResetNoticeGraceMs = 60 * 60_000L;
    static string FilePath => Path.Combine(UsageHistory.DirectoryPath,"alerts.json");
    internal AlertFile State { get; private set; } = new(1,new());
    readonly HashSet<string> cancellations = new();
    bool cancelModels;
    bool healthy = true;
    internal bool StorageOK { get; private set; } = true;
    internal LimitAlerts()
    {
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath),UsageJson.Default.AlertFile);
            if (file is { V:1, Windows:not null }) State = file; else StorageOK = healthy = false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { StorageOK = healthy = false; }
    }
    internal bool Save(AlertFile next)
    {
        if (!healthy) return false;
        StorageOK = true;
        if (State.Windows.Count == next.Windows.Count && next.Windows.All(p => State.Windows.GetValueOrDefault(p.Key) == p.Value)) return true;
        try
        {
            UsageHistory.Atomic(FilePath,stream => JsonSerializer.Serialize(stream,next,UsageJson.Default.AlertFile));
            State = next; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StorageOK = false; return false; }
    }
    internal void Cancel(string? provider = null)
    {
        if (provider is null) cancellations.UnionWith(["claude","codex"]);
        else cancellations.Add(provider);
        ApplyCancellations();
    }
    internal void ApplyPreferences(bool enabled, bool models)
    {
        if (!enabled) Cancel();
        else if (!models)
        {
            cancelModels = true;
            ApplyCancellations();
        }
    }

    readonly record struct Scope(int Minutes, string? Model)
    {
        public string Suffix
        {
            get
            {
                var length = Minutes.ToString(CultureInfo.InvariantCulture);
                if (Model == null) return length;
                var encoded = System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Model));
                return length + "/model/" + encoded;
            }
        }
        internal static Scope? Parse(string suffix)
        {
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)) return new Scope(minutes, null);
            const string prefix = "10080/model/";
            if (!suffix.StartsWith(prefix, StringComparison.Ordinal) || suffix.Length == prefix.Length) return null;
            try
            {
                var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(suffix.AsSpan(prefix.Length));
                var scope = new Scope(10080, Encoding.UTF8.GetString(bytes));
                return scope.Suffix == suffix ? scope : null;
            }
            catch (FormatException) { return null; }
        }
    }

    bool ApplyCancellations()
    {
        if (!cancelModels && cancellations.Count == 0) return true;
        static bool IsModelKey(string key)
        {
            if (!key.StartsWith("claude/", StringComparison.Ordinal)) return false;
            int separator = key.IndexOf('/', "claude/".Length);
            return separator >= 0 && Scope.Parse(key[(separator + 1)..]) is { Model: not null };
        }
        var next = State.Windows.ToDictionary(p => p.Key,p => p.Value.Pending &&
            (cancellations.Any(provider => p.Key.StartsWith(provider+"/",StringComparison.Ordinal)) ||
             (cancelModels && IsModelKey(p.Key)))
            ? p.Value with { Pending = false, Reset = "cancelled" } : p.Value);
        if (!Save(new(1,next))) return false;
        cancellations.Clear();
        cancelModels = false;
        return true;
    }
    internal AlertCandidate? NextLive(ProviderSnapshot claude, CodexReader codex, long now, bool enabled, bool models)
    {
        ApplyPreferences(enabled, models);
        if (!enabled) return null;
        return Next(UsageEvent.LiveRows(claude, codex), UsageAccount.Read(now).Keys(), now,
            modelRows: models ? claude.ModelObservations : null);
    }

    internal AlertCandidate? Next(List<UsageEvent> rows, Dictionary<string,string> keys, long now, bool skipped = false,
        IReadOnlyDictionary<string, UsageEvent>? modelRows = null)
    {
        if (!healthy || !ApplyCancellations()) return null;
        var next = State with { Windows = new(State.Windows) };
        foreach (var (provider,account) in keys.OrderBy(p => p.Key,StringComparer.Ordinal))
        {
            var prefix = provider+"/"+account+"/";
            var live = rows.Where(e => e.Valid && e.Provider == provider && e.Account == account && e.AtMs <= now).ToList();
            var groups = live.GroupBy(e => new Scope(e.Minutes, null)).ToDictionary(g => g.Key, g => g.ToList());
            if (provider == "claude" && modelRows != null)
                foreach (var (name, e) in modelRows)
                    if (e.Valid && e.Account == account && e.AtMs <= now) groups[new Scope(e.Minutes, name)] = [e];
            foreach (var key in State.Windows.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && Scope.Parse(key[prefix.Length..]) is { } saved &&
                    (saved.Model == null || (provider == "claude" && modelRows != null))) groups.TryAdd(saved, []);
            foreach (var scope in groups.Keys.OrderBy(s => s.Minutes).ThenBy(s => s.Model, StringComparer.Ordinal))
            {
                var m = scope.Minutes;
                var k = prefix + scope.Suffix;
                var duration = m * 60_000L;
                var title = provider == "claude" ? "Claude" : "ChatGPT";
                var label = scope.Model ?? Fmt.WindowLabel(m);
                var window = groups[scope];
                var resets = window.Where(e => e.ResetMs.HasValue).Select(e => e.ResetMs!.Value).ToList();
                var eligible = window.Where(e =>
                {
                    if (e.ResetMs is { } r) return r > now;
                    if (now-e.AtMs > duration) return false;
                    var measured = resets.Where(r => r-duration <= e.AtMs && e.AtMs < r);
                    return !measured.Any() || measured.Any(r => r > now);
                }).ToList();
                var w = next.Windows.GetValueOrDefault(k);
                var anchors = Generations.Anchors(UsageEvent.Ordered(eligible),w?.Anchor);
                var generation = anchors.Values.Cast<long?>().Max();
                var current = UsageEvent.Ordered(eligible.Where(e => e.ResetMs is { } r ? anchors[r] == generation
                    : generation is null || e.AtMs >= generation-duration-Generations.Tolerance)).ToList();
                var row = current.LastOrDefault();
                var newReset = row?.ResetMs ?? current.LastOrDefault(e => e.ResetMs.HasValue)?.ResetMs;
                // Migrate a legacy low using its last observation before AtMs moves forward.
                if (w is { Low.Length: > 0, LowAtMs: null }) w = w with { LowAtMs = w.AtMs };
                if (w is { ResetMs: null, LowAtMs: { } lowAt } && row != null &&
                    row.AtMs >= w.AtMs && row.AtMs-lowAt >= duration) w = null;
                if (row != null && w != null && w.Anchor == generation && row.AtMs >= w.AtMs && newReset is { } corrected)
                    w = w with { ResetMs = corrected };
                var newer = w != null && row?.AtMs >= w.AtMs && newReset is { } nr &&
                    (w.Anchor is { } a ? generation > a : nr-duration > (w.LowAtMs ?? w.AtMs));
                if (w is { Pending:true } && (w.ResetMs <= now || newer))
                {
                    long? start = newer && duration > 0 ? newReset-duration : null;
                    var boundary = Math.Min(w.ResetMs ?? long.MaxValue,start ?? long.MaxValue);
                    var timely = boundary <= now && boundary >= now-ResetNoticeGraceMs;
                    w = w with { Pending = false, Reset = timely && !skipped ? "sent" : "skipped" };
                    next.Windows[k] = w;
                    if (timely)
                        return new(new("reset",provider,account,m,T("{0} {1} 한도가 초기화됐습니다",title,label),T("다시 100% 남았습니다."),scope.Model),next);
                }
                if (row == null) continue;
                if (w != null)
                {
                    if (row.AtMs < w.AtMs || generation < w.Anchor) continue;
                    if (newer || (newReset is null && w.ResetMs <= row.AtMs)) w = null;
                    else if (w.ResetMs <= now) continue;
                }
                w ??= new();
                w = w with { AtMs = row.AtMs, Anchor = w.Anchor ?? generation ?? newReset, ResetMs = newReset ?? w.ResetMs };
                var measured = current.Where(e => e.ResetMs.HasValue).Select(e => (double?)e.Used).Max() ?? row.Used;
                var used = provider == "claude" && newReset.HasValue ? Math.Max(row.Used,measured) : row.Used;
                var left = 100-(int)Math.Floor(used);
                if (left <= 10 && w.Low.Length == 0)
                {
                    next.Windows[k] = w with { LowAtMs = row.AtMs, Low = skipped ? "skipped" : "sent", Pending = !skipped, Reset = skipped ? "skipped" : "pending" };
                    string? body = null;
                    if (w.ResetMs is { } r)
                    {
                        var time = DateTimeOffset.FromUnixTimeMilliseconds(r).LocalDateTime;
                        var format = time.Date == DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.Date ? "HH:mm" : "M'/'d(ddd) HH:mm";
                        body = T("{0}에 초기화됩니다.",time.ToString(format,L10n.Culture));
                    }
                    return new(new("low",provider,account,m,T("{0} {1} 한도 {2}% 남음",title,label,left),body,scope.Model),next);
                }
                next.Windows[k] = w;
            }
        }
        Save(next);
        return null;
    }
}

// The transient icon and the balloon have the same lifetime. Closing and timing out
// use the same transition, so a second callback cannot hide an already idle icon.
internal sealed class BalloonPresenter(Action<bool> setVisible, Action<AlertNotice> display)
{
    internal bool Busy { get; private set; }
    internal void Show(AlertNotice notice)
    {
        Busy = true;
        try { setVisible(true); display(notice); }
        catch { Dismiss(); throw; }
    }
    internal bool Dismiss()
    {
        if (!Busy) return false;
        Busy = false; setVisible(false);
        return true;
    }
}

internal static class UsageCommands
{
    static long? Time(JsonElement obj,string name) => obj.TryGetProperty(name,out var value)
        ? value.ValueKind == JsonValueKind.Number ? value.GetInt64() : J.Epoch(J.Number(value))?.ToUnixTimeMilliseconds() : null;
    static DateTimeOffset Clock(string[] args)
    {
        var i = Array.IndexOf(args,"--now");
        return i < 0 ? DateTimeOffset.Now : DateTimeOffset.Parse(args[i+1],CultureInfo.InvariantCulture);
    }
    internal static string Trend(string[] args)
    {
        var now = Clock(args).ToUnixTimeMilliseconds();
        var user = UsageAccount.Read(now);
        var rows = new UsageHistory().Rows.Concat(new SourceScan().Read(user,now));
        return JsonSerializer.Serialize(TrendResult.Calculate(rows,user,now),UsageJson.Default.DictionaryStringTrendResult);
    }
    static void RequireTestHome(params string[] names)
    {
        static string Canonical(string path, int depth = 0)
        {
            if (depth > 40 || path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Contains(".."))
                throw new ArgumentException("AIUSAGE_HOME");
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = Path.Combine(current,part);
                FileSystemInfo child = Directory.Exists(childPath) ? new DirectoryInfo(childPath) : new FileInfo(childPath);
                current = child.LinkTarget is { } link
                    ? Canonical(Path.IsPathRooted(link) ? link : Path.Combine(current, link), depth + 1)
                    : child.FullName;
            }
            return Path.TrimEndingDirectorySeparator(current);
        }
        var home = Environment.GetEnvironmentVariable("AIUSAGE_HOME");
        var real = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(home) || string.Equals(Canonical(home),Canonical(real),comparison))
            throw new ArgumentException("AIUSAGE_HOME");
        var prefix = Canonical(home) + Path.DirectorySeparatorChar;
        var realCache = Canonical(Path.Combine(real, ".cache", "ai-usage"));
        foreach (var name in names)
        {
            var dest = Canonical(Path.Combine(UsageHistory.DirectoryPath,name));
            if (!dest.StartsWith(prefix,comparison) || string.Equals(dest, realCache, comparison)
                || dest.StartsWith(realCache + Path.DirectorySeparatorChar, comparison)
                || realCache.StartsWith(dest + Path.DirectorySeparatorChar, comparison)
                || string.Equals(dest, Canonical(Path.Combine(realCache, name)), comparison))
                throw new ArgumentException("AIUSAGE_HOME");
        }
    }
    internal static string RecordOnce(string[] args)
    {
        RequireTestHome("accounts.json","claude","history.jsonl");
        return WithWriter(() => RecordLocal(args));
    }
    static string WithWriter(Func<string> run)
    {
        using var mutex = new Mutex(false,@"Local\AIUsagePet");
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("History writer is busy"); }
        catch (AbandonedMutexException) { }
        try { return run(); }
        finally { mutex.ReleaseMutex(); }
    }
    static string RecordLocal(string[] args)
    {
        var at = Clock(args);
        var now = at.ToUnixTimeMilliseconds();
        var claude = new ClaudeReader().Read(at);
        var codex = new CodexReader();
        codex.ReadLog(at);
        var history = new UsageHistory();
        var retained = history.Rows.Count(e => e.AtMs >= now-35*UsageHistory.Day);
        if (!history.RecordLive(UsageEvent.LiveRows(claude,codex),UsageAccount.Read(now),now))
            throw new IOException("history.jsonl");
        return (history.Rows.Count-retained).ToString(CultureInfo.InvariantCulture);
    }

    internal static string AlertsOnce(string[] args)
    {
        RequireTestHome("accounts.json", "claude", "alerts.json");
        bool Option(string name, bool fallback)
        {
            var i = Array.IndexOf(args, name);
            if (i < 0) return fallback;
            return args.ElementAtOrDefault(i + 1) switch { "0" => false, "1" => true, _ => throw new ArgumentException(name) };
        }
        var enabled = Option("--limit-alerts", true);
        var models = Option("--model-alerts", false);
        return WithWriter(() =>
        {
            var at = Clock(args);
            var claude = new ClaudeReader().Read(at);
            var codex = new CodexReader();
            codex.ReadLog(at);
            var alerts = new LimitAlerts();
            var notices = new JsonArray();
            while (alerts.NextLive(claude, codex, at.ToUnixTimeMilliseconds(), enabled, models) is { } candidate)
            {
                if (!alerts.Save(candidate.State)) throw new IOException("alerts.json");
                var n = candidate.Notice;
                notices.Add(new JsonObject {
                    ["kind"] = n.Kind, ["provider"] = n.Provider, ["minutes"] = n.Minutes,
                    ["scope"] = n.Model == null ? "overall" : "model", ["model"] = n.Model,
                    ["title"] = n.Title, ["body"] = n.Body
                });
            }
            if (!alerts.StorageOK) throw new IOException("alerts.json");
            return notices.ToJsonString();
        });
    }
    internal static string TestAlerts(string path)
    {
        RequireTestHome("alerts.json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
        var alerts = new LimitAlerts();
        var events = new JsonArray();
        var candidates = new JsonArray();
        var icon = new JsonArray();
        var presenter = new BalloonPresenter(visible => icon.Add((JsonNode?)JsonValue.Create(visible)), _ => { });
        static JsonNode Report(AlertNotice n,string outcome) =>
            new JsonObject { ["kind"]=n.Kind, ["title"]=n.Title, ["body"]=n.Body, ["result"]=outcome };
        var keys = new Dictionary<string,string>();
        var hidden = new HashSet<string>();
        bool enabled = true;
        bool? permission = null;
        int requests = 0;
        foreach (var s in fixture.RootElement.GetProperty("steps").EnumerateArray())
        {
            var now = Time(s,"now") ?? throw new FormatException("now");
            var p = J.Str(s,"provider") ?? "codex";
            if (J.Str(s,"account") is { } id)
            {
                if (UsageAccount.Hash(p,id) is { } accountKey) keys[p] = accountKey; else keys.Remove(p);
            }
            else keys.TryAdd(p,UsageAccount.Hash(p,"test")!);
            if (J.Obj(s,"accounts") is { } accounts)
                foreach (var pair in accounts.EnumerateObject())
                    if (UsageAccount.Hash(pair.Name,pair.Value.GetString()) is { } key) keys[pair.Name] = key;
                    else keys.Remove(pair.Name);
            if (s.TryGetProperty("enabled",out var on)) { enabled = on.GetBoolean(); if (!enabled) alerts.Cancel(); }
            if (s.TryGetProperty("hidden",out var hide))
            {
                if (hide.GetBoolean()) { hidden.Add(p); alerts.Cancel(p); } else hidden.Remove(p);
            }
            var rows = new List<UsageEvent>();
            foreach (var o in s.TryGetProperty("observations",out var obs) ? obs.EnumerateArray().ToArray() : new[] { s })
            {
                if (J.Number(o,"used") is not { } used) continue;
                var provider = J.Str(o,"provider") ?? p;
                if (!keys.ContainsKey(provider) && provider != p && UsageAccount.Hash(provider,J.Str(o,"account") ?? "test") is { } initial)
                    keys[provider] = initial;
                var account = UsageAccount.Hash(provider,J.Str(o,"account")) ?? keys.GetValueOrDefault(provider);
                if (account == null) continue;
                rows.Add(new(provider,account,(int)(J.Number(o,"minutes") ?? 10080),Time(o,"atMs") ?? now,Time(o,"resetMs"),used,"codex-server","observed",J.Str(o,"slot")));
            }
            var active = keys.Where(k => !hidden.Contains(k.Key)).ToDictionary();
            if (!enabled || alerts.Next(rows,active,now) is not { } first) continue;
            var outcome = J.Str(s,"outcome") ?? "sent";
            if (permission == null) { requests++; permission = outcome != "denied"; }
            if (outcome is "hideWhileWaiting" or "switchWhileWaiting")
            {
                candidates.Add(Report(first.Notice,outcome));
                if (outcome == "hideWhileWaiting") { hidden.Add(p); alerts.Cancel(p); }
                else keys[p] = UsageAccount.Hash(p,"switched")!;
                continue;
            }
            while (alerts.Next(rows,active,now,permission == false) is { } c)
            {
                var before = alerts.State;
                if (outcome == "saveFailure" || !alerts.Save(c.State)) { candidates.Add(Report(c.Notice,"saveFailure")); break; }
                var result = permission == false ? "skipped" : outcome;
                candidates.Add(Report(c.Notice,result));
                events.Add(Report(c.Notice,result));
                if (permission == true) { presenter.Show(c.Notice); presenter.Dismiss(); }
                if (outcome == "failed") { alerts.Save(before); break; }
                if (outcome == "unknown") break;
            }
        }
        return new JsonObject { ["events"]=events, ["candidates"]=candidates, ["permissionRequests"]=requests, ["iconVisibility"]=icon }.ToJsonString();
    }
}
