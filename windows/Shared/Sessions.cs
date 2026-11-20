// 세션 — 이 PC 에서 켜 둔 Claude Code·Codex 작업이 작업 중인지, 내 입력을 기다리는지, 끝났는지(3초마다).
// 맥 판(AIUsagePet.swift 의 ClaudeSessionReader·CodexSessionReader)과 같은 규칙, Windows 에 맞게 고친 곳은 주석에 적었다.
//
//   Claude : 실행 중인 세션마다 남는 ~/.claude/sessions/<pid>.json 의 status
//            (busy·shell=작업 중, waiting·blocked=입력 필요, idle=작업 완료) + 대화 기록(~/.claude/projects/…/<세션>.jsonl) 끝.
//            터미널 CLI 와 데스크톱 앱 Code 탭 세션 모두. 공식 문서에 없는 형식이라 모르는 값은 「알 수 없음」.
//   Codex  : ~/.codex/sessions/<날짜>/rollout-*.jsonl 의 마지막 표시(task_started=작업 중, task_complete=작업 완료, turn_aborted=중단).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using static AIUsage.L10n;

namespace AIUsage;

/// 창에 놓는 순서도 이 순서: 입력 필요 → 작업 중 → 작업 완료 → 알 수 없음
public enum SessionState { Waiting, Working, Done, Unknown }

public enum SessionTool { Claude, Codex }

/// Since: 작업 중이면 시작한 때, 그 밖에는 그 상태가 된 때
public sealed record AgentSession(string Id, SessionTool Tool, string Name, string Cwd, SessionState State, DateTimeOffset Since,
                                  bool Background = false, bool Aborted = false)
{
    /// Codex 데스크톱 앱이 붙인 대화 제목을 이름으로 쓰는가
    public bool Titled { get; init; }
    /// 그 대화를 여는 링크(Claude 데스크톱 앱 claude://…/epitaxy/local_…, Codex 데스크톱 앱 codex://threads/<id>). 없으면 null
    public string? Link { get; init; }
    /// 그 세션을 돌리는 프로세스(Claude CLI 처럼 링크가 없으면 그 터미널 창을 앞으로 가져온다)
    public int? Pid { get; init; }

    /// 「작업 중 12분」「입력 필요」「작업 완료 8분 전」
    public string StatusText(DateTimeOffset now) => State switch
    {
        SessionState.Working => Fmt.Duration(Since, now) is { } d ? T("작업 중") + " " + d : T("작업 중"),
        SessionState.Waiting => T("입력 필요"),
        SessionState.Done => T("작업 완료 {0}", Fmt.AgeText(Since, now)),
        _ => T("알 수 없음"),
    };

    /// 메뉴·--print 한 줄: 「Claude · 이름 — 작업 중 12분 · ~\폴더」
    public string Describe(DateTimeOffset now)
    {
        var home = Paths.Home;
        var place = Cwd.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + Cwd[home.Length..] : Cwd;
        var note = Background ? " " + T("(뒤에서 명령이 아직 도는 중)") : Aborted ? " " + T("(중단됨)") : "";
        return $"{(Tool == SessionTool.Claude ? "Claude" : "Codex")} · {Name} — {StatusText(now)}{note}" + (place.Length == 0 ? "" : " · " + place);
    }
}

/// Claude·Codex 세션을 함께 읽어 창에 놓을 순서로
public sealed class SessionReader
{
    readonly ClaudeSessionReader claude = new();
    readonly CodexSessionReader codex = new();

    public List<AgentSession> Read(DateTimeOffset now) => Sort(claude.Read(now).Concat(codex.Read(now)));

    /// 상태 순서대로, 같은 상태에서는 최근에 바뀐 것부터
    public static List<AgentSession> Sort(IEnumerable<AgentSession> list) =>
        list.OrderBy(s => (int)s.State).ThenByDescending(s => s.Since).ToList();

