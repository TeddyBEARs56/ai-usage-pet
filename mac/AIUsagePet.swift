// AI 사용량 창 — Claude·ChatGPT(Codex) 사용 한도를 화면 위에 계속 띄워 두는 작은 창
//
// 값의 출처
//   Claude  : Claude Code 상태 표시줄이 세션마다 남기는 ~/.cache/ai-usage/claude/<세션>.json
//             (이 앱을 `AIUsagePet --statusline` 으로 상태 표시줄에 걸거나, 직접 만든 statusline.sh 에서 같은 파일을 남긴다)
//   ChatGPT : `codex app-server` 의 account/rateLimits/read(5분마다) + ~/.codex/sessions 기록에 찍히는 최신 한도
//   세션    : Claude Code 가 실행 중인 세션마다 남기는 ~/.claude/sessions/<pid>.json(+ 대화 기록 끝),
//             Codex 의 ~/.codex/sessions rollout 기록 마지막 표시(+ 그 파일을 연 codex 프로세스가 있는지), 3초마다
//
// 실행 옵션: --print (값만 출력), --snapshot <png> (창 모양을 그림으로 저장),
//           --statusline (Claude Code 상태 표시줄: 한도를 남기고 상태 표시줄 글자를 낸다)

import AppKit
import ServiceManagement
import SwiftUI

// MARK: - 값

struct LimitWindow: Identifiable {
    let id: String
    let label: String
    let minutes: Int
    let usedPercent: Double
    let resetsAt: Date?

    /// 상태 표시줄과 같은 계산(100 - 사용률 내림). 초기화 시각이 지났으면 100.
    func leftPercent(now: Date) -> Int {
        if let resetsAt, resetsAt <= now { return 100 }
        return max(0, min(100, 100 - Int(usedPercent.rounded(.down))))
    }
}

struct ProviderSnapshot {
    var windows: [LimitWindow] = []
    var observedAt: Date?
    var plan: String?
    var message: String?
}

private let isoWithFraction: ISO8601DateFormatter = {
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return f
}()
private let isoPlain = ISO8601DateFormatter()

func parseDate(_ s: String) -> Date? { isoWithFraction.date(from: s) ?? isoPlain.date(from: s) }

/// 숫자 또는 숫자 문자열 → Double (시각이 ISO 문자열이면 epoch 초로)
func number(_ value: Any?) -> Double? {
    switch value {
    case let n as NSNumber: return n.doubleValue
    case let s as String: return Double(s) ?? parseDate(s)?.timeIntervalSince1970
    default: return nil
    }
}

func windowLabel(minutes: Int) -> String {
    switch minutes {
    case 300: return "5시간"
    case 10_080: return "주간"
    case let m where m > 0 && m % 1_440 == 0: return "\(m / 1_440)일"
    case let m where m > 0 && m % 60 == 0: return "\(m / 60)시간"
    case let m where m > 0: return "\(m)분"
    default: return "한도"
    }
}

func planName(_ raw: String?) -> String? {
    guard let raw, !raw.isEmpty, raw != "unknown" else { return nil }
    let known = ["free": "Free", "go": "Go", "plus": "Plus", "pro": "Pro", "prolite": "Pro Lite",
                 "promax": "Pro Max", "team": "Team", "business": "Business", "enterprise": "Enterprise", "edu": "Edu"]
    return known[raw] ?? raw
}

// MARK: - Claude

/// 세션마다 마지막으로 받은 한도를 모아, 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다.
/// 같은 창 안에서 사용률은 줄지 않으므로, 오래 쉬던 세션이 옛 값을 다시 써도 표시가 뒤로 가지 않는다.
final class ClaudeReader {
    private let dir = NSHomeDirectory() + "/.cache/ai-usage/claude"
    private var cache: [String: (mtime: Date, limits: [String: Any])] = [:]

    func read(now: Date) -> ProviderSnapshot {
        let fm = FileManager.default
        var entries: [(mtime: Date, limits: [String: Any])] = []
        var alive = Set<String>()
        for name in (try? fm.contentsOfDirectory(atPath: dir)) ?? [] where name.hasSuffix(".json") && !name.hasPrefix(".") {
            let path = dir + "/" + name
            guard let mtime = (try? fm.attributesOfItem(atPath: path))?[.modificationDate] as? Date else { continue }
            if now.timeIntervalSince(mtime) > 8 * 86_400 {  // 주간 창보다 오래된 세션 파일은 정리
                try? fm.removeItem(atPath: path)
                continue
            }
            alive.insert(path)
            if let hit = cache[path], hit.mtime == mtime {
                entries.append(hit)
                continue
            }
            guard let data = fm.contents(atPath: path),
                  let limits = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { continue }
            cache[path] = (mtime, limits)
            entries.append((mtime, limits))
        }
        cache = cache.filter { alive.contains($0.key) }

        var snap = ProviderSnapshot()
        snap.observedAt = entries.map { $0.mtime }.max()
        for (key, label, minutes) in [("five_hour", "5시간", 300), ("seven_day", "주간", 10_080)] {
            let found = entries.compactMap { entry -> (used: Double, reset: Double)? in
                guard let w = entry.limits[key] as? [String: Any], let used = number(w["used_percentage"]) else { return nil }
                return (used, number(w["resets_at"]) ?? 0)
            }
            guard let latest = found.map({ $0.reset }).max() else { continue }
            let used = found.filter { $0.reset >= latest - 600 }.map { $0.used }.max() ?? 0
            snap.windows.append(LimitWindow(id: key, label: label, minutes: minutes, usedPercent: used,
                                            resetsAt: latest > 0 ? Date(timeIntervalSince1970: latest) : nil))
        }
        if snap.windows.isEmpty { snap.message = "Claude Code 에서 한 번 대화하면 표시됩니다" }
        return snap
    }
}

// MARK: - ChatGPT(Codex)

final class CodexReader {
    private var lastLogKey = ""
    private var fetching = false
    private(set) var fromLog: ProviderSnapshot?
    private(set) var fromServer: ProviderSnapshot?
    private(set) var serverError: String?
    private(set) var serverCheckedAt: Date?

    /// 기록과 서버 가운데 더 나중에 확인한 값을 쓴다
    var current: ProviderSnapshot {
        let both = [fromLog, fromServer].compactMap { $0 }
        if let best = both.max(by: { ($0.observedAt ?? .distantPast) < ($1.observedAt ?? .distantPast) }) { return best }
        return ProviderSnapshot(message: serverError ?? "Codex 한도를 읽는 중…")
    }

    /// Codex 를 쓰는 동안 기록 파일에 찍히는 최신 한도(파일이 바뀌었을 때만 다시 읽는다)
    func readLog() {
        let files = Self.recentRolloutFiles()
        guard let newest = files.first else { return }
        let key = "\(newest.path)|\(newest.mtime.timeIntervalSince1970)"
        guard key != lastLogKey else { return }
        lastLogKey = key
        for file in files.prefix(5) {
            if let snap = Self.lastLimits(inLog: file.path, mtime: file.mtime) {
                fromLog = snap
                return
            }
        }
    }

    func refreshServer(done: @escaping () -> Void) {
        guard !fetching else { return }
        fetching = true
        DispatchQueue.global(qos: .utility).async {
            let result = Self.fetchServerSync()
            DispatchQueue.main.async {
                self.fetching = false
                self.applyServer(result)
                done()
            }
        }
    }

    func applyServer(_ result: (snapshot: ProviderSnapshot?, error: String?)) {
        serverCheckedAt = Date()
        if let snap = result.snapshot {
            fromServer = snap
            serverError = nil
        } else {
            serverError = result.error
        }
    }

