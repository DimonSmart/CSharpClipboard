import AppKit
let image = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024,
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: image)
NSColor(calibratedRed: 0.14, green: 0.33, blue: 0.73, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 12, y: 12, width: 1000, height: 1000), xRadius: 210, yRadius: 210).fill()
NSColor.white.setFill()
NSBezierPath(roundedRect: NSRect(x: 256, y: 170, width: 512, height: 684), xRadius: 45, yRadius: 45).fill()
NSColor(calibratedRed: 0.14, green: 0.33, blue: 0.73, alpha: 1).setFill()
for y in [330, 480, 630] { NSBezierPath(roundedRect: NSRect(x: 340, y: CGFloat(y), width: 344, height: 40), xRadius: 12, yRadius: 12).fill() }
NSGraphicsContext.restoreGraphicsState()
try image.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