    /// 끝이 잘린 줄은 버리고, 파일 끝 maxBytes 안의 줄들
    internal static List<ReadOnlyMemory<byte>> TailLines(string path, long maxBytes, long from = -1)
    {
        var lines = new List<ReadOnlyMemory<byte>>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = from >= 0 ? from : Math.Max(0, stream.Length - maxBytes);
            if (start >= stream.Length) return lines;
            stream.Seek(start, SeekOrigin.Begin);
            var data = new byte[stream.Length - start];
            stream.ReadExactly(data);
            int begin = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\n') continue;
                if (i > begin) lines.Add(data.AsMemory(begin, i - begin));
                begin = i + 1;
            }
            if (from < 0 && start > 0 && lines.Count > 0) lines.RemoveAt(0);  // 맨 앞은 잘린 줄
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    internal static DateTimeOffset? Timestamp(JsonElement entry) =>
        J.Str(entry, "timestamp") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;
}

public sealed class ClaudeSessionReader
{
    public readonly record struct TranscriptTail(DateTimeOffset? TurnEndedAt, DateTimeOffset? PromptAt, string? Title);

    readonly Dictionary<string, SessionState> lastState = new();
    readonly Dictionary<string, DateTimeOffset> workingSince = new();
    readonly Dictionary<string, string> transcriptPaths = new();
    readonly Dictionary<string, (long Size, TranscriptTail Tail)> tailCache = new();
    /// 세션마다 기록에서 마지막으로 찾은 제목. 긴 차례 도중에는 기록 끝 512KB 에 제목 줄이 없을 수 있어 앞의 것을 그대로 쓴다
    readonly Dictionary<string, string> titles = new();

    public List<AgentSession> Read(DateTimeOffset now)
    {
        var dir = Path.Combine(Paths.ClaudeDir, "sessions");
        var result = new List<AgentSession>();
        var seen = new HashSet<string>();
        List<string> files;
        try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        foreach (var file in files)
        {
            JsonElement obj;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                obj = doc.RootElement.Clone();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (J.Number(obj, "pid") is not { } pidNumber || !Alive((int)pidNumber, J.Str(obj, "procStart"))) continue;  // 꺼진 세션이 남긴 파일
            if (J.Str(obj, "kind") is { } kind && kind != "interactive") continue;  // claude -p(이 앱의 /usage 조회 등)는 빼고
            var pid = (int)pidNumber;
            var sessionId = J.Str(obj, "sessionId") ?? Path.GetFileNameWithoutExtension(file);
            var cwd = J.Str(obj, "cwd") ?? "";
            var changedAt = J.Epoch(J.Number(obj, "statusUpdatedAt") / 1000) ?? now;
            var key = $"{pid}:{sessionId}";
            seen.Add(key);

            // 차례 도중(작업 중·입력 필요)인 세션을 처음 보면 더 깊이(8MB) 읽는다 — 그 차례를 연 내 말과
            // 지난 차례 끝에 적힌 제목이 멀리 있을 수 있다. 끝난 세션은 제목이 기록 맨 끝에 있다
            var status = J.Str(obj, "status");
            var tail = TailFor(sessionId, deep: !lastState.ContainsKey(key) && status != "idle");
            if (tail.Title is { } title) titles[sessionId] = title;
            var name = SessionName(obj, titles.GetValueOrDefault(sessionId), cwd);

            SessionState state;
            var since = changedAt;
            DateTimeOffset? promptAt = null;
            var background = false;
            switch (status)
            {
                case "busy" or "shell":
                    // 뒤에서 도는 명령이 남아 있으면 답을 마친 뒤에도 busy 로 남는다 → 대화 기록 끝으로 가린다
                    // (데스크톱 앱 Code 탭 세션은 기록에 차례 끝 표시가 없어 status 를 그대로 믿는다)
                    if (tail.TurnEndedAt is { } ended)
                    {
                        state = SessionState.Done;
                        since = ended;
                        background = true;
                    }
                    else
                    {
                        state = SessionState.Working;
                        promptAt = tail.PromptAt;
                    }
                    break;
                case "idle": state = SessionState.Done; break;
                case "waiting" or "blocked": state = SessionState.Waiting; break;
                default: state = SessionState.Unknown; break;
            }

            if (state == SessionState.Working)
            {
                if (lastState.GetValueOrDefault(key, SessionState.Unknown) == SessionState.Working && workingSince.TryGetValue(key, out var kept))
                    since = kept;
                else if (lastState.TryGetValue(key, out var before) && before != SessionState.Working)
                    since = promptAt ?? now;      // 이 앱이 보는 동안 작업 중으로 바뀜
                else
                    since = promptAt ?? changedAt; // 앱을 막 켰을 때
                workingSince[key] = since;
            }
            else workingSince.Remove(key);
            lastState[key] = state;
            // 데스크톱 앱 Code 탭 세션은 앱 사이드바의 세션 id(hostSessionId)로 그 세션을 바로 연다
            var host = J.Str(obj, "hostSessionId");
            var link = host != null && System.Text.RegularExpressions.Regex.IsMatch(host, "^local_[0-9A-Za-z-]{1,64}$")
                ? "claude://claude.ai/epitaxy/" + host : null;
            result.Add(new AgentSession("claude:" + key, SessionTool.Claude, name, cwd, state, since, background) { Link = link, Pid = pid });
        }
        foreach (var gone in lastState.Keys.Where(k => !seen.Contains(k)).ToList()) { lastState.Remove(gone); workingSince.Remove(gone); }
        var live = seen.Select(k => k[(k.IndexOf(':') + 1)..]).ToHashSet();
        foreach (var gone in titles.Keys.Where(k => !live.Contains(k)).ToList()) titles.Remove(gone);
        return result;
    }