    static func windows(from limits: [String: Any]) -> [LimitWindow] {
        var out: [LimitWindow] = []
        for key in ["primary", "secondary"] {
            guard let w = limits[key] as? [String: Any], let used = number(w["usedPercent"] ?? w["used_percent"]) else { continue }
            let minutes = Int(number(w["windowDurationMins"] ?? w["window_minutes"]) ?? 0)
            let reset = number(w["resetsAt"] ?? w["resets_at"]).map { Date(timeIntervalSince1970: $0) }
            out.append(LimitWindow(id: key, label: windowLabel(minutes: minutes), minutes: minutes, usedPercent: used, resetsAt: reset))
        }
        return out.sorted { $0.minutes < $1.minutes }
    }

    /// 오늘·어제 날짜 폴더의 rollout 기록, 최근 것부터
    static func recentRolloutFiles(now: Date = Date()) -> [(path: String, mtime: Date)] {
        let fm = FileManager.default
        let cal = Calendar.current
        var files: [(path: String, mtime: Date)] = []
        for back in 0...1 {
            guard let day = cal.date(byAdding: .day, value: -back, to: now) else { continue }
            let c = cal.dateComponents([.year, .month, .day], from: day)
            let dir = NSHomeDirectory() + String(format: "/.codex/sessions/%04ld/%02ld/%02ld", c.year ?? 0, c.month ?? 0, c.day ?? 0)
            for name in (try? fm.contentsOfDirectory(atPath: dir)) ?? [] where name.hasPrefix("rollout-") && name.hasSuffix(".jsonl") {
                let path = dir + "/" + name
                if let mtime = (try? fm.attributesOfItem(atPath: path))?[.modificationDate] as? Date {
                    files.append((path, mtime))
                }
            }
        }
        return files.sorted { $0.mtime > $1.mtime }
    }

