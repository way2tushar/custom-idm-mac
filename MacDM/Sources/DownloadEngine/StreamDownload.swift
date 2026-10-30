import CommonCrypto
import Foundation

/// One track's media segments, from an HLS media playlist or a DASH representation.
struct MediaPlaylist {
    struct Key: Hashable {
        var uri: URL
        var iv: Data?
    }

    struct Segment {
        var url: URL
        var key: Key?
        var sequence: Int
        /// Inclusive byte range within `url`, for byte-range addressed media.
        var range: ClosedRange<Int64>?
    }

    var segments: [Segment] = []
    var initSection: URL?
    var initRange: ClosedRange<Int64>?
    var isLive = true
    /// File extension of the joined track: "ts", "mp4" or "webm".
    var container: String?
}

enum HLSError: LocalizedError {
    case notAPlaylist
    case unsupportedEncryption(String)
    case empty
    case live

    var errorDescription: String? {
        switch self {
        case .notAPlaylist: "The address is not an HLS or DASH stream"
        case .unsupportedEncryption(let method): "This stream is protected (\(method)) and can't be downloaded"
        case .empty: "The playlist has no media segments"
        case .live: "Live streams can't be downloaded"
        }
    }
}

enum Playlist {
    /// Attributes like `BANDWIDTH=1280000,URI="key.bin",IV=0x...`; quoted values may contain commas.
    static func attributes(_ text: Substring) -> [String: String] {
        var result: [String: String] = [:]
        var rest = text[...]
        while !rest.isEmpty {
            guard let equals = rest.firstIndex(of: "=") else { break }
            let name = rest[..<equals].trimmingCharacters(in: .whitespaces)
            rest = rest[rest.index(after: equals)...]
            var value: Substring
            if rest.first == "\"" {
                rest = rest.dropFirst()
                let close = rest.firstIndex(of: "\"") ?? rest.endIndex
                value = rest[..<close]
                rest = close < rest.endIndex ? rest[rest.index(after: close)...] : ""
                if rest.first == "," { rest = rest.dropFirst() }
            } else {
                let comma = rest.firstIndex(of: ",") ?? rest.endIndex
                value = rest[..<comma]
                rest = comma < rest.endIndex ? rest[rest.index(after: comma)...] : ""
            }
            result[name] = String(value)
        }
        return result
    }

    struct Variant {
        var url: URL
        var bandwidth: Int
        var audioGroup: String?
    }

    struct Master {
        var variants: [Variant]
        /// Separate audio tracks by GROUP-ID, default track first.
        var audio: [String: [URL]]

        /// The requested quality (matched by bandwidth), else the highest.
        func pick(bandwidth: Int?) -> Variant? {
            if let bandwidth, let exact = variants.first(where: { $0.bandwidth == bandwidth }) { return exact }
            return variants.max { $0.bandwidth < $1.bandwidth }
        }

        func audioURL(for variant: Variant) -> URL? {
            variant.audioGroup.flatMap { audio[$0]?.first }
        }
    }

