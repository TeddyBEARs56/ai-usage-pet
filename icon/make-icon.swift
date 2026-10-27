// 앱 아이콘 그리기 — 바깥 고리 Claude 주황, 안쪽 고리 ChatGPT 초록
//   swift make-icon.swift icon.png               맥용 1024px PNG (make-icons.sh 가 mac/AppIcon.icns 로 바꾼다)
//   swift make-icon.swift --windows AIUsage.ico  윈도우용 ICO (여백 없이 꽉 차게, 크기마다 따로 그림)
// 두 가지를 한 번에 다시 만들 때: zsh icon/make-icons.sh

import AppKit

/// 1024 격자 기준으로 그린 그림을 size 픽셀에 맞춰 그린다
func render(size: Int, windows: Bool) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: size * 4, bitsPerPixel: 32)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let transform = NSAffineTransform()
    transform.scale(by: CGFloat(size) / 1024)
    if windows {  // 윈도우 아이콘은 맥 격자의 여백이 없다
        transform.translateX(by: 512, yBy: 512)
        transform.scale(by: 1.16)
        transform.translateX(by: -512, yBy: -512)
    }
    transform.concat()

    // macOS 아이콘 격자: 1024 안에 824 둥근 사각형
    let tile = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 185, yRadius: 185)
    if !windows {
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 24
        shadow.shadowOffset = NSSize(width: 0, height: -10)
        shadow.set()
        NSColor(white: 0.1, alpha: 1).setFill()
        tile.fill()
        NSGraphicsContext.restoreGraphicsState()
    }
    NSGradient(starting: NSColor(srgbRed: 0.21, green: 0.21, blue: 0.24, alpha: 1),
               ending: NSColor(srgbRed: 0.07, green: 0.07, blue: 0.08, alpha: 1))!.draw(in: tile, angle: -90)

    let center = NSPoint(x: 512, y: 512)
    func ring(radius: CGFloat, fraction: CGFloat, color: NSColor) {
        let track = NSBezierPath()
        track.appendArc(withCenter: center, radius: radius, startAngle: 0, endAngle: 360)
        track.lineWidth = 80
        NSColor.white.withAlphaComponent(0.10).setStroke()
        track.stroke()
        let arc = NSBezierPath()
        arc.appendArc(withCenter: center, radius: radius, startAngle: 90, endAngle: 90 - 360 * fraction, clockwise: true)
        arc.lineWidth = 80
        arc.lineCapStyle = .round
        color.setStroke()
        arc.stroke()
    }
    ring(radius: 286, fraction: 0.72, color: NSColor(srgbRed: 0.851, green: 0.467, blue: 0.341, alpha: 1))
    ring(radius: 176, fraction: 0.55, color: NSColor(srgbRed: 0.063, green: 0.639, blue: 0.498, alpha: 1))
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

/// ICO: 크기마다 32비트 BGRA(알파 곱하지 않은 값, 아래 줄부터) + 빈 AND 마스크
func writeIco(sizes: [Int], to path: String) {
    var images: [Data] = []
    for size in sizes {
        let rep = render(size: size, windows: true)
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
    try! render(size: 1024, windows: false).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output))
}