    static func lastLimits(inLog path: String, mtime: Date) -> ProviderSnapshot? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > 2_000_000 ? size - 2_000_000 : 0)
        guard let data = try? handle.readToEnd() else { return nil }
        let marker = Data("\"rate_limits\"".utf8)
        for line in data.split(separator: 0x0A).reversed() where line.range(of: marker) != nil {
            guard let event = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  let payload = event["payload"] as? [String: Any],
                  let limits = payload["rate_limits"] as? [String: Any] else { continue }
            if let id = limits["limit_id"] as? String, id != "codex" { continue }
            let windows = windows(from: limits)
            if windows.isEmpty { continue }
            return ProviderSnapshot(windows: windows,
                                    observedAt: (event["timestamp"] as? String).flatMap(parseDate) ?? mtime,
                                    plan: planName(limits["plan_type"] as? String))
        }
        return nil
    }

    /// codex 를 까는 방법마다 놓이는 곳(독립 설치·Homebrew·npm·nvm·ChatGPT 앱에 들어 있는 것) 가운데 먼저 있는 것
    static func codexExecutable() -> String? {
        let home = NSHomeDirectory()
        var candidates = [home + "/.local/bin/codex", "/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                          home + "/.codex/packages/standalone/current/bin/codex",
                          home + "/.npm-global/bin/codex", home + "/.volta/bin/codex", home + "/.bun/bin/codex"]
        let nvm = home + "/.nvm/versions/node"
        for version in ((try? FileManager.default.contentsOfDirectory(atPath: nvm)) ?? []).sorted().reversed() {
            candidates.append("\(nvm)/\(version)/bin/codex")
        }
        candidates.append("/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex")
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// `codex app-server` 를 잠깐 띄워 account/rateLimits/read 로 지금 한도를 묻는다(인증은 codex 가 알아서 한다)
    static func fetchServerSync() -> (snapshot: ProviderSnapshot?, error: String?) {
        guard let exe = codexExecutable() else { return (nil, "codex 명령을 찾지 못했습니다") }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: exe)
        process.arguments = ["app-server"]
        process.currentDirectoryURL = URL(fileURLWithPath: NSHomeDirectory())
        // Dock 에서 연 앱은 PATH 가 짧다. npm 으로 깐 codex 는 node 스크립트라 같은 폴더의 node 를 찾을 수 있게 앞에 붙인다.
        var env = ProcessInfo.processInfo.environment
        let exeDir = (exe as NSString).deletingLastPathComponent
        env["PATH"] = [exeDir, "/opt/homebrew/bin", "/usr/local/bin", env["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"].joined(separator: ":")
        process.environment = env
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return (nil, "codex 를 실행하지 못했습니다") }

        let killer = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 20, execute: killer)
        defer {
            killer.cancel()
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
            process.waitUntilExit()
        }

        var buffer = Data()
        func send(_ line: String) -> Bool {
            (try? input.fileHandleForWriting.write(contentsOf: Data((line + "\n").utf8))) != nil
        }
        func receive(id: Int) -> [String: Any]? {
            while true {
                if let newline = buffer.firstIndex(of: 0x0A) {
                    let line = buffer.subdata(in: buffer.startIndex..<newline)
                    buffer.removeSubrange(buffer.startIndex...newline)
                    if let message = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                       message["method"] == nil, (message["id"] as? NSNumber)?.intValue == id {
                        return message
                    }
                    continue
                }
                let chunk = output.fileHandleForReading.availableData
                if chunk.isEmpty { return nil }  // 끝났거나 20초가 지나 끊었다
                buffer.append(chunk)
            }
        }

        guard send(#"{"id":1,"method":"initialize","params":{"clientInfo":{"name":"ai-usage-pet","version":"1.0"}}}"#),
              receive(id: 1) != nil else { return (nil, "codex 가 응답하지 않습니다") }
        _ = send(#"{"method":"initialized"}"#)
        guard send(#"{"id":2,"method":"account/rateLimits/read"}"#),
              let response = receive(id: 2) else { return (nil, "codex 가 응답하지 않습니다") }
        guard let result = response["result"] as? [String: Any] else {
            return (nil, "Codex 한도를 읽지 못했습니다(로그인 확인)")
        }
        let byId = result["rateLimitsByLimitId"] as? [String: Any]
        let limits = (byId?["codex"] as? [String: Any]) ?? (result["rateLimits"] as? [String: Any]) ?? [:]
        var snap = ProviderSnapshot(windows: windows(from: limits), observedAt: Date(),
                                    plan: planName(limits["planType"] as? String))
        if snap.windows.isEmpty { snap.message = "표시할 한도가 없습니다" }
        return (snap, nil)
    }
}

// MARK: - 세션 (지금 켜진 Claude Code·Codex 작업이 진행 중인지, 답을 마쳤는지)

/// 창에 놓는 순서도 이 순서: 내 확인을 기다리는 것 → 진행 중 → 답 나옴 → 멈춤
enum SessionState: Int {
    case waiting, working, answered, stopped, unknown
}

enum SessionTool { case claude, codex }

struct AgentSession: Identifiable {
    let id: String
    let tool: SessionTool
    let name: String
    let cwd: String
    let state: SessionState
    let since: Date  // 진행 중이면 시작한 때, 그 밖에는 그 상태가 된 때
    var background = false  // 답은 마쳤지만 뒤에서 명령이 아직 도는 Claude 세션
}

/// 진행 중인 것부터, 같은 상태에서는 최근에 바뀐 것부터
func sortSessions(_ list: [AgentSession]) -> [AgentSession] {
    list.sorted { a, b in
        a.state.rawValue != b.state.rawValue ? a.state.rawValue < b.state.rawValue : a.since > b.since
    }
}

func readJSONObject(_ path: String) -> [String: Any]? {
    guard let data = FileManager.default.contents(atPath: path) else { return nil }
    return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
}

/// 실행 중인 프로세스의 실행 파일 경로(없으면 nil)
func executablePath(_ pid: pid_t) -> String? {
    var buffer = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
    guard proc_pidpath(pid, &buffer, UInt32(buffer.count)) > 0 else { return nil }
    return String(cString: buffer)
}

/// 파일 끝에서 최대 maxBytes 만큼을 줄 단위로(맨 앞의 잘린 줄은 버림)
func tailLines(_ path: String, maxBytes: UInt64) -> [Data] {
    guard let handle = FileHandle(forReadingAtPath: path) else { return [] }
    defer { try? handle.close() }
    let size = (try? handle.seekToEnd()) ?? 0
    let start = size > maxBytes ? size - maxBytes : 0
    try? handle.seek(toOffset: start)
    guard let data = try? handle.readToEnd() else { return [] }
    var lines = data.split(separator: 0x0A, omittingEmptySubsequences: true)
    if start > 0, !lines.isEmpty { lines.removeFirst() }
    return lines.map { Data($0) }
}

/// Claude Code 는 실행 중인 세션마다 ~/.claude/sessions/<pid>.json 에 상태를 남긴다
/// (status: busy·shell=진행 중, idle=답 나옴, waiting=권한 묻기 등에서 내 답을 기다림).
/// 공식 문서에 없는 형식이라 모르는 값이 오면 「알 수 없음」으로 둔다.
final class ClaudeSessionReader {
    private var lastState: [String: SessionState] = [:]
    private var workingSince: [String: Date] = [:]
    private var transcriptPaths: [String: String] = [:]
    private var tailCache: [String: (size: UInt64, turnEndedAt: Date?, promptAt: Date?)] = [:]

    func read(now: Date) -> [AgentSession] {
        let dir = NSHomeDirectory() + "/.claude/sessions"
        var result: [AgentSession] = []
        var seen = Set<String>()
        for file in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] where file.hasSuffix(".json") {
            guard let obj = readJSONObject(dir + "/" + file),
                  let pid = (obj["pid"] as? NSNumber)?.int32Value,
                  let exe = executablePath(pid), exe.lowercased().contains("claude") else { continue }  // 꺼진 세션이 남긴 파일은 건너뛴다
            let sessionId = obj["sessionId"] as? String ?? file
            let cwd = obj["cwd"] as? String ?? ""
            let given = (obj["name"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
            let name = given.isEmpty ? (cwd as NSString).lastPathComponent : given
            let changedAt = number(obj["statusUpdatedAt"]).map { Date(timeIntervalSince1970: $0 / 1000) } ?? now
            let key = "\(pid):\(sessionId)"
            seen.insert(key)

            var state: SessionState
            var since = changedAt
            var promptAt: Date?
            var background = false
            switch obj["status"] as? String {
            case "busy", "shell":
                // 뒤에서 도는 작업(백그라운드 명령 등)이 남아 있으면 답을 마친 뒤에도 busy 로 남는다 → 대화 기록 끝으로 가린다
                // 처음 보는 세션이면 그 차례를 연 내 말을 찾으러 더 깊이(8MB) 읽는다 — 화면 캡처 등으로 기록이 금방 커진다
                let tail = transcriptTail(sessionId: sessionId, deep: lastState[key] == nil)
                if let ended = tail.turnEndedAt {
                    state = .answered
                    since = ended
                    background = true
                } else {
                    state = .working
                    promptAt = tail.promptAt
                }
            case "idle": state = .answered
            case "waiting", "blocked": state = .waiting
            default: state = .unknown
            }

            if state == .working {
                if lastState[key] == .working, let kept = workingSince[key] {
                    since = kept
                } else if let before = lastState[key], before != .working {
                    since = promptAt ?? now  // 이 앱이 보는 동안 진행 중으로 바뀜
                } else {
                    since = promptAt ?? changedAt  // 앱을 막 켰을 때
                }
                workingSince[key] = since
            } else {
                workingSince[key] = nil
            }
            lastState[key] = state
            result.append(AgentSession(id: "claude:" + key, tool: .claude, name: name, cwd: cwd, state: state, since: since,
                                       background: background))
        }
        lastState = lastState.filter { seen.contains($0.key) }
        workingSince = workingSince.filter { seen.contains($0.key) }
        return result
    }

    /// 대화 기록(~/.claude/projects/<폴더>/<세션>.jsonl) 끝에서: 마지막 차례가 끝났는지(turn_duration), 아니면 그 차례를 연 내 말의 시각
    private func transcriptTail(sessionId: String, deep: Bool) -> (turnEndedAt: Date?, promptAt: Date?) {
        guard let path = transcriptPath(sessionId) else { return (nil, nil) }
        // 기록이 그대로면 지난번 결과를 쓴다(3초마다 512KB 를 다시 읽지 않게)
        let size = ((try? FileManager.default.attributesOfItem(atPath: path))?[.size] as? NSNumber)?.uint64Value ?? 0
        if let cached = tailCache[path], cached.size == size { return (cached.turnEndedAt, cached.promptAt) }
        let result = scanTail(path, maxBytes: deep ? 8 << 20 : 512 << 10)
        tailCache[path] = (size, result.turnEndedAt, result.promptAt)
        return result
    }

    private func scanTail(_ path: String, maxBytes: UInt64) -> (turnEndedAt: Date?, promptAt: Date?) {
        for line in tailLines(path, maxBytes: maxBytes).reversed() {
            guard line.range(of: Data("\"type\":\"user\"".utf8)) != nil
                    || line.range(of: Data("\"type\":\"assistant\"".utf8)) != nil
                    || line.range(of: Data("\"turn_duration\"".utf8)) != nil,
                  let entry = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { continue }
            let at = (entry["timestamp"] as? String).flatMap(parseDate)
            switch entry["type"] as? String {
            case "system" where entry["subtype"] as? String == "turn_duration":
                return (at, nil)
            case "assistant":
                continue
            case "user":
                if entry["isMeta"] as? Bool == true { continue }
                let message = entry["message"] as? [String: Any]
                if message?["content"] is String { return (nil, at) }
                let parts = message?["content"] as? [[String: Any]] ?? []
                let types = Set(parts.compactMap { $0["type"] as? String })
                if !types.contains("tool_result") && !types.isEmpty { return (nil, at) }
            default:
                continue
            }
        }
        return (nil, nil)
    }

    private func transcriptPath(_ sessionId: String) -> String? {
        if let cached = transcriptPaths[sessionId], FileManager.default.fileExists(atPath: cached) { return cached }
        let root = NSHomeDirectory() + "/.claude/projects"
        for folder in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? [] {
            let path = "\(root)/\(folder)/\(sessionId).jsonl"
            if FileManager.default.fileExists(atPath: path) {
                transcriptPaths[sessionId] = path
                return path
            }
        }
        return nil
    }
}

/// Codex 는 작업마다 ~/.codex/sessions/<날짜>/rollout-*.jsonl 에 기록을 남긴다.
/// 마지막 표시가 task_started 면 진행 중, task_complete 면 답 나옴, turn_aborted 면 멈춤.
/// 진행 중으로 남았는데 그 파일을 연 codex 프로세스가 없으면 도중에 꺼진 것(멈춤).
final class CodexSessionReader {
    /// 끝난 작업을 창에 남겨 두는 시간
    static let keepFinished: TimeInterval = 30 * 60

    private struct Track {
        var offset: UInt64 = 0
        var cwd = ""
        var isSubagent = false
        var startedAt: Date?
        var lastKind: String?
        var lastAt: Date?
    }
    private var tracks: [String: Track] = [:]

    func read(now: Date) -> [AgentSession] {
        let open = Self.openRolloutFiles()
        var result: [AgentSession] = []
        var seen = Set<String>()
        for file in CodexReader.recentRolloutFiles(now: now) {
            let isOpen = open.contains(file.path)
            if !isOpen && now.timeIntervalSince(file.mtime) > Self.keepFinished { continue }
            seen.insert(file.path)
            var track = tracks[file.path] ?? Track()
            Self.readNewLines(file.path, into: &track)
            tracks[file.path] = track
            if track.isSubagent { continue }  // Codex 가 스스로 띄운 하위 작업은 빼고 본 작업만

            let state: SessionState
            let since: Date
            switch track.lastKind {
            case "task_complete":
                state = .answered; since = track.lastAt ?? file.mtime
            case "turn_aborted":
                state = .stopped; since = track.lastAt ?? file.mtime
            case "task_started":
                if isOpen { state = .working; since = track.lastAt ?? file.mtime } else { state = .stopped; since = file.mtime }
            default:
                guard isOpen else { continue }
                state = .working; since = track.startedAt ?? file.mtime
            }
            let name = track.cwd.isEmpty ? "Codex" : (track.cwd as NSString).lastPathComponent
            result.append(AgentSession(id: "codex:" + file.path, tool: .codex, name: name, cwd: track.cwd, state: state, since: since))
        }
        tracks = tracks.filter { seen.contains($0.key) }
        // 같은 폴더에서 Codex 를 다시 돌리면(설계 → 구현 등) 앞의 끝난 기록은 빼고 최근 것만
        let latest = Dictionary(result.map { ($0.cwd, $0.since) }, uniquingKeysWith: max)
        return result.filter { $0.state == .working || $0.cwd.isEmpty || $0.since >= latest[$0.cwd, default: $0.since] }
    }

    /// 지난번 읽은 곳 다음부터 끝난 줄만 읽는다(기록은 수 MB 까지 커진다)
    private static func readNewLines(_ path: String, into track: inout Track) {
        guard let handle = FileHandle(forReadingAtPath: path) else { return }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        if size < track.offset { track = Track() }  // 파일이 새로 쓰였으면 처음부터
        guard size > track.offset else { return }
        try? handle.seek(toOffset: track.offset)
        guard let data = try? handle.readToEnd(), let lastNewline = data.lastIndex(of: 0x0A) else { return }
        let complete = data[data.startIndex...lastNewline]
        track.offset += UInt64(complete.count)
        let markers = ["\"session_meta\"", "\"task_started\"", "\"task_complete\"", "\"turn_aborted\""].map { Data($0.utf8) }
        for line in complete.split(separator: 0x0A) where markers.contains(where: { line.range(of: $0) != nil }) {
            guard let entry = (try? JSONSerialization.jsonObject(with: Data(line))) as? [String: Any],
                  let payload = entry["payload"] as? [String: Any] else { continue }
            let at = (entry["timestamp"] as? String).flatMap(parseDate)
            switch entry["type"] as? String {
            case "session_meta":
                track.cwd = payload["cwd"] as? String ?? track.cwd
                track.isSubagent = payload["source"] is [String: Any]  // 하위 작업은 source 가 {subagent: …}
                track.startedAt = at
            case "event_msg":
                if let kind = payload["type"] as? String, ["task_started", "task_complete", "turn_aborted"].contains(kind) {
                    track.lastKind = kind
                    track.lastAt = at
                }
            default:
                break
            }
        }
    }

    /// 실행 파일 이름이 codex 로 시작하는 프로세스들이 열어 둔 rollout 기록 파일
    static func openRolloutFiles() -> Set<String> {
        var result = Set<String>()
        let count = proc_listallpids(nil, 0)
        guard count > 0 else { return result }
        var pids = [pid_t](repeating: 0, count: Int(count) + 64)
        let filled = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        for pid in pids.prefix(Int(max(0, filled))) where pid > 0 {
            guard let exe = executablePath(pid), (exe as NSString).lastPathComponent.hasPrefix("codex") else { continue }
            let bytes = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
            guard bytes > 0 else { continue }
            let stride = MemoryLayout<proc_fdinfo>.stride
            var fds = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(bytes) / stride + 16)
            let got = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, &fds, Int32(fds.count * stride))
            for fd in fds.prefix(Int(max(0, got)) / stride) where fd.proc_fdtype == UInt32(PROX_FDTYPE_VNODE) {
                var info = vnode_fdinfowithpath()
                let size = Int32(MemoryLayout<vnode_fdinfowithpath>.size)
                guard proc_pidfdinfo(pid, fd.proc_fd, PROC_PIDFDVNODEPATHINFO, &info, size) == size else { continue }
                let path = withUnsafeBytes(of: info.pvip.vip_path) { raw in
                    String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self)
                }
                if path.contains("/rollout-") && path.hasSuffix(".jsonl") { result.insert(path) }
            }
        }
        return result
    }
}