    /// 그 pid 가 아직 Claude Code 인가. procStart(프로세스 시작 시각, FILETIME)가 있으면 같은 pid 를 다른 프로세스가 다시 쓴 것도 가린다
    static bool Alive(int pid, string? procStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase)) return false;
            if (OperatingSystem.IsWindows() && long.TryParse(procStart, out var started))
                return Math.Abs(process.StartTime.ToFileTimeUtc() - started) < 20_000_000;  // 2초 안
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException) { return false; }
    }

    /// 보일 이름: 직접 붙인 이름(데스크톱 앱 사이드바 제목·/rename) → 대화 기록의 제목(Claude 가 붙인 것) → 지금 폴더 이름.
    /// sessions/<pid>.json 의 name 은 직접 붙이지 않았으면(nameSource 가 user 가 아니면) 켤 때 시작 폴더로 한 번 만든
    /// 「폴더-16진수 2자리」라서 쓰지 않는다
    public static string SessionName(JsonElement obj, string? title, string cwd)
    {
        var given = J.Str(obj, "name")?.Trim() ?? "";
        if (J.Str(obj, "nameSource") == "user" && given.Length > 0) return given;
        if (title?.Trim() is { Length: > 0 } t) return t;
        var folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return folder.Length == 0 ? "Claude" : folder;
    }

    TranscriptTail TailFor(string sessionId, bool deep)
    {
        if (TranscriptPath(sessionId) is not { } path) return default;
        long size;
        try { size = new FileInfo(path).Length; } catch (IOException) { return default; }
        // 기록이 그대로면 지난번 결과를 쓴다(3초마다 512KB 를 다시 읽지 않게)
        if (tailCache.TryGetValue(path, out var cached) && cached.Size == size) return cached.Tail;
        var tail = ScanTail(path, deep ? 8L << 20 : 512L << 10);
        tailCache[path] = (size, tail);
        return tail;
    }

    /// 대화 기록 끝에서: 마지막 차례가 끝났는지(turn_duration), 아니면 그 차례를 연 내 말의 시각, 그리고 세션 제목
    public static TranscriptTail ScanTail(string path, long maxBytes)
    {
        DateTimeOffset? ended = null, prompt = null;
        string? custom = null, ai = null;
        var turnFound = false;
        var lines = SessionReader.TailLines(path, maxBytes);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (turnFound && custom != null && ai != null) break;
            var line = lines[i];
            var span = line.Span;
            try
            {
                // 제목 줄: Claude Code 가 차례마다 기록 끝에 다시 적는다. /resume 목록처럼 custom-title 이 먼저다
                if (span.IndexOf("\"type\":\"custom-title\""u8) >= 0 || span.IndexOf("\"type\":\"ai-title\""u8) >= 0)
                {
                    using var doc = JsonDocument.Parse(line);
                    var entry = doc.RootElement;
                    var type = J.Str(entry, "type");
                    if (type == "custom-title") custom ??= J.Str(entry, "customTitle");
                    else if (type == "ai-title") ai ??= J.Str(entry, "aiTitle");
                    continue;
                }
                if (turnFound || (span.IndexOf("\"type\":\"user\""u8) < 0 && span.IndexOf("\"turn_duration\""u8) < 0)) continue;
                using (var doc = JsonDocument.Parse(line))
                {
                    var entry = doc.RootElement;
                    var at = SessionReader.Timestamp(entry);
                    var type = J.Str(entry, "type");
                    if (type == "system" && J.Str(entry, "subtype") == "turn_duration")
                    {
                        ended = at;
                        turnFound = true;
                    }
                    else if (type == "user" && !(entry.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                             && J.Obj(entry, "message") is { } message && message.TryGetProperty("content", out var content))
                    {
                        // 도구 결과(tool_result)가 아닌 내 말
                        var mine = content.ValueKind == JsonValueKind.String;
                        if (content.ValueKind == JsonValueKind.Array)
                        {
                            var types = content.EnumerateArray().Select(p => J.Str(p, "type")).OfType<string>().ToHashSet();
                            mine = types.Count > 0 && !types.Contains("tool_result");
                        }
                        if (mine)
                        {
                            prompt = at;
                            turnFound = true;
                        }
                    }
                }
            }
            catch (JsonException) { }
        }
        return new TranscriptTail(ended, prompt, custom ?? ai);
    }

    string? TranscriptPath(string sessionId)
    {
        if (transcriptPaths.TryGetValue(sessionId, out var cached) && File.Exists(cached)) return cached;
        var root = Path.Combine(Paths.ClaudeDir, "projects");
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(folder, sessionId + ".jsonl");
                if (!File.Exists(path)) continue;
                transcriptPaths[sessionId] = path;
                return path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}

