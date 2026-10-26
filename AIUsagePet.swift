// AI 사용량 창 — Claude·ChatGPT(Codex) 사용 한도를 화면 위에 계속 띄워 두는 작은 창
//
// 값의 출처
//   Claude  : Claude Code 상태 표시줄(~/.claude/statusline.sh)이 세션마다 남기는 ~/.cache/ai-usage/claude/<세션>.json
//   ChatGPT : `codex app-server` 의 account/rateLimits/read(5분마다) + ~/.codex/sessions 기록에 찍히는 최신 한도
//
// 실행 옵션: --print (값만 출력), --snapshot <png> (창 모양을 그림으로 저장)

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

    static func codexExecutable() -> String? {
        let home = NSHomeDirectory()
        return [home + "/.local/bin/codex", "/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                home + "/.codex/packages/standalone/current/bin/codex"]
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// `codex app-server` 를 잠깐 띄워 account/rateLimits/read 로 지금 한도를 묻는다(인증은 codex 가 알아서 한다)
    static func fetchServerSync() -> (snapshot: ProviderSnapshot?, error: String?) {
        guard let exe = codexExecutable() else { return (nil, "codex 명령을 찾지 못했습니다") }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: exe)
        process.arguments = ["app-server"]
        process.currentDirectoryURL = URL(fileURLWithPath: NSHomeDirectory())
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

final class UsageModel: ObservableObject {
    @Published var claude = ProviderSnapshot()
    @Published var codex = ProviderSnapshot()
    @Published var now = Date()
}

struct PetView: View {
    @ObservedObject var model: UsageModel

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            ProviderSection(name: "Claude", detail: nil,
                            color: Color(red: 0.851, green: 0.467, blue: 0.341), snap: model.claude, now: model.now)
            Rectangle().fill(Color.white.opacity(0.08)).frame(height: 1)
            ProviderSection(name: "ChatGPT", detail: ["Codex", model.codex.plan].compactMap { $0 }.joined(separator: " · "),
                            color: Color(red: 0.063, green: 0.639, blue: 0.498), snap: model.codex, now: model.now)
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
    private var panel: NSPanel!
    private var container: ContainerView!
    private var hosting: NSHostingView<PetView>!

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        buildPanel()
        refreshAll()
        Timer.scheduledTimer(timeInterval: 15, target: self, selector: #selector(refreshLocal), userInfo: nil, repeats: true)
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
        afterUpdate()
    }

    @objc private func refreshServer() {
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

    /// 내용 높이가 바뀌면 왼쪽 위 모서리를 그대로 두고 창 크기를 맞춘다
    private func fitPanel() {
        let size = hosting.fittingSize
        guard size.width > 0, size.height > 0, size != panel.frame.size else { return }
        let topLeft = NSPoint(x: panel.frame.minX, y: panel.frame.maxY)
        panel.setContentSize(size)
        panel.setFrameTopLeftPoint(topLeft)
        panel.invalidateShadow()
    }

    private func placePanel(reset: Bool) {
        if !reset, let saved = UserDefaults.standard.array(forKey: "topLeft") as? [Double], saved.count == 2 {
            let point = NSPoint(x: saved[0], y: saved[1])
            if NSScreen.screens.contains(where: { $0.visibleFrame.contains(NSPoint(x: point.x + 10, y: point.y - 10)) }) {
                panel.setFrameTopLeftPoint(point)
                return
            }
        }
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        panel.setFrameTopLeftPoint(NSPoint(x: visible.maxX - panel.frame.width - 16, y: visible.maxY - 16))
    }

    @objc private func savePosition() {
        UserDefaults.standard.set([panel.frame.minX, panel.frame.maxY], forKey: "topLeft")
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
        add("Claude (Claude Code 상태 표시줄)", model.claude)
        add("ChatGPT (Codex 한도)", model.codex)
        lines.append("Codex 서버 확인 \(codex.serverCheckedAt.map(clock) ?? "전")\(codex.serverError.map { " — \($0)" } ?? "")")
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

    // MARK: Dock 앱

    /// 앱이 앞에 있을 때 위쪽 메뉴 막대에 나오는 메뉴(⌘R 새로고침, ⌘Q 종료)
    private func buildMainMenu() {
        let appMenu = NSMenu()
        appMenu.addItem(menuItem("지금 새로고침", #selector(refreshAll), key: "r"))
        appMenu.addItem(menuItem("위치 초기화", #selector(resetPosition)))
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
        if menuItem.action == #selector(toggleLoginItem) {
            menuItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        }
        return true
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
}

@MainActor func saveSnapshot(to path: String) {
    _ = NSApplication.shared
    let model = UsageModel()
    model.claude = ClaudeReader().read(now: model.now)
    let codex = CodexReader()
    codex.readLog()
    codex.applyServer(CodexReader.fetchServerSync())
    model.codex = codex.current
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