// MARK: - 글자

private func makeFormatter(_ format: String) -> DateFormatter {
    let f = DateFormatter()
    f.locale = Locale(identifier: "ko_KR")
    f.dateFormat = format
    return f
}
private let clockFormat = makeFormatter("HH:mm")
private let weekdayClockFormat = makeFormatter("E HH:mm")
private let dateClockFormat = makeFormatter("M/d HH:mm")
private let fullFormat = makeFormatter("M월 d일(E) HH:mm")

func clock(_ date: Date) -> String { clockFormat.string(from: date) }
func fullTime(_ date: Date) -> String { fullFormat.string(from: date) }

func resetText(_ date: Date?, now: Date) -> String {
    guard let date else { return "" }
    if date <= now { return "초기화됨" }
    if Calendar.current.isDate(date, inSameDayAs: now) { return clockFormat.string(from: date) }
    return (date.timeIntervalSince(now) < 6 * 86_400 ? weekdayClockFormat : dateClockFormat).string(from: date)
}

func ageText(_ date: Date?, now: Date) -> String {
    guard let date else { return "" }
    let seconds = max(0, now.timeIntervalSince(date))
    if seconds < 60 { return "방금" }
    if seconds < 3_600 { return "\(Int(seconds / 60))분 전" }
    if seconds < 86_400 { return "\(Int(seconds / 3_600))시간 전" }
    return "\(Int(seconds / 86_400))일 전"
}

/// 「12분」「1시간 5분」, 1분이 안 되면 nil
func durationText(since: Date, now: Date) -> String? {
    let minutes = Int(max(0, now.timeIntervalSince(since)) / 60)
    if minutes < 1 { return nil }
    if minutes < 60 { return "\(minutes)분" }
    return minutes % 60 == 0 ? "\(minutes / 60)시간" : "\(minutes / 60)시간 \(minutes % 60)분"
}

/// 세션 줄 오른쪽 글자: 「진행 중 12분」「답 나옴 8분 전」「확인 기다림」「멈춤 25분 전」
func sessionStatusText(_ s: AgentSession, now: Date) -> String {
    switch s.state {
    case .working: return ["진행 중", durationText(since: s.since, now: now)].compactMap { $0 }.joined(separator: " ")
    case .answered: return "답 나옴 \(ageText(s.since, now: now))"
    case .waiting: return "확인 기다림"
    case .stopped: return "멈춤 \(ageText(s.since, now: now))"
    case .unknown: return "알 수 없음"
    }
}

func describeSession(_ s: AgentSession, now: Date) -> String {
    let home = NSHomeDirectory()
    let place = s.cwd.hasPrefix(home) ? "~" + s.cwd.dropFirst(home.count) : s.cwd
    let note = s.background ? " (뒤에서 명령이 아직 도는 중)" : ""
    return "\(s.tool == .claude ? "Claude" : "Codex") · \(s.name) — \(sessionStatusText(s, now: now))\(note)\(place.isEmpty ? "" : " · \(place)")"
}

/// 상태 표시줄과 같은 기준: 50% 이상 초록, 20% 이상 노랑, 그 아래 빨강
func statusColor(_ left: Int) -> Color {
    if left >= 50 { return Color(nsColor: .systemGreen) }
    if left >= 20 { return Color(nsColor: .systemYellow) }
    return Color(nsColor: .systemRed)
}