/// Codex 는 작업마다 ~/.codex/sessions/<날짜>/rollout-*.jsonl 에 기록을 남긴다.
/// 맥 판은 그 파일을 연 codex 프로세스가 있는지로 도중에 꺼졌는지 보지만, Windows 에서는 다른 프로세스가 연 파일을 알기 어렵고
/// Codex 데스크톱 앱이 codex.exe 를 늘 띄워 두므로, 작업 중 표시가 남았는데 기록이 20분 넘게 안 바뀌었거나 codex 가 없으면 중단으로 본다
public sealed class CodexSessionReader
{
    /// 끝난 작업을 남겨 두는 시간
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(30);
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(20);

    sealed class Track
    {
        public long Offset;
        public string Cwd = "";
        public bool IsSubagent;
        public string? LastKind;
        public DateTimeOffset? LastAt;
    }
    readonly Dictionary<string, Track> tracks = new();

    public List<AgentSession> Read(DateTimeOffset now)
    {
        var running = CodexRunning();
        var result = new List<AgentSession>();
        var seen = new HashSet<string>();
        var names = ThreadNames();
        foreach (var (path, mtime) in RecentlyWritten(now))
        {
            var fresh = running && now - mtime < Silent;
            if (!fresh && now - mtime > KeepFinished) continue;
            seen.Add(path);
            var track = tracks.GetValueOrDefault(path) ?? new Track();
            ReadNewLines(path, track);
            tracks[path] = track;
            if (track.IsSubagent) continue;  // Codex 가 스스로 띄운 하위 작업은 빼고 본 작업만

            SessionState state;
            DateTimeOffset since;
            var aborted = false;
            switch (track.LastKind)
            {
                case "task_complete":
                    state = SessionState.Done; since = track.LastAt ?? mtime; break;
                case "turn_aborted":
                    state = SessionState.Done; since = track.LastAt ?? mtime; aborted = true; break;
                case "task_started" when fresh:
                    state = SessionState.Working; since = track.LastAt ?? mtime; break;
                case "task_started":
                    state = SessionState.Done; since = mtime; aborted = true; break;
                default:
                    continue;
            }
            if (state == SessionState.Done && now - since > KeepFinished) continue;
            // 이름: Codex 데스크톱 앱이 붙인 대화 제목, 없으면(CLI) 작업 폴더
            var titled = names.TryGetValue(ThreadId(path), out var title);
            var name = titled ? title! : Path.GetFileName(track.Cwd.TrimEnd('\\', '/')) is { Length: > 0 } folder ? folder : "Codex";
            result.Add(new AgentSession("codex:" + path, SessionTool.Codex, name, track.Cwd, state, since, Aborted: aborted)
                { Titled = titled, Link = ThreadLink(path) });
        }
        foreach (var gone in tracks.Keys.Where(k => !seen.Contains(k)).ToList()) tracks.Remove(gone);
        // 제목 없는 CLI 작업을 같은 폴더에서 다시 돌리면(설계 → 구현 등) 앞의 끝난 기록은 빼고 최근 것만.
        // 제목이 있는 데스크톱 앱 대화는 같은 폴더라도 서로 다른 대화라 그대로 둔다
        var latest = result.Where(s => !s.Titled).GroupBy(s => s.Cwd).ToDictionary(g => g.Key, g => g.Max(s => s.Since));
        return result.Where(s => s.Titled || s.State == SessionState.Working || s.Cwd.Length == 0 || s.Since >= latest[s.Cwd]).ToList();
    }