    /// Parses a master playlist; nil for a media playlist.
    static func master(_ text: String, base: URL) -> Master? {
        var variants: [Variant] = []
        var audio: [String: [URL]] = [:]
        var pending: [String: String]?
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("#EXT-X-STREAM-INF:") {
                pending = attributes(line.dropFirst("#EXT-X-STREAM-INF:".count)[...])
            } else if line.hasPrefix("#EXT-X-MEDIA:") {
                let attrs = attributes(line.dropFirst("#EXT-X-MEDIA:".count)[...])
                guard attrs["TYPE"] == "AUDIO", let group = attrs["GROUP-ID"],
                      let uri = attrs["URI"], let url = URL(string: uri, relativeTo: base)?.absoluteURL else { continue }
                if attrs["DEFAULT"] == "YES" { audio[group, default: []].insert(url, at: 0) } else { audio[group, default: []].append(url) }
            } else if let attrs = pending, !line.isEmpty, !line.hasPrefix("#") {
                if let url = URL(string: line, relativeTo: base)?.absoluteURL {
                    let bandwidth = Int(attrs["BANDWIDTH"] ?? attrs["AVERAGE-BANDWIDTH"] ?? "0") ?? 0
                    variants.append(Variant(url: url, bandwidth: bandwidth, audioGroup: attrs["AUDIO"]))
                }
                pending = nil
            }
        }
        return variants.isEmpty ? nil : Master(variants: variants, audio: audio)
    }

    static func media(_ text: String, base: URL) throws -> MediaPlaylist {
        guard text.hasPrefix("#EXTM3U") else { throw HLSError.notAPlaylist }
        var playlist = MediaPlaylist()
        var sequence = 0
        var key: MediaPlaylist.Key?
        var pendingRange: ClosedRange<Int64>?
        var nextOffset: Int64 = 0
        /// "length[@offset]"; without an offset the range follows the previous one.
        func byteRange(_ spec: Substring) -> ClosedRange<Int64>? {
            let parts = spec.split(separator: "@")
            guard let length = Int64(parts[0]), length > 0 else { return nil }
            let start = parts.count > 1 ? Int64(parts[1]) ?? nextOffset : nextOffset
            nextOffset = start + length
            return start...(start + length - 1)
        }
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("#EXT-X-BYTERANGE:") {
                pendingRange = byteRange(line.dropFirst("#EXT-X-BYTERANGE:".count)[...])
            } else if line.hasPrefix("#EXT-X-MEDIA-SEQUENCE:") {
                sequence = Int(line.dropFirst("#EXT-X-MEDIA-SEQUENCE:".count)) ?? 0
            } else if line.hasPrefix("#EXT-X-KEY:") {
                let attrs = attributes(line.dropFirst("#EXT-X-KEY:".count)[...])
                switch attrs["METHOD"] ?? "NONE" {
                case "NONE":
                    key = nil
                case "AES-128":
                    guard let uri = attrs["URI"], let url = URL(string: uri, relativeTo: base)?.absoluteURL else {
                        throw HLSError.unsupportedEncryption("AES-128 without a key address")
                    }
                    key = .init(uri: url, iv: attrs["IV"].flatMap(hexData))
                case let method:
                    throw HLSError.unsupportedEncryption(method)
                }
            } else if line.hasPrefix("#EXT-X-MAP:") {
                let attrs = attributes(line.dropFirst("#EXT-X-MAP:".count)[...])
                playlist.initSection = attrs["URI"].flatMap { URL(string: $0, relativeTo: base)?.absoluteURL }
                playlist.initRange = attrs["BYTERANGE"].flatMap { byteRange($0[...]) }
                nextOffset = 0
            } else if line.hasPrefix("#EXT-X-ENDLIST") {
                playlist.isLive = false
            } else if !line.hasPrefix("#"), !line.isEmpty, let url = URL(string: line, relativeTo: base)?.absoluteURL {
                playlist.segments.append(.init(url: url, key: key, sequence: sequence, range: pendingRange))
                pendingRange = nil
                sequence += 1
            }
        }
        guard !playlist.segments.isEmpty else { throw HLSError.empty }
        return playlist
    }

    static func hexData(_ text: String) -> Data? {
        var hex = text.lowercased()
        if hex.hasPrefix("0x") { hex.removeFirst(2) }
        guard hex.count % 2 == 0 else { return nil }
        var data = Data(capacity: hex.count / 2)
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            data.append(byte)
            index = next
        }
        return data
    }
}

/// Downloads an HLS stream: every media segment is fetched (several at once), decrypted when the
/// playlist uses AES-128, kept on disk so a paused download resumes, and joined in order at the end.
/// `.ts` output is converted to `.mp4` when ffmpeg is installed; fMP4 streams are already `.mp4`.
public final class StreamDownload: DownloadJob, @unchecked Sendable {
    public let id: UUID
    public let request: DownloadRequest
    public let directory: URL
    public var maxConnections: Int
    public var replaceExisting = false
    public var onProgress: (@Sendable (ProgressSnapshot) -> Void)?

