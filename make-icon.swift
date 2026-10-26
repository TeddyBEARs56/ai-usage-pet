// 앱 아이콘(1024px PNG) 그리기 — 바깥 고리 Claude 주황, 안쪽 고리 ChatGPT 초록
//   swift make-icon.swift icon.png   (build.sh 가 AppIcon.icns 로 바꾼다)

import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// macOS 아이콘 격자: 1024 안에 824 둥근 사각형
let tile = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 185, yRadius: 185)
NSGraphicsContext.saveGraphicsState()
let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
shadow.shadowBlurRadius = 24
shadow.shadowOffset = NSSize(width: 0, height: -10)
shadow.set()
NSColor(white: 0.1, alpha: 1).setFill()
tile.fill()
NSGraphicsContext.restoreGraphicsState()
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
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output))
