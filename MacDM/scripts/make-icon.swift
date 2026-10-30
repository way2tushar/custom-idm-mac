// Draws the MacDM icon: a download arrow above four segmented progress bars
// (one per connection) on a blue rounded square. Writes a 1024px PNG.
// Usage: swift scripts/make-icon.swift <output.png>
import AppKit

let size: CGFloat = 1024
let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.png"

let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
    guard let context = NSGraphicsContext.current?.cgContext else { return false }

    // macOS icon grid: the tile is inset ~10% with a large corner radius and a soft drop shadow.
    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = CGPath(roundedRect: tile, cornerWidth: 185, cornerHeight: 185, transform: nil)
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: NSColor.black.withAlphaComponent(0.35).cgColor)
    context.addPath(tilePath)
    context.setFillColor(NSColor.black.cgColor)
    context.fillPath()
    context.restoreGState()

    context.saveGState()
    context.addPath(tilePath)
    context.clip()
    let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
        NSColor(red: 0.20, green: 0.62, blue: 1.00, alpha: 1).cgColor,
        NSColor(red: 0.05, green: 0.30, blue: 0.86, alpha: 1).cgColor,
    ] as CFArray, locations: [0, 1])!
    context.drawLinearGradient(gradient, start: CGPoint(x: 0, y: tile.maxY), end: CGPoint(x: 0, y: tile.minY), options: [])
    // Gloss on the upper half.
    context.setFillColor(NSColor.white.withAlphaComponent(0.08).cgColor)
    context.fillEllipse(in: CGRect(x: -100, y: 560, width: 1224, height: 700))
    context.restoreGState()

    // Four segmented bars: each connection's filled part, at a different progress.
    let barX: CGFloat = 250, barWidth: CGFloat = 524, barHeight: CGFloat = 34
    let fills: [CGFloat] = [0.92, 0.64, 0.80, 0.48]
    for (index, fill) in fills.enumerated() {
        let y = 196 + CGFloat(index) * 52
        let track = CGPath(roundedRect: CGRect(x: barX, y: y, width: barWidth, height: barHeight),
                           cornerWidth: 17, cornerHeight: 17, transform: nil)
        context.addPath(track)
        context.setFillColor(NSColor.white.withAlphaComponent(0.22).cgColor)
        context.fillPath()
        let done = CGPath(roundedRect: CGRect(x: barX, y: y, width: barWidth * fill, height: barHeight),
                          cornerWidth: 17, cornerHeight: 17, transform: nil)
        context.addPath(done)
        context.setFillColor(NSColor(red: 0.55, green: 1.0, blue: 0.75, alpha: 1).cgColor)
        context.fillPath()
    }

    // Download arrow.
    let arrow = CGMutablePath()
    arrow.move(to: CGPoint(x: 440, y: 820))
    arrow.addLine(to: CGPoint(x: 584, y: 820))
    arrow.addLine(to: CGPoint(x: 584, y: 600))
    arrow.addLine(to: CGPoint(x: 690, y: 600))
    arrow.addLine(to: CGPoint(x: 512, y: 420))
    arrow.addLine(to: CGPoint(x: 334, y: 600))
    arrow.addLine(to: CGPoint(x: 440, y: 600))
    arrow.closeSubpath()
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -6), blur: 14, color: NSColor.black.withAlphaComponent(0.25).cgColor)
    context.addPath(arrow)
    context.setFillColor(NSColor.white.cgColor)
    context.fillPath()
    context.restoreGState()
    return true
}

guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:]) else { fatalError("could not render icon") }
try png.write(to: URL(fileURLWithPath: output))