    private let lock = NSLock()
    private var status: DownloadStatus = .queued
    private var runner: Task<Void, Never>?
    private var generation = 0
    private var partsDone = 0
    private var partsTotal = 0
    private var bytesDone: Int64 = 0
    private var bytesThisTick: Int64 = 0
    private var speed: Double = 0
    private var speedLimit: Int64?
    private var limiterWindowStart = Date()
    private var limiterWindowBytes: Int64 = 0
    private var filename: String
    private var finalURL: URL?
    private var lastError: String?
    private var keys: [URL: Data] = [:]

    private var partsDirectory: URL {
        directory.appendingPathComponent(".macdm/\(id.uuidString)-parts", isDirectory: true)
    }

    public init(id: UUID = UUID(), request: DownloadRequest, directory: URL, maxConnections: Int = 8) {
        self.id = id
        self.request = request
        self.directory = directory
        self.maxConnections = max(1, maxConnections)
        // Playlists are often called index/master/playlist.m3u8; the folder name says more.
        let generic: Set<String> = ["index", "master", "playlist", "prog_index", "chunklist", "manifest", "main", "video"]
        var base = request.url.deletingPathExtension().lastPathComponent
        if generic.contains(base.lowercased()) {
            base = request.url.deletingLastPathComponent().lastPathComponent
        }
        if let given = request.filename, !given.isEmpty { base = (given as NSString).deletingPathExtension }
        filename = Probe.sanitize(base) ?? "video"
    }

    // MARK: Control

    public func start() {
        lock.lock()
        guard ![.probing, .downloading, .completed].contains(status) else { lock.unlock(); return }
        generation += 1
        let current = generation
        status = .probing
        lastError = nil
        lock.unlock()
        emit()
        runner = Task { [self] in
            do {
                try await run(generation: current)
            } catch is CancellationError {
                // paused or cancelled; status already set
            } catch {
                finish(generation: current, error: error)
            }
        }
    }

    public func pause() {
        lock.lock()
        guard [.probing, .downloading].contains(status) else { lock.unlock(); return }
        generation += 1
        status = .paused
        lock.unlock()
        runner?.cancel()
        emit()
    }

    public func cancel() {
        lock.lock()
        generation += 1
        status = .failed
        lastError = "Cancelled"
        lock.unlock()
        runner?.cancel()
        try? FileManager.default.removeItem(at: partsDirectory)
        emit()
    }

    public func setSpeedLimit(_ bytesPerSecond: Int64?) {
        lock.lock()
        speedLimit = bytesPerSecond.flatMap { $0 > 0 ? $0 : nil }
        lock.unlock()
    }

    // MARK: Work

