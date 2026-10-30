import AppKit
import DownloadEngine
import SwiftUI

func format(bytes: Int64) -> String {
    ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
}

func format(seconds: Double?) -> String {
    guard let seconds, seconds.isFinite else { return "" }
    let formatter = DateComponentsFormatter()
    formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute, .second] : [.minute, .second]
    formatter.unitsStyle = .abbreviated
    formatter.zeroFormattingBehavior = .dropLeading
    return formatter.string(from: seconds) ?? ""
}

func format(rate: Double) -> String {
    rate > 0 ? "\(format(bytes: Int64(rate)))/s" : ""
}

func sizeText(_ snapshot: ProgressSnapshot) -> String {
    if let total = snapshot.totalBytes { return format(bytes: total) }
    // Streams have no size until finished; show what has arrived so far.
    return snapshot.downloadedBytes > 0 ? format(bytes: snapshot.downloadedBytes) : ""
}

func percentText(_ snapshot: ProgressSnapshot) -> String {
    guard let fraction = snapshot.fraction else { return "" }
    return String(format: "%.2f%%", fraction * 100)
}

/// Status column text.
@MainActor
func statusText(_ item: DownloadItem) -> String {
    let snapshot = item.snapshot
    switch snapshot.status {
    case .completed: return "Complete"
    case .queued: return item.inQueue ? "Queued" : "Starting…"
    case .probing: return "Connecting…"
    case .downloading: return percentText(snapshot)
    case .paused: return snapshot.downloadedBytes > 0 ? percentText(snapshot) : (item.inQueue ? "Queued" : "Stopped")
    case .failed: return "Error"
    }
}

/// Each connection's range drawn at its position in the file.
struct SegmentBar: View {
    let snapshot: ProgressSnapshot

    var body: some View {
        Canvas { context, size in
            let track = Path(CGRect(origin: .zero, size: size))
            context.fill(track, with: .color(Color(nsColor: .quaternaryLabelColor)))
            if snapshot.status == .completed {
                context.fill(track, with: .color(.accentColor))
                return
            }
            guard let total = snapshot.totalBytes, total > 0, !snapshot.segments.isEmpty else {
                if let fraction = snapshot.fraction {
                    context.fill(Path(CGRect(x: 0, y: 0, width: size.width * fraction, height: size.height)), with: .color(.accentColor))
                }
                return
            }
            let scale = size.width / CGFloat(total)
            for segment in snapshot.segments {
                let rect = CGRect(x: CGFloat(segment.start) * scale, y: 0,
                                  width: max(1, CGFloat(segment.downloaded) * scale), height: size.height)
                context.fill(Path(rect), with: .color(.accentColor))
                // Mark where each connection started.
                context.fill(Path(CGRect(x: CGFloat(segment.start) * scale, y: 0, width: 1, height: size.height)),
                             with: .color(.red.opacity(0.8)))
            }
        }
        .overlay(Rectangle().stroke(Color(nsColor: .separatorColor)))
    }
}

func fileIcon(for name: String) -> NSImage {
    let ext = (name as NSString).pathExtension
    return NSWorkspace.shared.icon(for: .init(filenameExtension: ext.isEmpty ? "bin" : ext) ?? .data)
}