func describe(_ title: String, _ snap: ProviderSnapshot, now: Date) -> String {
    var lines = ["\(title)\(snap.plan.map { " (\($0))" } ?? "") — 마지막 값 \(snap.observedAt.map(fullTime) ?? "없음")"]
    if snap.windows.isEmpty { lines.append("  \(snap.message ?? "값 없음")") }
    for w in snap.windows {
        lines.append("  \(w.label): 사용 \(w.usedPercent)% → \(w.leftPercent(now: now))% 남음, 초기화 \(w.resetsAt.map(fullTime) ?? "-")")
    }
    return lines.joined(separator: "\n")
}

// MARK: - 화면

/// 창에 보일 칸 — 오른쪽 클릭·Dock 메뉴에서 고르고 기억한다(처음엔 둘 다, 하나는 꼭 남는다)
enum Shown {
    static let claudeKey = "showClaude"
    static let codexKey = "showCodex"
    static let sessionsKey = "showSessions"
    static func get(_ key: String) -> Bool { UserDefaults.standard.object(forKey: key) as? Bool ?? true }
}

final class UsageModel: ObservableObject {
    @Published var claude = ProviderSnapshot()
    @Published var codex = ProviderSnapshot()
    @Published var sessions: [AgentSession] = []
    @Published var now = Date()
    @Published var showClaude = Shown.get(Shown.claudeKey)
    @Published var showCodex = Shown.get(Shown.codexKey)
    @Published var showSessions = Shown.get(Shown.sessionsKey)
}

let claudeTint = Color(red: 0.851, green: 0.467, blue: 0.341)
let codexTint = Color(red: 0.063, green: 0.639, blue: 0.498)

struct PetView: View {
    @ObservedObject var model: UsageModel

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            if model.showSessions {
                SessionSection(sessions: model.sessions, now: model.now)
                Rectangle().fill(Color.white.opacity(0.08)).frame(height: 1)
            }
            if model.showClaude {
                ProviderSection(name: "Claude", detail: nil, color: claudeTint, snap: model.claude, now: model.now)
            }
            if model.showClaude && model.showCodex {
                Rectangle().fill(Color.white.opacity(0.08)).frame(height: 1)
            }
            if model.showCodex {
                ProviderSection(name: "ChatGPT", detail: ["Codex", model.codex.plan].compactMap { $0 }.joined(separator: " · "),
                                color: codexTint, snap: model.codex, now: model.now)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(width: 290)
    }
}

struct ProviderSection: View {
    let name: String
    let detail: String?
    let color: Color
    let snap: ProviderSnapshot
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Circle().fill(color).frame(width: 7, height: 7)
                Text(name).font(.system(size: 12, weight: .semibold))
                if let detail, !detail.isEmpty {
                    Text(detail).font(.system(size: 10)).foregroundStyle(.secondary)
                }
                Spacer(minLength: 4)
                Text(ageText(snap.observedAt, now: now)).font(.system(size: 10)).foregroundStyle(.tertiary)
            }
            if snap.windows.isEmpty {
                Text(snap.message ?? "값이 없습니다").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            ForEach(snap.windows) { window in
                WindowRow(window: window, now: now)
            }
        }
    }
}

struct WindowRow: View {
    let window: LimitWindow
    let now: Date

    var body: some View {
        let left = window.leftPercent(now: now)
        let tint = statusColor(left)
        let reset = resetText(window.resetsAt, now: now)
        HStack(spacing: 8) {
            Text(window.label).font(.system(size: 11)).foregroundStyle(.secondary).frame(width: 30, alignment: .leading)
            Bar(fraction: Double(left) / 100, color: tint)
            Text("\(left)% 남음").font(.system(size: 11, weight: .semibold).monospacedDigit())
                .foregroundStyle(tint).frame(width: 62, alignment: .trailing)
            Group {
                if reset.isEmpty || reset == "초기화됨" {
                    Text(reset)
                } else {
                    Text("\(Image(systemName: "arrow.counterclockwise")) \(reset)")
                }
            }
            .font(.system(size: 10).monospacedDigit())
            .foregroundStyle(.secondary)
            .frame(width: 70, alignment: .trailing)
        }
    }
}

struct Bar: View {
    let fraction: Double
    let color: Color

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.12))
                Capsule().fill(color).frame(width: max(3, geo.size.width * min(1, max(0, fraction))))
            }
        }
        .frame(height: 5)
    }
}

/// 세션 목록 — 한 줄에 「상태 표시 · 도구 점 · 이름 · 상태 글자」
struct SessionSection: View {
    let sessions: [AgentSession]
    let now: Date
    static let maxRows = 8

    var body: some View {
        let count = { (state: SessionState) in sessions.filter { $0.state == state }.count }
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Image(systemName: "list.bullet").font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary).frame(width: 7)
                Text("세션").font(.system(size: 12, weight: .semibold))
                Spacer(minLength: 4)
                Text("진행 \(count(.working)) · 답 \(count(.answered)) · 확인 \(count(.waiting))")
                    .font(.system(size: 10).monospacedDigit()).foregroundStyle(.tertiary)
            }
            if sessions.isEmpty {
                Text("켜져 있는 세션이 없습니다").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            ForEach(sessions.prefix(Self.maxRows)) { SessionRow(session: $0, now: now) }
            if sessions.count > Self.maxRows {
                Text("외 \(sessions.count - Self.maxRows)개").font(.system(size: 10)).foregroundStyle(.tertiary).padding(.leading, 30)
            }
        }
    }
}

struct SessionRow: View {
    let session: AgentSession
    let now: Date

    var body: some View {
        let style = Self.style(session.state)
        let dim = session.state == .stopped || session.state == .unknown
        HStack(spacing: 6) {
            Group {
                if session.state == .working {
                    SpinningSymbol(name: style.icon, color: .systemBlue)
                } else {
                    Image(systemName: style.icon)
                }
            }
            .font(.system(size: 11, weight: .semibold)).foregroundStyle(style.tint).frame(width: 13)
            Circle().fill(session.tool == .claude ? claudeTint : codexTint).frame(width: 5, height: 5)
            Text(session.name).font(.system(size: 11)).lineLimit(1).truncationMode(.middle)
                .foregroundStyle(dim ? Color.secondary : Color.primary)
            Spacer(minLength: 6)
            Text(sessionStatusText(session, now: now))
                .font(.system(size: 10, weight: dim ? .regular : .medium).monospacedDigit())
                .foregroundStyle(dim ? Color.secondary : style.tint)
                .fixedSize()
        }
    }

    static func style(_ state: SessionState) -> (icon: String, tint: Color) {
        switch state {
        case .waiting: return ("exclamationmark.circle.fill", Color(nsColor: .systemOrange))
        case .working: return ("circle.dotted", Color(nsColor: .systemBlue))
        case .answered: return ("checkmark.circle.fill", Color(nsColor: .systemGreen))
        case .stopped: return ("stop.circle", Color.secondary)
        case .unknown: return ("questionmark.circle", Color.secondary)
        }
    }
}

/// 그림 저장(--snapshot) 중에는 도는 표시 대신 멈춘 기호를 그린다(ImageRenderer 는 AppKit 뷰를 못 그린다)
var renderingStill = false

/// 진행 중 표시 — 2초에 한 바퀴 돈다. 회전은 Core Animation 에 맡겨 화면 합성기가 돌리므로 앱 CPU 를 거의 안 쓴다
/// (SwiftUI 애니메이션·기호 효과는 앱이 매 프레임 다시 그려 3~5% 를 썼다). 「동작 줄이기」를 켜 두면 멈춰 있다.
struct SpinningSymbol: View {
    let name: String
    let color: NSColor

    var body: some View {
        if renderingStill || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            Image(systemName: name)
        } else {
            SpinningLayer(name: name, color: color).frame(width: 13, height: 13)
        }
    }
}

struct SpinningLayer: NSViewRepresentable {
    let name: String
    let color: NSColor
    func makeNSView(context: Context) -> SpinnerView { SpinnerView(name: name, color: color) }
    func updateNSView(_ view: SpinnerView, context: Context) {}
}