    /// 최근(끝난 작업을 남겨 두는 시간 안)에 바뀐 rollout 기록. 데스크톱 앱은 대화를 처음 만든 날짜 폴더(예: 10/30)에 계속 이어 쓰므로
    /// 날짜 폴더가 아니라 바뀐 시각으로 고른다
    static List<(string Path, DateTimeOffset Mtime)> RecentlyWritten(DateTimeOffset now)
    {
        var result = new List<(string, DateTimeOffset)>();
        try
        {
            var root = new DirectoryInfo(Paths.CodexSessions);
            if (!root.Exists) return result;
            foreach (var file in root.EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (now - mtime <= KeepFinished) result.Add((file.FullName, mtime));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// Codex 데스크톱 앱에서 그 대화를 여는 링크(대화 id 가 UUID 모양일 때만)
    static string? ThreadLink(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(ThreadId(path), "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
            ? "codex://threads/" + ThreadId(path) : null;

    /// rollout-2026-10-30T11-22-00-<대화 id>.jsonl 의 대화 id
    static string ThreadId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 36 ? name[^36..] : name;
    }

    Dictionary<string, string> threadNames = new();
    long namesSize = -1;

    /// ~/.codex/session_index.jsonl: 대화 id 마다 제목(thread_name). 이름을 바꾸면 줄이 덧붙으므로 마지막 것이 지금 제목
    Dictionary<string, string> ThreadNames()
    {
        var path = Path.Combine(Paths.Home, ".codex", "session_index.jsonl");
        try
        {
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size == namesSize) return threadNames;
            var next = new Dictionary<string, string>();
            foreach (var line in SessionReader.TailLines(path, 0, 0))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (J.Str(doc.RootElement, "id") is { } id && J.Str(doc.RootElement, "thread_name")?.Trim() is { Length: > 0 } title)
                        next[id] = title;
                }
                catch (JsonException) { }
            }
            threadNames = next;
            namesSize = size;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return threadNames;
    }

    /// 지난번 읽은 곳 다음부터 끝난 줄만 읽는다(기록은 수 MB 까지 커진다)
    static void ReadNewLines(string path, Track track)
    {
        long size;
        try { size = new FileInfo(path).Length; } catch (IOException) { return; }
        if (size < track.Offset)
        {
            track.Offset = 0; track.Cwd = ""; track.IsSubagent = false; track.LastKind = null; track.LastAt = null;  // 새로 쓰였으면 처음부터
        }
        if (size <= track.Offset) return;
        var lines = SessionReader.TailLines(path, 0, track.Offset);
        foreach (var line in lines)
        {
            track.Offset += line.Length + 1;
            var span = line.Span;
            if (span.IndexOf("\"session_meta\""u8) < 0 && span.IndexOf("\"task_started\""u8) < 0
                && span.IndexOf("\"task_complete\""u8) < 0 && span.IndexOf("\"turn_aborted\""u8) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var entry = doc.RootElement;
                if (J.Obj(entry, "payload") is not { } payload) continue;
                switch (J.Str(entry, "type"))
                {
                    case "session_meta":
                        track.Cwd = J.Str(payload, "cwd") ?? track.Cwd;
                        track.IsSubagent = payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object;  // 하위 작업은 {subagent: …}
                        break;
                    case "event_msg" when J.Str(payload, "type") is "task_started" or "task_complete" or "turn_aborted":
                        track.LastKind = J.Str(payload, "type");
                        track.LastAt = SessionReader.Timestamp(entry);
                        break;
                }
            }
            catch (JsonException) { }
        }
    }

    static bool CodexRunning()
    {
        var found = Process.GetProcessesByName("codex");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }
}
