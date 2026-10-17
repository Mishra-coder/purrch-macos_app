// Renders the Purrch app icon: the mascot (Resources/Branding/Mascot.png) on a soft lavender macOS squircle tile.
// Usage: swift scripts/make-icon.swift <output.iconset> <mascot.png> [preview.png]
// Normally run by scripts/build-app.sh when Resources/Branding/Mascot.png changes.
import AppKit

func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: r, green: g, blue: b, alpha: a)
}

let mascot: CGImage = {
    let url = URL(fileURLWithPath: CommandLine.arguments[2])
    let source = CGImageSourceCreateWithURL(url as CFURL, nil)!
    return CGImageSourceCreateImageAtIndex(source, 0, nil)!
}()

func render(_ px: Int) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!.cgContext
    ctx.interpolationQuality = .high
    let s = CGFloat(px)
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x * s, y: y * s) }
    func gradient(_ colors: [CGColor]) -> CGGradient { CGGradient(colorsSpace: space, colors: colors as CFArray, locations: nil)! }

    // macOS icon grid: 824/1024 body with continuous-looking corners.
    let tileRect = CGRect(x: 0.098 * s, y: 0.098 * s, width: 0.804 * s, height: 0.804 * s)
    let tile = CGPath(roundedRect: tileRect, cornerWidth: 0.18 * s, cornerHeight: 0.18 * s, transform: nil)

    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -0.012 * s), blur: 0.03 * s, color: rgb(0.1, 0.05, 0.3, 0.3))
    ctx.addPath(tile); ctx.setFillColor(rgb(0.93, 0.92, 1.0)); ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(tile); ctx.clip()
    // Soft lavender-white so the white face and violet body both pop.
    ctx.drawLinearGradient(gradient([rgb(0.99, 0.98, 1.0), rgb(0.86, 0.84, 1.0)]),
                           start: pt(0.5, 0.902), end: pt(0.5, 0.098), options: [])
    ctx.drawRadialGradient(gradient([rgb(0.55, 0.45, 1.0, 0.35), rgb(0.55, 0.45, 1.0, 0)]),
                           startCenter: pt(0.5, 0.42), startRadius: 0, endCenter: pt(0.5, 0.42), endRadius: 0.42 * s, options: [])

    // Mascot, as large as the tile allows, with a soft violet shadow.
    let aspect = CGFloat(mascot.width) / CGFloat(mascot.height)
    let w = 0.74 * s, h = w / aspect
    let rect = CGRect(x: (s - w) / 2, y: (s - h) / 2 - 0.01 * s, width: w, height: h)
    ctx.setShadow(offset: CGSize(width: 0, height: -0.02 * s), blur: 0.04 * s, color: rgb(0.25, 0.12, 0.7, 0.45))
    ctx.draw(mascot, in: rect)
    ctx.restoreGState()

    // Hairline edge highlight.
    ctx.addPath(tile); ctx.setStrokeColor(rgb(1, 1, 1, 0.6)); ctx.setLineWidth(0.004 * s); ctx.strokePath()
    return rep
}

let out = CommandLine.arguments[1]
try? FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)
for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    try! render(px).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/icon_\(name).png"))
}
if CommandLine.arguments.count > 3 {
    try! render(1024).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[3]))
}