final class SpinnerView: NSView {
    private let symbol = CALayer()
    private let image: NSImage?

    init(name: String, color: NSColor) {
        let config = NSImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
            .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
        image = NSImage(systemSymbolName: name, accessibilityDescription: "진행 중")?.withSymbolConfiguration(config)
        super.init(frame: NSRect(x: 0, y: 0, width: 13, height: 13))
        wantsLayer = true
        symbol.contentsGravity = .center
        layer?.addSublayer(symbol)
    }

    required init?(coder: NSCoder) { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        symbol.bounds = bounds
        symbol.position = CGPoint(x: bounds.midX, y: bounds.midY)  // 가운데를 축으로 돈다
        CATransaction.commit()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window else { return }
        let scale = window.backingScaleFactor
        symbol.contentsScale = scale
        symbol.contents = image?.layerContents(forContentsScale: scale)
        guard symbol.animation(forKey: "spin") == nil else { return }
        let spin = CABasicAnimation(keyPath: "transform.rotation.z")
        spin.fromValue = 0
        spin.toValue = -2 * Double.pi  // 시계 방향
        spin.duration = 2
        spin.repeatCount = .infinity
        spin.isRemovedOnCompletion = false
        symbol.add(spin, forKey: "spin")
    }
}

// MARK: - 창

/// 글자 위를 눌러도 창이 끌리도록 클릭은 모두 이 뷰가 받는다
final class ContainerView: NSVisualEffectView {
    var onDoubleClick: (() -> Void)?
    var onRightClick: ((NSEvent) -> Void)?

    override func hitTest(_ point: NSPoint) -> NSView? {
        bounds.contains(convert(point, from: superview)) ? self : nil
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { onDoubleClick?() } else { window?.performDrag(with: event) }
    }
    override func rightMouseDown(with event: NSEvent) { onRightClick?(event) }
}

