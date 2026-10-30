import Foundation

enum ConnectionState {
    case idle, connecting, receiving, finished
    case retrying(Int)

    var text: String {
        switch self {
        case .idle: "Idle"
        case .connecting: "Connecting…"
        case .receiving: "Receiving data…"
        case .finished: "Completed"
        case .retrying(let attempt): "Connection lost, retry \(attempt)…"
        }
    }
}

/// One byte range of the file. `end` is inclusive; `Int64.max` means "until the stream ends".
final class Segment {
    var start: Int64
    var end: Int64
    var downloaded: Int64 = 0
    var task: URLSessionDataTask?
    var retries = 0
    var state: ConnectionState = .idle

    init(start: Int64, end: Int64, downloaded: Int64 = 0) {
        self.start = start
        self.end = end
        self.downloaded = downloaded
    }

    var position: Int64 { start + downloaded }
    var remaining: Int64 { end == .max ? .max : max(0, end - position + 1) }
    var isDone: Bool { end != .max && position > end }
}

/// Multi-connection download with dynamic segmentation:
/// the file starts as N equal ranges, and whenever a connection finishes its range
/// it takes over the second half of the largest range still in progress.
/// All mutable state is confined to `queue`; URLSession delegate callbacks run on it too.
public final class SegmentedDownload: NSObject, URLSessionDataDelegate, DownloadJob, @unchecked Sendable {
    public let id: UUID
    public let request: DownloadRequest
    public let directory: URL
    public var maxConnections: Int
    /// Bytes per second; nil means unlimited. Change it with `setSpeedLimit(_:)`.
    public private(set) var speedLimit: Int64?
    /// Replace an existing file with the same name instead of adding " (1)".
    public var replaceExisting = false
    /// Called on the engine queue; hop to the main actor before touching UI.
    public var onProgress: (@Sendable (ProgressSnapshot) -> Void)?

    /// Ranges smaller than twice this are never split again.
    static let minimumSplit: Int64 = 512 * 1024
    static let maxRetries = 8

    private let queue: DispatchQueue
    private var status: DownloadStatus = .queued
    private var probe: ProbeResult?
    private var segments: [Segment] = []
    private var tasks: [ObjectIdentifier: Segment] = [:]
    private var file: FileHandle?
    private var timer: DispatchSourceTimer?
    private var bytesThisTick: Int64 = 0
    private var bytesThisWindow: Int64 = 0
    private var throttled = false
    private var speed: Double = 0
    private var ticks = 0
    private var finalURL: URL?
    private var lastError: String?
    private var generation = 0

    private var workDirectory: URL { directory.appendingPathComponent(".macdm", isDirectory: true) }
    private var partURL: URL { workDirectory.appendingPathComponent("\(id.uuidString).part") }
    private var stateURL: URL { workDirectory.appendingPathComponent("\(id.uuidString).json") }

    public init(id: UUID = UUID(), request: DownloadRequest, directory: URL, maxConnections: Int = 8) {
        self.id = id
        self.request = request
        self.directory = directory
        self.maxConnections = max(1, maxConnections)
        self.queue = DispatchQueue(label: "macdm.download.\(id.uuidString)")
    }

    // MARK: Public control

    public func start() {
        queue.async { [self] in
            guard status != .downloading, status != .probing, status != .completed else { return }
            status = .probing
            lastError = nil
            generation += 1
            let current = generation
            emit()
            Task {
                do {
                    let result = try await Probe.run(request)
                    queue.async { if self.generation == current, self.status == .probing { self.begin(result) } }
                } catch {
                    queue.async { if self.generation == current { self.fail(error) } }
                }
            }
        }
    }

    public func pause() {
        queue.async { [self] in
            guard status == .downloading || status == .probing else { return }
            generation += 1
            stopTransfers()
            saveState()
            status = .paused
            emit()
        }
    }

    public func setSpeedLimit(_ bytesPerSecond: Int64?) {
        queue.async { [self] in
            speedLimit = bytesPerSecond.flatMap { $0 > 0 ? $0 : nil }
            if speedLimit == nil { releaseThrottle() }
        }
    }

    /// Stops the download and deletes its partial data.
    public func cancel() {
        queue.async { [self] in
            generation += 1
            stopTransfers()
            try? FileManager.default.removeItem(at: partURL)
            try? FileManager.default.removeItem(at: stateURL)
            status = .failed
            lastError = "Cancelled"
            emit()
        }
    }

    // MARK: Setup

    private func begin(_ result: ProbeResult) {
        probe = result
        do {
            try FileManager.default.createDirectory(at: workDirectory, withIntermediateDirectories: true)
            segments = restoredSegments(for: result) ?? freshSegments(for: result)
            if !FileManager.default.fileExists(atPath: partURL.path) {
                FileManager.default.createFile(atPath: partURL.path, contents: nil)
            }
            let handle = try FileHandle(forWritingTo: partURL)
            if let total = result.totalBytes, result.acceptsRanges {
                try handle.truncate(atOffset: UInt64(total)) // sparse preallocation
            }
            file = handle
        } catch {
            fail(error)
            return
        }

        status = .downloading
        startTimer()
        fillConnections()
        if segments.allSatisfy(\.isDone) { finish() }
    }