    private func run(generation current: Int) async throws {
        let session = URLSession(configuration: .macdm)
        defer { session.finishTasksAndInvalidate() }

        // A master playlist (HLS) or manifest (DASH) lists qualities; separate audio is its own track.
        let manifest = try await fetch(request.url, session: session)
        let text = String(decoding: manifest.prefix(1_000_000), as: UTF8.self)
        var tracks: [Track]
        if DASH.looksLikeManifest(text) {
            let (video, audio) = try DASH.tracks(manifest, base: request.url, videoBandwidth: request.variantBandwidth)
            tracks = [Track(name: "v", playlist: video)] + (audio.map { [Track(name: "a", playlist: $0)] } ?? [])
        } else {
            var videoURL = request.url
            var audioURL: URL?
            var videoText = text
            if let master = Playlist.master(text, base: request.url), let variant = master.pick(bandwidth: request.variantBandwidth) {
                videoURL = variant.url
                audioURL = master.audioURL(for: variant)
                videoText = try await fetchText(videoURL, session: session)
            }
            tracks = [Track(name: "v", playlist: try Playlist.media(videoText, base: videoURL))]
            if let audioURL {
                tracks.append(Track(name: "a", playlist: try Playlist.media(try await fetchText(audioURL, session: session), base: audioURL)))
            }
        }

        try FileManager.default.createDirectory(at: partsDirectory, withIntermediateDirectories: true)
        var work: [(track: Track, index: Int)] = []
        var resumedParts = 0
        var resumedBytes: Int64 = 0
        for track in tracks {
            for index in track.playlist.segments.indices {
                let part = partURL(track, index)
                if FileManager.default.fileExists(atPath: part.path) {
                    resumedParts += 1
                    resumedBytes += fileSize(part)
                } else {
                    work.append((track, index))
                }
            }
        }
        let total = tracks.reduce(0) { $0 + $1.playlist.segments.count }
        lock.withLock {
            partsTotal = total
            partsDone = resumedParts
            bytesDone = resumedBytes
            status = .downloading
        }

        let ticker = Task { [self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(500))
                tick()
            }
        }
        defer { ticker.cancel() }

        for track in tracks {
            if let initSection = track.playlist.initSection, !FileManager.default.fileExists(atPath: initURL(track).path) {
                try save(try await fetch(initSection, range: track.playlist.initRange, session: session), to: initURL(track))
            }
        }

        var pending = work[...]
        try await withThrowingTaskGroup(of: Void.self) { group in
            func next() {
                guard let (track, index) = pending.popFirst() else { return }
                group.addTask {
                    try await self.download(track.playlist.segments[index], to: self.partURL(track, index), session: session)
                }
            }
            for _ in 0..<min(maxConnections, 16) { next() }
            while try await group.next() != nil { next() }
        }