func roundedMask(radius: CGFloat) -> NSImage {
    let edge = radius * 2 + 1
    let image = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
        NSColor.black.setFill()
        NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
        return true
    }
    image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
    image.resizingMode = .stretch
    return image
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuItemValidation {
    private let model = UsageModel()
    private let claude = ClaudeReader()
    private let codex = CodexReader()
    private let claudeSessions = ClaudeSessionReader()
    private let codexSessions = CodexSessionReader()
    private var panel: NSPanel!
    /// 사람이 둔 창의 왼쪽 아래 모서리. 창 높이가 바뀌어도 이 자리를 지키고 위로 늘거나 준다.
    private var anchor = NSPoint.zero
    private var appliedOrigin: NSPoint?
    private var anchorFromOldTop = false  // 예전 판이 기억한 위 모서리로 처음 띄웠으면, 값이 다 찬 높이로 한 번 다시 잰다
    private var container: ContainerView!
    private var hosting: NSHostingView<PetView>!

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        buildPanel()
        refreshAll()
        Timer.scheduledTimer(timeInterval: 15, target: self, selector: #selector(refreshLocal), userInfo: nil, repeats: true)
        Timer.scheduledTimer(timeInterval: 3, target: self, selector: #selector(refreshSessions), userInfo: nil, repeats: true)
        Timer.scheduledTimer(timeInterval: 300, target: self, selector: #selector(refreshServer), userInfo: nil, repeats: true)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(refreshAll),
                                                          name: NSWorkspace.didWakeNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(savePosition),
                                               name: NSWindow.didMoveNotification, object: panel)
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged),
                                               name: NSApplication.didChangeScreenParametersNotification, object: nil)
    }

    private func buildPanel() {
        hosting = NSHostingView(rootView: PetView(model: model))
        let size = hosting.fittingSize
        container = ContainerView(frame: NSRect(origin: .zero, size: size))
        container.material = .hudWindow
        container.blendingMode = .behindWindow
        container.state = .active
        container.maskImage = roundedMask(radius: 14)
        container.onDoubleClick = { [weak self] in self?.refreshAll() }
        container.onRightClick = { [weak self] event in self?.showMenu(event) }
        hosting.frame = container.bounds
        hosting.autoresizingMask = [.width, .height]
        container.addSubview(hosting)

        // Codex 펫처럼: 다른 창 위, 모든 데스크톱·전체 화면 앱 위에 뜨고, 눌러도 지금 쓰는 앱의 초점을 뺏지 않는다
        panel = NSPanel(contentRect: NSRect(origin: .zero, size: size),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.contentView = container
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.isReleasedWhenClosed = false
        panel.appearance = NSAppearance(named: .darkAqua)
        placePanel(reset: false)
        panel.orderFrontRegardless()
    }

    @objc private func refreshLocal() {
        model.now = Date()
        model.claude = claude.read(now: model.now)
        codex.readLog()
        model.codex = codex.current
        refreshSessions()
        afterUpdate()
    }

    /// 세션 상태는 자주 바뀌므로 3초마다 읽는다(파일 몇 개와 기록 끝부분만 읽어 가볍다)
    @objc private func refreshSessions() {
        guard model.showSessions else {
            if !model.sessions.isEmpty { model.sessions = [] }
            return
        }
        let now = Date()
        let sessions = sortSessions(claudeSessions.read(now: now) + codexSessions.read(now: now))
        let resized = sessions.count != model.sessions.count
        model.now = now
        model.sessions = sessions
        if resized { afterUpdate() }
    }

    @objc private func refreshServer() {
        guard model.showCodex else { return }  // ChatGPT 칸을 껐으면 codex 를 띄우지 않는다
        codex.refreshServer { [weak self] in
            guard let self else { return }
            self.model.codex = self.codex.current
            self.afterUpdate()
        }
    }

    @objc private func refreshAll() {
        refreshLocal()
        refreshServer()
    }

    private func afterUpdate() {
        container.toolTip = detailLines().joined(separator: "\n")
        DispatchQueue.main.async { self.fitPanel() }  // SwiftUI 가 새 값으로 다시 그린 뒤 크기를 맞춘다
    }

    /// 내용 높이가 바뀌면 왼쪽 아래 모서리를 그대로 두고(위로 늘고 준다) 창 크기를 맞춘다
    private func fitPanel() {
        let size = hosting.fittingSize
        guard size.width > 0, size.height > 0 else { return }
        if anchorFromOldTop {
            anchorFromOldTop = false
            anchor = NSPoint(x: panel.frame.minX, y: panel.frame.maxY - size.height)
            saveAnchor()
        }
        guard size != panel.frame.size else { return }
        applyFrame(size: size)
    }

    /// 자리(anchor)에 창을 놓는다. 위로 늘다가 화면 위 끝을 넘으면 넘친 만큼만 아래로 민다(자리는 그대로 기억).
    private func applyFrame(size: NSSize) {
        var origin = anchor
        let probe = NSPoint(x: anchor.x + 10, y: anchor.y + 10)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(probe) }) ?? panel.screen,
           origin.y + size.height > screen.visibleFrame.maxY {
            origin.y = screen.visibleFrame.maxY - size.height
        }
        appliedOrigin = origin
        panel.setFrame(NSRect(origin: origin, size: size), display: true)
        panel.invalidateShadow()
    }

    private func placePanel(reset: Bool) {
        let defaults = UserDefaults.standard
        if !reset, let saved = defaults.array(forKey: "bottomLeft") as? [Double], saved.count == 2 {
            let point = NSPoint(x: saved[0], y: saved[1])
            if NSScreen.screens.contains(where: { $0.frame.contains(NSPoint(x: point.x + 10, y: point.y + 10)) }) {
                anchor = point
                applyFrame(size: panel.frame.size)
                return
            }
        }
        // 1.2 까지는 위 모서리를 기억했다 — 그 자리에서 이어서 시작한다
        if !reset, let old = defaults.array(forKey: "topLeft") as? [Double], old.count == 2 {
            let point = NSPoint(x: old[0], y: old[1])
            if NSScreen.screens.contains(where: { $0.visibleFrame.contains(NSPoint(x: point.x + 10, y: point.y - 10)) }) {
                anchor = NSPoint(x: point.x, y: point.y - panel.frame.height)
                anchorFromOldTop = true
                applyFrame(size: panel.frame.size)
                return
            }
        }
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        anchor = NSPoint(x: visible.maxX - panel.frame.width - 16, y: visible.maxY - 16 - panel.frame.height)
        applyFrame(size: panel.frame.size)
        if reset { saveAnchor() }
    }

    /// 창을 끌어 옮겼을 때만 새 자리로 기억한다(높이 맞추기·화면 위 끝에 밀린 이동은 빼고)
    @objc private func savePosition() {
        guard panel.frame.origin != appliedOrigin else { return }
        anchor = panel.frame.origin
        appliedOrigin = anchor
        saveAnchor()
    }

    private func saveAnchor() {
        UserDefaults.standard.set([anchor.x, anchor.y], forKey: "bottomLeft")
        UserDefaults.standard.removeObject(forKey: "topLeft")
    }

    @objc private func screensChanged() { placePanel(reset: false) }  // 모니터를 빼서 화면 밖에 남으면 기본 위치로
    @objc private func resetPosition() { placePanel(reset: true) }
    @objc private func quit() { NSApp.terminate(nil) }

    private func detailLines() -> [String] {
        let now = model.now
        var lines: [String] = []
        func add(_ title: String, _ snap: ProviderSnapshot) {
            lines.append("\(title) · 마지막 값 \(snap.observedAt.map(fullTime) ?? "없음")")
            for w in snap.windows {
                lines.append("    \(w.label) \(w.leftPercent(now: now))% 남음 · 초기화 \(w.resetsAt.map(fullTime) ?? "-")")
            }
        }
        if model.showClaude { add("Claude (Claude Code 상태 표시줄)", model.claude) }
        if model.showCodex {
            add("ChatGPT (Codex 한도)", model.codex)
            lines.append("Codex 서버 확인 \(codex.serverCheckedAt.map(clock) ?? "전")\(codex.serverError.map { " — \($0)" } ?? "")")
        }
        if model.showSessions {
            for s in model.sessions { lines.append("세션 · " + describeSession(s, now: now)) }
        }
        return lines
    }

    private func showMenu(_ event: NSEvent) {
        let menu = NSMenu()
        for line in detailLines() {
            let info = NSMenuItem(title: line, action: nil, keyEquivalent: "")
            info.isEnabled = false
            menu.addItem(info)
        }
        menu.addItem(.separator())
        menu.addItem(menuItem("지금 새로고침", #selector(refreshAll)))
        menu.addItem(menuItem("위치 초기화", #selector(resetPosition)))
        addShownItems(to: menu)
        menu.addItem(menuItem("로그인할 때 자동으로 열기", #selector(toggleLoginItem)))
        menu.addItem(.separator())
        menu.addItem(menuItem("종료", #selector(quit)))
        NSMenu.popUpContextMenu(menu, with: event, for: container)
    }

    private func menuItem(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    /// 「Claude 보이기」「ChatGPT 보이기」「세션 보이기」 — 앞뒤를 구분선으로 묶는다
    private func addShownItems(to menu: NSMenu) {
        menu.addItem(.separator())
        menu.addItem(menuItem("Claude 보이기", #selector(toggleClaude)))
        menu.addItem(menuItem("ChatGPT 보이기", #selector(toggleCodex)))
        menu.addItem(menuItem("세션 보이기", #selector(toggleSessions)))
        menu.addItem(.separator())
    }

    @objc private func toggleClaude() { setShown(claude: !model.showClaude, codex: model.showCodex) }
    @objc private func toggleCodex() { setShown(claude: model.showClaude, codex: !model.showCodex) }

    @objc private func toggleSessions() {
        model.showSessions.toggle()
        UserDefaults.standard.set(model.showSessions, forKey: Shown.sessionsKey)
        refreshSessions()
        afterUpdate()
    }

    private func setShown(claude: Bool, codex: Bool) {
        guard claude || codex else { return }  // 둘 다 끄면 빈 창이 되므로 하나는 남긴다
        let codexTurnedOn = codex && !model.showCodex
        model.showClaude = claude
        model.showCodex = codex
        UserDefaults.standard.set(claude, forKey: Shown.claudeKey)
        UserDefaults.standard.set(codex, forKey: Shown.codexKey)
        if codexTurnedOn { refreshServer() }
        afterUpdate()
    }

    // MARK: Dock 앱

    /// 앱이 앞에 있을 때 위쪽 메뉴 막대에 나오는 메뉴(⌘R 새로고침, ⌘Q 종료)
    private func buildMainMenu() {
        let appMenu = NSMenu()
        appMenu.addItem(menuItem("지금 새로고침", #selector(refreshAll), key: "r"))
        appMenu.addItem(menuItem("위치 초기화", #selector(resetPosition)))
        addShownItems(to: appMenu)
        appMenu.addItem(menuItem("로그인할 때 자동으로 열기", #selector(toggleLoginItem)))
        appMenu.addItem(.separator())
        appMenu.addItem(menuItem("AI 사용량 종료", #selector(quit), key: "q"))
        let appItem = NSMenuItem()
        appItem.submenu = appMenu
        let mainMenu = NSMenu()
        mainMenu.addItem(appItem)
        NSApp.mainMenu = mainMenu
    }

    /// Dock 아이콘을 누르면 창을 맨 앞으로(다른 데스크톱에 있다가 돌아왔을 때 등)
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        panel.orderFrontRegardless()
        return true
    }

    /// Dock 아이콘 오른쪽 클릭 메뉴(「종료」는 macOS 가 붙여 준다)
    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let menu = NSMenu()
        menu.addItem(menuItem("지금 새로고침", #selector(refreshAll)))
        menu.addItem(menuItem("위치 초기화", #selector(resetPosition)))
        addShownItems(to: menu)
        menu.addItem(menuItem("로그인할 때 자동으로 열기", #selector(toggleLoginItem)))
        return menu
    }

    /// 시스템 설정 → 일반 → 로그인 항목에 이 앱을 넣고 뺀다
    @objc private func toggleLoginItem() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled {
                try service.unregister()
            } else {
                try service.register()
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = "로그인 항목을 바꾸지 못했습니다"
            alert.informativeText = "시스템 설정 → 일반 → 로그인 항목에서 「AI 사용량」을 직접 추가하거나 빼 주세요.\n(\(error.localizedDescription))"
            alert.addButton(withTitle: "시스템 설정 열기")
            alert.addButton(withTitle: "닫기")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn { SMAppService.openSystemSettingsLoginItems() }
            return
        }
        if service.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(toggleLoginItem):
            menuItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        case #selector(toggleClaude):
            menuItem.state = model.showClaude ? .on : .off
            return model.showCodex  // 혼자 켜져 있으면 끌 수 없다(흐리게)
        case #selector(toggleCodex):
            menuItem.state = model.showCodex ? .on : .off
            return model.showClaude
        case #selector(toggleSessions):
            menuItem.state = model.showSessions ? .on : .off
        default:
            break
        }
        return true
    }
}

// MARK: - Claude Code 상태 표시줄 (--statusline)

/// Claude Code 가 넘겨준 JSON 에서 한도를 세션별 파일로 남기고, 상태 표시줄 글자를 돌려준다.
/// 윈도우 판 AIUsageStatus --statusline 과 같은 모양. jq 없이 돌아서 다른 맥에 그대로 걸 수 있다.
enum StatusLine {
    static func handle(_ input: Data) -> String {
        guard let root = (try? JSONSerialization.jsonObject(with: input)) as? [String: Any] else { return "" }
        saveLimits(root)
        return render(root)
    }

    static func saveLimits(_ root: [String: Any]) {
        guard let id = root["session_id"] as? String, id.range(of: "^[A-Za-z0-9_-]{1,128}$", options: .regularExpression) != nil,
              let limits = root["rate_limits"] as? [String: Any],
              let data = try? JSONSerialization.data(withJSONObject: limits) else { return }
        let dir = NSHomeDirectory() + "/.cache/ai-usage/claude"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let temp = "\(dir)/.\(id).tmp"
        guard FileManager.default.createFile(atPath: temp, contents: data) else { return }
        if rename(temp, "\(dir)/\(id).json") != 0 { try? FileManager.default.removeItem(atPath: temp) }
    }

    static let reset = "\u{1b}[0m", dim = "\u{1b}[2m", cyan = "\u{1b}[36m", white = "\u{1b}[97m", sky = "\u{1b}[38;5;117m"

    /// 남은 비율에 따라 초록(여유)·노랑(주의)·빨강(부족)
    static func color(_ left: Int) -> String { left >= 50 ? "\u{1b}[32m" : left >= 20 ? "\u{1b}[33m" : "\u{1b}[31m" }

    static func effortColor(_ level: String) -> String {
        ["low": "\u{1b}[90m", "medium": "\u{1b}[34m", "high": "\u{1b}[35m", "xhigh": "\u{1b}[38;5;208m", "max": "\u{1b}[1;31m"][level] ?? ""
    }

    static func k(_ n: Double) -> String { n >= 1_000_000 ? String(format: "%.1fM", n / 1_000_000) : "\(Int(n / 1000))K" }

    static func render(_ root: [String: Any]) -> String {
        let sep = " \(dim)·\(reset) "
        var lines: [String] = []

        // 1줄: 저장소 · 워크트리 · PR (git 저장소 밖이면 생략)
        let workspace = root["workspace"] as? [String: Any]
        var location: [String] = []
        if let repo = (workspace?["repo"] as? [String: Any])?["name"] as? String, !repo.isEmpty { location.append("저장소 \(white)\(repo)\(reset)") }
        if let worktree = workspace?["git_worktree"] as? String, !worktree.isEmpty { location.append("워크트리 \(sky)\(worktree)\(reset)") }
        if let pr = number((root["pr"] as? [String: Any])?["number"]) { location.append("PR #\(Int(pr))") }
        if !location.isEmpty { lines.append(location.joined(separator: sep)) }

        // 2줄: 모델 · 추론 강도 │ 컨텍스트
        var line = "\(cyan)\((root["model"] as? [String: Any])?["display_name"] as? String ?? "?")\(reset)"
        if let level = (root["effort"] as? [String: Any])?["level"] as? String, !level.isEmpty {
            line += "\(sep)추론 강도 \(effortColor(level))\(level)\(reset)"
        }
        if let context = root["context_window"] as? [String: Any], let remaining = number(context["remaining_percentage"]) {
            let left = Int(remaining.rounded(.down))
            line += " \(dim)│\(reset) 컨텍스트 \(color(left))\(left)% 남음\(reset) "
                + "\(dim)(\(k(number(context["total_input_tokens"]) ?? 0))/\(k(number(context["context_window_size"]) ?? 0)))\(reset)"
        }
        lines.append(line)

        // 3줄: 사용 한도(Claude 구독으로 로그인했을 때만 온다)
        if let limits = root["rate_limits"] as? [String: Any] {
            var parts: [String] = []
            if let five = limits["five_hour"] as? [String: Any], let used = number(five["used_percentage"]) {
                let left = 100 - Int(used.rounded(.down))
                var text = "5시간 한도 \(color(left))\(left)% 남음\(reset)"
                if let at = number(five["resets_at"]), at > 0 { text += " \(dim)(\(clock(Date(timeIntervalSince1970: at))) 초기화)\(reset)" }
                parts.append(text)
            }
            if let week = limits["seven_day"] as? [String: Any], let used = number(week["used_percentage"]) {
                let left = 100 - Int(used.rounded(.down))
                parts.append("주간 한도 \(color(left))\(left)% 남음\(reset)")
            }
            if !parts.isEmpty { lines.append(parts.joined(separator: sep)) }
        }
        return lines.joined(separator: "\n")
    }
}

// MARK: - 시작

/// 이미 떠 있으면 false (로그인 자동 실행과 직접 실행이 겹쳐도 하나만 뜨게).
/// 다시 띄울 때는 앞의 것이 꺼지는 중일 수 있어 3초까지 기다린다.
func takeSingleInstanceLock() -> Bool {
    let dir = NSHomeDirectory() + "/.cache/ai-usage"
    try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    let fd = open(dir + "/pet.lock", O_CREAT | O_RDWR, 0o644)
    if fd < 0 { return true }
    for _ in 0..<30 {
        if flock(fd, LOCK_EX | LOCK_NB) == 0 { return true }  // fd 는 닫지 않는다(프로세스가 끝나면 풀린다)
        usleep(100_000)
    }
    close(fd)
    return false
}

@MainActor func printOnce() {
    let now = Date()
    let codex = CodexReader()
    codex.readLog()
    let fromLog = codex.fromLog
    codex.applyServer(CodexReader.fetchServerSync())
    print(describe("Claude", ClaudeReader().read(now: now), now: now))
    print(describe("ChatGPT · 기록", fromLog ?? ProviderSnapshot(message: "최근 기록 없음"), now: now))
    print(describe("ChatGPT · 서버", codex.fromServer ?? ProviderSnapshot(message: codex.serverError ?? "실패"), now: now))
    print(describe("ChatGPT · 표시", codex.current, now: now))
    let sessions = sortSessions(ClaudeSessionReader().read(now: now) + CodexSessionReader().read(now: now))
    print("세션 \(sessions.count)개")
    for s in sessions { print("  " + describeSession(s, now: now)) }
}

@MainActor func saveSnapshot(to path: String) {
    _ = NSApplication.shared
    renderingStill = true
    let model = UsageModel()
    model.claude = ClaudeReader().read(now: model.now)
    let codex = CodexReader()
    codex.readLog()
    codex.applyServer(CodexReader.fetchServerSync())
    model.codex = codex.current
    model.sessions = sortSessions(ClaudeSessionReader().read(now: model.now) + CodexSessionReader().read(now: model.now))
    let view = PetView(model: model)
        .background(Color(red: 0.12, green: 0.12, blue: 0.13))
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .environment(\.colorScheme, .dark)
    let renderer = ImageRenderer(content: view)
    renderer.scale = 2
    guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
          let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) else {
        print("그림을 만들지 못했습니다")
        return
    }
    do {
        try png.write(to: URL(fileURLWithPath: path))
        print(path)
    } catch {
        print("저장 실패: \(error)")
    }
}

@main
enum Main {
    @MainActor static func main() {
        signal(SIGPIPE, SIG_IGN)
        let args = CommandLine.arguments
        if args.contains("--statusline") {
            // 상태 표시줄이 실패해도 Claude Code 는 그대로 돌아야 하므로, 읽지 못하면 아무것도 내지 않는다
            let input = FileHandle.standardInput.readDataToEndOfFile()
            FileHandle.standardOutput.write(Data(StatusLine.handle(input).utf8))
            return
        }
        if args.contains("--print") {
            printOnce()
            return
        }
        if let i = args.firstIndex(of: "--snapshot"), args.indices.contains(i + 1) {
            saveSnapshot(to: args[i + 1])
            return
        }
        guard takeSingleInstanceLock() else { return }
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.regular)  // Dock 에 아이콘이 나오고, Dock 에서 열고 끈다
        withExtendedLifetime(delegate) { app.run() }
    }
}