    private func freshSegments(for result: ProbeResult) -> [Segment] {
        try? FileManager.default.removeItem(at: partURL)
        guard result.acceptsRanges, let total = result.totalBytes, total > 0 else {
            return [Segment(start: 0, end: result.totalBytes.map { $0 - 1 } ?? .max)]
        }
        let count = Int(max(1, min(Int64(maxConnections), total / Self.minimumSplit)))
        let size = total / Int64(count)
        return (0..<count).map { index in
            let start = Int64(index) * size
            let end = index == count - 1 ? total - 1 : start + size - 1
            return Segment(start: start, end: end)
        }
    }

    /// Reuses saved progress only if the server still describes the same file.
    private func restoredSegments(for result: ProbeResult) -> [Segment]? {
        guard result.acceptsRanges,
              let data = try? Data(contentsOf: stateURL),
              let state = try? JSONDecoder().decode(ResumeState.self, from: data),
              FileManager.default.fileExists(atPath: partURL.path),
              state.totalBytes == result.totalBytes,
              state.etag == result.etag || result.etag == nil,
              state.lastModified == result.lastModified || result.lastModified == nil
        else { return nil }
        return state.segments.map { Segment(start: $0.start, end: $0.end, downloaded: $0.downloaded) }
    }

    // MARK: Scheduling

    private var activeCount: Int { segments.filter { $0.task != nil }.count }

    private func fillConnections() {
        guard status == .downloading else { return }
        for segment in segments where segment.task == nil && !segment.isDone && activeCount < maxConnections {
            launch(segment)
        }
        while activeCount < maxConnections, let segment = splitLargest() {
            launch(segment)
        }
    }

    /// Halves the active range with the most bytes left and returns the new back half.
    private func splitLargest() -> Segment? {
        guard probe?.acceptsRanges == true,
              let largest = segments.filter({ $0.task != nil && !$0.isDone }).max(by: { $0.remaining < $1.remaining }),
              largest.remaining >= 2 * Self.minimumSplit
        else { return nil }
        let middle = largest.position + largest.remaining / 2
        let back = Segment(start: middle, end: largest.end)
        largest.end = middle - 1 // its in-flight request is cut off once it reaches the new end
        segments.insert(back, at: segments.firstIndex { $0 === largest }! + 1)
        return back
    }