        try Task.checkCancellation()
        let output = try assemble(tracks)
        finish(generation: current, output: output)
    }

    private func download(_ segment: MediaPlaylist.Segment, to part: URL, session: URLSession) async throws {
        var attempt = 0
        while true {
            try Task.checkCancellation()
            do {
                let size: Int64
                if let key = segment.key {
                    let data = try decrypt(try await fetch(segment.url, range: segment.range, session: session),
                                           key: try await keyData(key.uri, session: session),
                                           iv: key.iv ?? sequenceIV(segment.sequence))
                    try save(data, to: part)
                    size = Int64(data.count)
                } else {
                    // Straight to disk: DASH single-file tracks can be hundreds of megabytes.
                    size = try await fetchToFile(segment.url, range: segment.range, destination: part, session: session)
                }
                lock.withLock {
                    partsDone += 1
                    bytesDone += size
                    bytesThisTick += size
                }
                try await throttle(size)
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                attempt += 1
                if attempt > 6 { throw error }
                try await Task.sleep(for: .seconds(min(30, 1 << (attempt - 1))))
            }
        }
    }

    /// Sleeps just long enough to keep the average rate under the limit.
    private func throttle(_ bytes: Int64) async throws {
        let wait: Double = lock.withLock {
            guard let limit = speedLimit else { return 0 }
            limiterWindowBytes += bytes
            let elapsed = Date().timeIntervalSince(limiterWindowStart)
            let allowed = Double(limiterWindowBytes) / Double(limit)
            if elapsed > 5 {
                limiterWindowStart = Date()
                limiterWindowBytes = 0
            }
            return allowed - elapsed
        }
        if wait > 0 { try await Task.sleep(for: .seconds(wait)) }
    }

    private func makeRequest(_ url: URL, range: ClosedRange<Int64>?) -> URLRequest {
        var urlRequest = URLRequest(url: url)
        apply(request.headers, to: &urlRequest)
        if let range { urlRequest.setValue("bytes=\(range.lowerBound)-\(range.upperBound)", forHTTPHeaderField: "Range") }
        return urlRequest
    }

    private func check(_ response: URLResponse, ranged: Bool) throws {
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw DownloadError.badStatus((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        if ranged && http.statusCode != 206 { throw DownloadError.rangeIgnored }
    }

    private func fetch(_ url: URL, range: ClosedRange<Int64>? = nil, session: URLSession) async throws -> Data {
        let (data, response) = try await session.data(for: makeRequest(url, range: range))
        try check(response, ranged: range != nil)
        return data
    }

    /// Downloads into `destination` (via a temporary file, so a partial file never counts) and returns its size.
    private func fetchToFile(_ url: URL, range: ClosedRange<Int64>?, destination: URL, session: URLSession) async throws -> Int64 {
        let (temporary, response) = try await session.download(for: makeRequest(url, range: range))
        try check(response, ranged: range != nil)
        _ = try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temporary, to: destination)
        return fileSize(destination)
    }

    private func fetchText(_ url: URL, session: URLSession) async throws -> String {
        guard let text = String(data: try await fetch(url, session: session), encoding: .utf8) else { throw HLSError.notAPlaylist }
        return text
    }

    private func keyData(_ url: URL, session: URLSession) async throws -> Data {
        if let cached = lock.withLock({ keys[url] }) { return cached }
        let data = try await fetch(url, session: session)
        guard data.count == 16 else { throw HLSError.unsupportedEncryption("key of \(data.count) bytes") }
        lock.withLock { keys[url] = data }
        return data
    }

    /// Without an explicit IV, HLS uses the segment's media sequence number as a 128-bit big-endian value.
    private func sequenceIV(_ sequence: Int) -> Data {
        var iv = Data(count: 16)
        var value = UInt64(sequence).bigEndian
        withUnsafeBytes(of: &value) { iv.replaceSubrange(8..<16, with: $0) }
        return iv
    }

    private func decrypt(_ data: Data, key: Data, iv: Data) throws -> Data {
        var output = Data(count: data.count + kCCBlockSizeAES128)
        var written = 0
        let capacity = output.count
        let status = output.withUnsafeMutableBytes { out in
            data.withUnsafeBytes { input in
                key.withUnsafeBytes { keyBytes in
                    iv.withUnsafeBytes { ivBytes in
                        CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                keyBytes.baseAddress, kCCKeySizeAES128, ivBytes.baseAddress,
                                input.baseAddress, data.count, out.baseAddress, capacity, &written)
                    }
                }
            }
        }
        guard status == kCCSuccess else { throw HLSError.unsupportedEncryption("segment could not be decrypted") }
        output.count = written
        return output
    }

    // MARK: Files

    /// One media playlist to fetch: the video (or only) track, or a separate audio track.
    struct Track {
        let name: String
        let playlist: MediaPlaylist
    }

    private func initURL(_ track: Track) -> URL { partsDirectory.appendingPathComponent("\(track.name)-init.part") }
    private func partURL(_ track: Track, _ index: Int) -> URL {
        partsDirectory.appendingPathComponent(String(format: "%@-%06d.part", track.name, index))
    }

    private func fileSize(_ url: URL) -> Int64 {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? 0
    }

    /// Write-then-rename, so a half-written segment never counts as done.
    private func save(_ data: Data, to url: URL) throws {
        let temporary = url.appendingPathExtension("tmp")
        try data.write(to: temporary)
        _ = try? FileManager.default.removeItem(at: url)
        try FileManager.default.moveItem(at: temporary, to: url)
    }

    /// Concatenates one track's segments in order (after its init section, for fMP4).
    private func join(_ track: Track) throws -> URL {
        let fragmented = track.playlist.initSection != nil
        let container = track.playlist.container ?? (fragmented ? "mp4" : "ts")
        let joined = partsDirectory.appendingPathComponent("\(track.name)-joined.\(container)")
        FileManager.default.createFile(atPath: joined.path, contents: nil)
        let handle = try FileHandle(forWritingTo: joined)
        if fragmented { try handle.write(contentsOf: try Data(contentsOf: initURL(track))) }
        for index in track.playlist.segments.indices {
            try handle.write(contentsOf: try Data(contentsOf: partURL(track, index)))
        }
        try handle.close()
        return joined
    }

    private func assemble(_ tracks: [Track]) throws -> URL {
        let video = try join(tracks[0])
        var result = video
        var leftoverAudio: URL?
        if tracks.count > 1 {
            let audio = try join(tracks[1])
            if let merged = ffmpeg(["-i", video.path, "-i", audio.path, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy"]
                                   + (audio.pathExtension == "ts" ? ["-bsf:a", "aac_adtstoasc"] : []),
                                   output: partsDirectory.appendingPathComponent(
                                       video.pathExtension == "webm" && audio.pathExtension == "webm" ? "merged.webm" : "merged.mp4")) {
                result = merged
            } else {
                leftoverAudio = audio // no ffmpeg: keep the audio next to the video
            }
        } else if video.pathExtension == "ts", let converted = remuxToMP4(video) {
            result = converted
        }
        let destination = uniqueDestination(for: "\(filename).\(result.pathExtension)")
        try FileManager.default.moveItem(at: result, to: destination)
        if let leftoverAudio {
            let audioName = destination.deletingPathExtension().lastPathComponent + " (audio).\(leftoverAudio.pathExtension)"
            try? FileManager.default.moveItem(at: leftoverAudio, to: directory.appendingPathComponent(audioName))
        }
        try? FileManager.default.removeItem(at: partsDirectory)
        return destination
    }

    /// Repackages MPEG-TS as MP4 without re-encoding, so QuickTime can play it.
    private func remuxToMP4(_ source: URL) -> URL? {
        ffmpeg(["-i", source.path, "-c", "copy", "-bsf:a", "aac_adtstoasc"],
               output: source.deletingPathExtension().appendingPathExtension("mp4"))
    }

    /// Runs ffmpeg (if installed) with `arguments` writing `output`; nil if unavailable or it fails.
    private func ffmpeg(_ arguments: [String], output: URL) -> URL? {
        guard let ffmpeg = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"].first(where: FileManager.default.isExecutableFile)
        else { return nil }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: ffmpeg)
        process.arguments = ["-y", "-loglevel", "error"] + arguments + [output.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            try? FileManager.default.removeItem(at: output)
            return nil
        }
        return output
    }

    private func uniqueDestination(for name: String) -> URL {
        var candidate = directory.appendingPathComponent(name)
        if replaceExisting {
            try? FileManager.default.removeItem(at: candidate)
            return candidate
        }
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var counter = 1
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = directory.appendingPathComponent("\(base) (\(counter)).\(ext)")
            counter += 1
        }
        return candidate
    }

    // MARK: Progress

    private func finish(generation current: Int, output: URL? = nil, error: Error? = nil) {
        lock.lock()
        guard generation == current else { lock.unlock(); return }
        if let output {
            finalURL = output
            filename = output.deletingPathExtension().lastPathComponent
            status = .completed
        } else {
            lastError = error?.localizedDescription
            status = .failed
        }
        lock.unlock()
        emit()
    }

    private func tick() {
        lock.lock()
        let instant = Double(bytesThisTick) / 0.5
        speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3
        bytesThisTick = 0
        lock.unlock()
        emit()
    }

    private func emit() {
        lock.lock()
        var snapshot = ProgressSnapshot()
        snapshot.status = status
        snapshot.filename = finalURL?.lastPathComponent ?? filename
        snapshot.downloadedBytes = bytesDone
        snapshot.bytesPerSecond = status == .downloading ? speed : 0
        snapshot.partsDone = partsDone
        snapshot.partsTotal = partsTotal
        snapshot.resumable = true
        snapshot.fileURL = finalURL
        snapshot.error = lastError
        lock.unlock()
        onProgress?(snapshot)
    }
}
