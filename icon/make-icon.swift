// 앱 아이콘 그리기 — 사선으로 나눈 바탕(위 Claude 주황 · 아래 ChatGPT 초록) 위에 유리로 된 % 기호
//   swift make-icon.swift icon.png               맥용 1024px PNG (make-icons.sh 가 mac/AppIcon.icns 로 바꾼다)
//   swift make-icon.swift --windows AIUsage.ico  윈도우용 ICO (여백 없이 꽉 차게)
// 두 가지를 한 번에 다시 만들 때: zsh icon/make-icons.sh
// 그림자·빛 번짐은 픽셀 단위라 작은 크기로 바로 그리면 커져 버린다 → 늘 1024 로 그린 뒤 줄인다.

import AppKit

func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> NSColor { NSColor(srgbRed: r, green: g, blue: b, alpha: a) }

let orangeHi = rgb(1.0, 0.70, 0.50), orangeLo = rgb(0.84, 0.39, 0.26)
let greenHi = rgb(0.36, 0.90, 0.70), greenLo = rgb(0.02, 0.53, 0.41)

// macOS 아이콘 격자: 1024 안에 824 둥근 사각형
let tileRect = NSRect(x: 100, y: 100, width: 824, height: 824)
let tile = NSBezierPath(roundedRect: tileRect, xRadius: 185, yRadius: 185)

// % 기호: 사선 (372,270)→(652,754), 위 점·아래 점
let slashFrom = NSPoint(x: 372, y: 270), slashTo = NSPoint(x: 652, y: 754)
let topDot = NSPoint(x: 352, y: 652), bottomDot = NSPoint(x: 672, y: 372)
let dotRadius: CGFloat = 104, slashWidth: CGFloat = 92

/// 유리의 흰빛 세기(시안 6차 4번)
let glassWhite: CGFloat = 0.30

extension NSBezierPath {
    func strokedPath() -> NSBezierPath {
        NSBezierPath(cgPath: cgPath.copy(strokingWithWidth: lineWidth, lineCap: lineCapStyle == .round ? .round : .butt, lineJoin: .round, miterLimit: 10))
    }
}

func withShadow(_ color: NSColor, blur: CGFloat, dy: CGFloat, _ draw: () -> Void) {
    NSGraphicsContext.saveGraphicsState()
    let s = NSShadow(); s.shadowColor = color; s.shadowBlurRadius = blur; s.shadowOffset = NSSize(width: 0, height: -dy); s.set()
    draw()
    NSGraphicsContext.restoreGraphicsState()
}

func circle(_ c: NSPoint, _ r: CGFloat) -> NSBezierPath { NSBezierPath(ovalIn: NSRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)) }

/// 바탕: 사선을 늘인 선의 왼쪽 위는 주황, 오른쪽 아래는 초록
func background() {
    NSGradient(starting: greenHi, ending: greenLo)!.draw(in: tile, angle: -60)
    let k = (slashTo.y - slashFrom.y) / (slashTo.x - slashFrom.x)
    let upper = NSBezierPath()
    upper.move(to: NSPoint(x: 0, y: 1024))
    upper.line(to: NSPoint(x: 0, y: slashFrom.y - slashFrom.x * k))
    upper.line(to: NSPoint(x: slashFrom.x + (1024 - slashFrom.y) / k, y: 1024))
    upper.close()
    NSGraphicsContext.saveGraphicsState()
    upper.addClip()
    NSGradient(starting: orangeHi, ending: orangeLo)!.draw(in: tile, angle: -60)
    NSGraphicsContext.restoreGraphicsState()
}

/// 유리 % — 바깥 그림자 → 안쪽 흰빛 → 안쪽 테두리 빛(왼쪽 위 밝게) → 가는 바깥선 → 반사광
func glassPercent() {
    let slash = NSBezierPath()
    slash.move(to: slashFrom); slash.line(to: slashTo)
    slash.lineWidth = slashWidth; slash.lineCapStyle = .round
    let shapes = [slash.strokedPath(), circle(topDot, dotRadius), circle(bottomDot, dotRadius)]
    let union = NSBezierPath(); shapes.forEach { union.append($0) }

    NSGraphicsContext.saveGraphicsState()
    let outside = NSBezierPath(rect: NSRect(x: -200, y: -200, width: 1424, height: 1424)); outside.append(union); outside.windingRule = .evenOdd
    outside.addClip()
    withShadow(NSColor.black.withAlphaComponent(0.25), blur: 30, dy: 12) { NSColor.black.setFill(); union.fill() }
    NSGraphicsContext.restoreGraphicsState()

    for p in shapes {
        NSGraphicsContext.saveGraphicsState()
        p.addClip()
        NSGradient(colors: [NSColor.white.withAlphaComponent(glassWhite * 1.15), NSColor.white.withAlphaComponent(glassWhite * 0.75)])!
            .draw(in: p.bounds.insetBy(dx: -2, dy: -2), angle: -90)
        let ring = p.copy() as! NSBezierPath
        ring.lineWidth = 16
        NSGradient(colors: [NSColor.white, NSColor.white.withAlphaComponent(0.15), NSColor.white.withAlphaComponent(0.55)], atLocations: [0, 0.6, 1], colorSpace: .sRGB)!
            .draw(in: ring.strokedPath(), angle: -50)
        NSGraphicsContext.restoreGraphicsState()
        p.lineWidth = 2.5
        NSColor.white.withAlphaComponent(0.6).setStroke()
        p.stroke()
    }

    // 점의 반사광(왼쪽 위 호)
    for c in [topDot, bottomDot] {
        let arc = NSBezierPath()
        arc.appendArc(withCenter: c, radius: dotRadius * 0.72, startAngle: 105, endAngle: 160)
        arc.lineWidth = dotRadius * 0.13; arc.lineCapStyle = .round
        NSColor.white.withAlphaComponent(0.95).setStroke(); arc.stroke()
    }
    // 사선의 반사광(왼쪽으로 조금 비켜 위쪽 절반)
    let dx = slashTo.x - slashFrom.x, dy = slashTo.y - slashFrom.y, len = (dx * dx + dy * dy).squareRoot()
    let nx = -dy / len * 20, ny = dx / len * 20
    let shine = NSBezierPath()
    shine.move(to: NSPoint(x: slashFrom.x + dx * 0.45 + nx, y: slashFrom.y + dy * 0.45 + ny))
    shine.line(to: NSPoint(x: slashFrom.x + dx * 0.86 + nx, y: slashFrom.y + dy * 0.86 + ny))
    shine.lineWidth = 12; shine.lineCapStyle = .round
    NSColor.white.withAlphaComponent(0.8).setStroke(); shine.stroke()
}