    private func launch(_ segment: Segment) {
        guard let probe else { return }
        var urlRequest = URLRequest(url: probe.finalURL)
        apply(request.headers, to: &urlRequest)
        if probe.acceptsRanges {
            urlRequest.setValue("bytes=\(segment.position)-\(segment.end)", forHTTPHeaderField: "Range")
        } else {
            segment.downloaded = 0 // no ranges: a restarted stream begins at byte 0
        }
        // A session per connection keeps each range on its own TCP connection;
        // a shared session would multiplex them over a single HTTP/2 connection.
        let operations = OperationQueue()
        operations.underlyingQueue = queue
        operations.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: .macdm, delegate: self, delegateQueue: operations)
        let task = session.dataTask(with: urlRequest)
        segment.task = task
        segment.state = .connecting
        tasks[ObjectIdentifier(task)] = segment
        if !throttled { task.resume() } // otherwise releaseThrottle() starts it
        session.finishTasksAndInvalidate()
    }

    private func detach(_ segment: Segment) {
        if let task = segment.task {
            tasks[ObjectIdentifier(task)] = nil
            task.cancel()
        }
        segment.task = nil
        segment.state = .idle
    }

    private func stopTransfers() {
        for segment in segments { detach(segment) }
        timer?.cancel()
        timer = nil
        try? file?.synchronize()
        try? file?.close()
        file = nil
    }

    // MARK: URLSessionDataDelegate

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                           didReceive response: URLResponse,
                           completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let segment = tasks[ObjectIdentifier(dataTask)], let http = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            return
        }
        let ranged = probe?.acceptsRanges == true
        if !(200..<300).contains(http.statusCode) {
            completionHandler(.cancel)
            retry(segment, reason: DownloadError.badStatus(http.statusCode).localizedDescription)
        } else if ranged && http.statusCode != 206 {
            completionHandler(.cancel)
            detach(segment)
            fail(DownloadError.rangeIgnored)
        } else {
            segment.state = .receiving
            completionHandler(.allow)
        }
    }

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard let segment = tasks[ObjectIdentifier(dataTask)], let file else { return }
        var chunk = data
        if segment.remaining < Int64(chunk.count) {
            chunk = chunk.prefix(Int(segment.remaining))
        }
        do {
            try file.seek(toOffset: UInt64(segment.position))
            try file.write(contentsOf: chunk)
        } catch {
            fail(error)
            return
        }
        segment.downloaded += Int64(chunk.count)
        segment.retries = 0
        bytesThisTick += Int64(chunk.count)
        bytesThisWindow += Int64(chunk.count)
        if let speedLimit, !throttled, bytesThisWindow >= speedLimit / 4 {
            // Over this quarter-second's budget: hold every connection until the next tick.
            throttled = true
            for task in tasks.values.compactMap(\.task) { task.suspend() }
        }

        if segment.isDone {
            detach(segment)
            segment.state = .finished
            segmentFinished()
        }
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let segment = tasks.removeValue(forKey: ObjectIdentifier(task)) else { return } // we cancelled it
        segment.task = nil
        guard status == .downloading else { return }
        if let error {
            retry(segment, reason: error.localizedDescription)
        } else if segment.end == .max {
            segment.end = segment.position - 1 // unknown length: the stream ending means done
            segment.state = .finished
            segmentFinished()
        } else if !segment.isDone {
            retry(segment, reason: "Connection closed early")
        } else {
            segment.state = .finished
            segmentFinished()
        }
    }

    private func retry(_ segment: Segment, reason: String) {
        detach(segment)
        segment.retries += 1
        segment.state = .retrying(segment.retries)
        guard segment.retries <= Self.maxRetries else {
            fail(DownloadError.tooManyRetries(reason))
            return
        }
        let current = generation
        let delay = min(30, pow(2, Double(segment.retries - 1)))
        queue.asyncAfter(deadline: .now() + delay) { [self] in
            guard generation == current, status == .downloading, segment.task == nil, !segment.isDone else { return }
            launch(segment)
        }
    }

    private func segmentFinished() {
        if segments.allSatisfy(\.isDone) {
            finish()
        } else {
            fillConnections()
        }
    }

    // MARK: Completion

    private func finish() {
        guard status == .downloading else { return }
        stopTransfers()
        do {
            let destination = uniqueDestination(for: probe?.filename ?? "download")
            try FileManager.default.moveItem(at: partURL, to: destination)
            try? FileManager.default.removeItem(at: stateURL)
            finalURL = destination
            status = .completed
        } catch {
            fail(error)
            return
        }
        emit()
    }

    private func fail(_ error: Error) {
        generation += 1
        stopTransfers()
        saveState()
        lastError = error.localizedDescription
        status = .failed
        emit()
    }

    private func uniqueDestination(for name: String) -> URL {
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var candidate = directory.appendingPathComponent(name)
        if replaceExisting {
            try? FileManager.default.removeItem(at: candidate)
            return candidate
        }
        var counter = 1
        while FileManager.default.fileExists(atPath: candidate.path) {
            let numbered = ext.isEmpty ? "\(base) (\(counter))" : "\(base) (\(counter)).\(ext)"
            candidate = directory.appendingPathComponent(numbered)
            counter += 1
        }
        return candidate
    }

    // MARK: Progress

    private func startTimer() {
        timer?.cancel()
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now() + 0.25, repeating: 0.25)
        source.setEventHandler { [weak self] in self?.tick() }
        source.resume()
        timer = source
    }

    private func tick() {
        bytesThisWindow = 0
        releaseThrottle()
        ticks += 1
        guard ticks % 2 == 0 else { return }
        let instant = Double(bytesThisTick) / 0.5
        speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3
        bytesThisTick = 0
        if ticks % 8 == 0 { saveState() }
        emit()
    }

    private func releaseThrottle() {
        guard throttled else { return }
        throttled = false
        for task in tasks.values.compactMap(\.task) { task.resume() }
    }

    private func saveState() {
        guard let probe, probe.acceptsRanges, let total = probe.totalBytes else { return }
        let state = ResumeState(
            url: probe.finalURL, totalBytes: total, etag: probe.etag, lastModified: probe.lastModified,
            filename: probe.filename,
            segments: segments.map { .init(start: $0.start, end: $0.end, downloaded: $0.downloaded) }
        )
        try? file?.synchronize()
        try? JSONEncoder().encode(state).write(to: stateURL, options: .atomic)
    }

    private func emit() {
        var snapshot = ProgressSnapshot()
        snapshot.status = status
        snapshot.filename = finalURL?.lastPathComponent ?? probe?.filename
        snapshot.totalBytes = probe?.totalBytes
        snapshot.downloadedBytes = segments.reduce(0) { $0 + $1.downloaded }
        snapshot.bytesPerSecond = status == .downloading ? speed : 0
        snapshot.segments = segments.map {
            SegmentSnapshot(start: $0.start, end: $0.end, downloaded: $0.downloaded,
                            active: $0.task != nil, info: $0.isDone ? "Completed" : $0.state.text)
        }
        snapshot.activeConnections = activeCount
        snapshot.resumable = probe?.acceptsRanges ?? false
        snapshot.fileURL = finalURL
        snapshot.error = lastError
        if status != .downloading { speed = 0 }
        onProgress?(snapshot)
    }
}
