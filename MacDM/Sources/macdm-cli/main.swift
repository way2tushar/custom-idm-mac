import DownloadEngine
import Foundation

// Usage: macdm-cli <url> [--connections N] [--output DIR] [--id UUID] [--variant BANDWIDTH]
// Passing the same --id again resumes an interrupted download.

var arguments = Array(CommandLine.arguments.dropFirst())
func option(_ name: String) -> String? {
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
    defer { arguments.removeSubrange(index...(index + 1)) }
    return arguments[index + 1]
}

let connections = option("--connections").flatMap(Int.init) ?? 8
let output = URL(fileURLWithPath: option("--output") ?? FileManager.default.currentDirectoryPath, isDirectory: true)
let id = option("--id").flatMap(UUID.init(uuidString:)) ?? UUID()
let variant = option("--variant").flatMap(Int.init)
guard let target = arguments.first.flatMap(URL.init(string:)) else {
    FileHandle.standardError.write(Data("usage: macdm-cli <url> [--connections N] [--output DIR] [--id UUID]\n".utf8))
    exit(2)
}

print("id \(id.uuidString)")
let request = DownloadRequest(url: target, variantBandwidth: variant)
let download: DownloadJob = request.isStream
    ? StreamDownload(id: id, request: request, directory: output, maxConnections: connections)
    : SegmentedDownload(id: id, request: request, directory: output, maxConnections: connections)
download.onProgress = { snapshot in
    let percent = snapshot.fraction.map { String(format: "%5.1f%%", $0 * 100) } ?? "  ?  "
    let mbps = snapshot.bytesPerSecond / 1_048_576
    print(String(format: "%@ %@ %.2f MB/s  conns %d  segs %d  parts %d/%d  %@",
                 snapshot.status.rawValue, percent, mbps, snapshot.activeConnections,
                 snapshot.segments.count, snapshot.partsDone, snapshot.partsTotal, snapshot.filename ?? ""))
    switch snapshot.status {
    case .completed:
        print("saved \(snapshot.fileURL?.path ?? "?")")
        exit(0)
    case .failed:
        print("error \(snapshot.error ?? "unknown")")
        exit(1)
    default:
        break
    }
}
signal(SIGINT, SIG_IGN)
let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
interrupt.setEventHandler {
    download.pause()
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { exit(130) }
}
interrupt.resume()
download.start()
dispatchMain()