/// 바탕 위쪽 가장자리 빛
func rim() {
    let inner = NSBezierPath(roundedRect: tileRect.insetBy(dx: 3, dy: 3), xRadius: 182, yRadius: 182)
    inner.lineWidth = 6
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.35), NSColor.white.withAlphaComponent(0)], atLocations: [0, 0.5], colorSpace: .sRGB)!
        .draw(in: inner.strokedPath(), angle: -90)
}

func bitmap(_ size: Int) -> NSBitmapImageRep {
    NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                     isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32)!
}

/// 1024 로 그린다. 윈도우 아이콘은 맥 격자의 여백과 바닥 그림자 없이 꽉 차게.
func render1024(windows: Bool) -> NSBitmapImageRep {
    let rep = bitmap(1024)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    if windows {
        let t = NSAffineTransform()
        t.translateX(by: 512, yBy: 512); t.scale(by: 1.16); t.translateX(by: -512, yBy: -512)
        t.concat()
    } else {
        withShadow(NSColor.black.withAlphaComponent(0.30), blur: 24, dy: 10) { NSColor(white: 0.5, alpha: 1).setFill(); tile.fill() }
    }
    NSGraphicsContext.saveGraphicsState()
    tile.addClip()
    background()
    glassPercent()
    rim()
    NSGraphicsContext.restoreGraphicsState()
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

/// 큰 그림을 size 로 줄인다(고품질 보간)
func scaled(_ source: NSBitmapImageRep, to size: Int) -> NSBitmapImageRep {
    let rep = bitmap(size)
    NSGraphicsContext.saveGraphicsState()
    let context = NSGraphicsContext(bitmapImageRep: rep)!
    context.imageInterpolation = .high
    NSGraphicsContext.current = context
    source.draw(in: NSRect(x: 0, y: 0, width: size, height: size), from: .zero, operation: .copy, fraction: 1, respectFlipped: false, hints: nil)
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

/// ICO: 크기마다 32비트 BGRA(알파 곱하지 않은 값, 아래 줄부터) + 빈 AND 마스크
func writeIco(sizes: [Int], to path: String) {
    let big = render1024(windows: true)
    var images: [Data] = []
    for size in sizes {
        let rep = scaled(big, to: size)
        let pixels = rep.bitmapData!
        var dib = Data()
        func le32(_ v: Int) { var x = UInt32(truncatingIfNeeded: v).littleEndian; dib.append(Data(bytes: &x, count: 4)) }
        func le16(_ v: Int) { var x = UInt16(truncatingIfNeeded: v).littleEndian; dib.append(Data(bytes: &x, count: 2)) }
        let maskRow = ((size + 31) / 32) * 4
        le32(40); le32(size); le32(size * 2); le16(1); le16(32); le32(0)
        le32(size * size * 4 + maskRow * size); le32(0); le32(0); le32(0); le32(0)
        for y in stride(from: size - 1, through: 0, by: -1) {
            for x in 0..<size {
                let i = (y * size + x) * 4
                let a = Int(pixels[i + 3])
                func straight(_ c: UInt8) -> UInt8 { a == 0 ? 0 : UInt8(min(255, (Int(c) * 255 + a / 2) / a)) }
                dib.append(contentsOf: [straight(pixels[i + 2]), straight(pixels[i + 1]), straight(pixels[i]), UInt8(a)])
            }
        }
        dib.append(Data(count: maskRow * size))
        images.append(dib)
    }
    var ico = Data()
    func le16(_ v: Int) { var x = UInt16(truncatingIfNeeded: v).littleEndian; ico.append(Data(bytes: &x, count: 2)) }
    func le32(_ v: Int) { var x = UInt32(truncatingIfNeeded: v).littleEndian; ico.append(Data(bytes: &x, count: 4)) }
    le16(0); le16(1); le16(sizes.count)
    var offset = 6 + 16 * sizes.count
    for (size, image) in zip(sizes, images) {
        ico.append(contentsOf: [UInt8(size >= 256 ? 0 : size), UInt8(size >= 256 ? 0 : size), 0, 0])
        le16(1); le16(32); le32(image.count); le32(offset)
        offset += image.count
    }
    images.forEach { ico.append($0) }
    try! ico.write(to: URL(fileURLWithPath: path))
}

let args = Array(CommandLine.arguments.dropFirst())
if args.first == "--windows" {
    writeIco(sizes: [16, 20, 24, 32, 40, 48, 64, 128, 256], to: args.count > 1 ? args[1] : "AIUsage.ico")
} else {
    let output = args.first ?? "icon.png"
    try! render1024(windows: false).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output))
}
