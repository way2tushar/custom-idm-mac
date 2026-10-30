import Foundation

/// What to download, plus the browser context needed to fetch it (cookies, referrer, user agent).
public struct DownloadRequest: Codable, Sendable, Hashable {
    public var url: URL
    public var headers: [String: String]
    public var filename: String?
    /// nil or `.file` for an ordinary file; `.hls` / `.dash` for a streaming playlist or manifest.
    public var kind: Kind?
    /// For a multi-quality HLS stream, the BANDWIDTH of the chosen quality; nil picks the best.
    public var variantBandwidth: Int?

    public enum Kind: String, Codable, Sendable { case file, hls, dash }

    public init(url: URL, headers: [String: String] = [:], filename: String? = nil, kind: Kind? = nil,
                variantBandwidth: Int? = nil) {
        self.url = url
        self.headers = headers
        self.filename = filename
        self.kind = kind
        self.variantBandwidth = variantBandwidth
    }

    /// Streams are recognised by an explicit kind or a `.m3u8` / `.mpd` path.
    public var isStream: Bool {
        kind == .hls || kind == .dash || ["m3u8", "mpd"].contains(url.pathExtension.lowercased())
    }
}

/// What the app needs from any download job, whether a file or a stream.
public protocol DownloadJob: AnyObject, Sendable {
    var maxConnections: Int { get set }
    var replaceExisting: Bool { get set }
    var onProgress: (@Sendable (ProgressSnapshot) -> Void)? { get set }
    func start()
    func pause()
    func cancel()
    func setSpeedLimit(_ bytesPerSecond: Int64?)
}

public enum DownloadStatus: String, Codable, Sendable {
    case queued, probing, downloading, paused, completed, failed
}

public struct SegmentSnapshot: Sendable, Hashable {
    public let start: Int64
    public let end: Int64
    public let downloaded: Int64
    public let active: Bool
    public let info: String
}

/// Manual HTTP/HTTPS proxy applied to new connections.
public struct ProxySettings: Sendable, Equatable {
    public var host: String
    public var port: Int

    public init(host: String, port: Int) {
        self.host = host
        self.port = port
    }

    /// Set from the app's options; read whenever a connection is created.
    nonisolated(unsafe) public static var current: ProxySettings?
}

public struct ProgressSnapshot: Sendable {
    public var status: DownloadStatus = .queued
    public var filename: String?
    public var totalBytes: Int64?
    public var downloadedBytes: Int64 = 0
    public var bytesPerSecond: Double = 0
    public var segments: [SegmentSnapshot] = []
    public var activeConnections = 0
    public var resumable = false
    public var fileURL: URL?
    public var error: String?
    /// Stream downloads count progress in media segments, since the byte size is unknown up front.
    public var partsDone = 0
    public var partsTotal = 0

    public init() {}

    public var fraction: Double? {
        if let totalBytes, totalBytes > 0 { return min(1, Double(downloadedBytes) / Double(totalBytes)) }
        if partsTotal > 0 { return min(1, Double(partsDone) / Double(partsTotal)) }
        return nil
    }
}

public enum DownloadError: LocalizedError {
    case badStatus(Int)
    case rangeIgnored
    case tooManyRetries(String)

    public var errorDescription: String? {
        switch self {
        case .badStatus(let code): "Server responded with HTTP \(code)"
        case .rangeIgnored: "Server stopped honoring byte ranges"
        case .tooManyRetries(let reason): "Gave up after repeated failures: \(reason)"
        }
    }
}

/// On-disk record that lets a partial download continue after pause, quit or crash.
struct ResumeState: Codable {
    struct Segment: Codable {
        var start: Int64
        var end: Int64
        var downloaded: Int64
    }

    var url: URL
    var totalBytes: Int64
    var etag: String?
    var lastModified: String?
    var filename: String
    var segments: [Segment]
}
