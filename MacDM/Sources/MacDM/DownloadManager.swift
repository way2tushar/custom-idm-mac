import AppKit
import DownloadEngine
import Foundation

@MainActor
final class DownloadItem: ObservableObject, Identifiable {
    let id: UUID
    let request: DownloadRequest
    let addedAt: Date
    @Published var snapshot: ProgressSnapshot
    @Published var directory: URL
    @Published var itemDescription: String
    @Published var lastTry: Date?
    @Published var inQueue: Bool
    /// Per-download speed limit in KB/s; nil uses the global limiter setting.
    @Published var speedLimitKB: Int?
    @Published var showCompleteDialog: Bool
    @Published var openWhenDone = false
    @Published var sleepWhenDone = false
    /// Chosen in the add dialog when the file already exists; nil follows the Options setting.
    var replaceExisting: Bool?
    var engine: DownloadJob?

    init(id: UUID = UUID(), request: DownloadRequest, addedAt: Date = Date(), directory: URL,
         description: String = "", inQueue: Bool = false, showCompleteDialog: Bool = true,
         snapshot: ProgressSnapshot = ProgressSnapshot()) {
        self.id = id
        self.request = request
        self.addedAt = addedAt
        self.directory = directory
        self.itemDescription = description
        self.inQueue = inQueue
        self.showCompleteDialog = showCompleteDialog
        self.snapshot = snapshot
    }

    var displayName: String {
        snapshot.filename ?? request.filename ?? request.url.lastPathComponent
    }

    var category: FileCategory { request.isStream ? .video : .of(filename: displayName) }
    var isFinished: Bool { snapshot.status == .completed }
    var isActive: Bool { [.probing, .downloading].contains(snapshot.status) }

    // Sort keys for the table columns.
    var sortSize: Int64 { snapshot.totalBytes ?? -1 }
    var sortProgress: Double { isFinished ? 2 : snapshot.fraction ?? 0 }
    var sortSpeed: Double { snapshot.bytesPerSecond }
    var sortTimeLeft: Double { timeLeft ?? .infinity }
    var sortLastTry: Date { lastTry ?? addedAt }

    var timeLeft: Double? {
        guard isActive, let total = snapshot.totalBytes, snapshot.bytesPerSecond > 1 else { return nil }
        return Double(total - snapshot.downloadedBytes) / snapshot.bytesPerSecond
    }
}

/// Owns the download list, the main queue and its scheduler, and persists everything.
@MainActor
final class DownloadManager: ObservableObject {
    static let shared = DownloadManager()

    @Published private(set) var items: [DownloadItem] = []
    @Published private(set) var queueRunning = false

    private let defaults = UserDefaults.standard
    private var schedulerTimer: Timer?
    private var lastScheduleCheck = Date()

    private var storeURL: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MacDM", isDirectory: true)
        try? FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        return support.appendingPathComponent("downloads.json")
    }

    private init() {
        Prefs.registerDefaults()
        applyProxy()
        load()
        schedulerTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { _ in
            Task { @MainActor in DownloadManager.shared.checkSchedule() }
        }
    }

    // MARK: Lookup

    func item(_ id: UUID) -> DownloadItem? { items.first { $0.id == id } }

    func items(in node: SidebarNode) -> [DownloadItem] {
        items.filter { item in
            let scopeMatches = switch node.scope {
            case .all: true
            case .unfinished: !item.isFinished
            case .finished: item.isFinished
            case .queue: item.inQueue
            }
            return scopeMatches && (node.category == nil || item.category == node.category)
        }
    }

    func saveFolder(for category: FileCategory) -> URL {
        if let custom = defaults.string(forKey: Prefs.categoryFolderKey(category)), !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        return defaults.string(forKey: Prefs.directory).map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
    }

    // MARK: Adding

    /// Downloads from the browser bridge follow the "show download dialog" option;
    /// when it is off they start straight away.
    func addFromBrowser(_ request: DownloadRequest) {
        if defaults.bool(forKey: Prefs.showStartDialog) {
            WindowOpener.open("add", value: request)
        } else {
            add(request)
        }
    }

    @discardableResult
    func add(_ request: DownloadRequest, directory: URL? = nil, description: String = "",
             startNow: Bool = true, toQueue: Bool = false, replaceExisting: Bool? = nil) -> DownloadItem {
        let name = request.filename ?? request.url.lastPathComponent
        let item = DownloadItem(
            request: request,
            directory: directory ?? saveFolder(for: .of(filename: name)),
            description: description,
            inQueue: toQueue,
            showCompleteDialog: defaults.bool(forKey: Prefs.showCompleteDialog)
        )
        item.snapshot.filename = request.filename
        item.replaceExisting = replaceExisting
        item.snapshot.status = startNow ? .queued : .paused
        items.insert(item, at: 0)
        save()
        if startNow {
            schedule()
            if defaults.bool(forKey: Prefs.showProgressDialog) {
                WindowOpener.open("progress", value: item.id)
            }
        }
        return item
    }

    // MARK: Item actions

    func resume(_ item: DownloadItem) {
        guard [.paused, .failed].contains(item.snapshot.status) else { return }
        item.snapshot.status = .queued
        item.snapshot.error = nil
        schedule()
    }

    func stop(_ item: DownloadItem) {
        if item.snapshot.status == .queued {
            item.snapshot.status = .paused
        } else {
            item.engine?.pause()
        }
    }

    func stopAll() {
        queueRunning = false
        items.forEach(stop)
    }

    func redownload(_ item: DownloadItem) {
        item.engine?.cancel()
        item.engine = nil
        if let file = item.snapshot.fileURL { try? FileManager.default.trashItem(at: file, resultingItemURL: nil) }
        let filename = item.snapshot.filename
        item.snapshot = ProgressSnapshot()
        item.snapshot.filename = filename
        item.snapshot.status = .queued
        schedule()
    }

    func remove(_ item: DownloadItem, deleteFile: Bool = false) {
        if item.isFinished {
            if deleteFile, let url = item.snapshot.fileURL { try? FileManager.default.trashItem(at: url, resultingItemURL: nil) }
        } else {
            (item.engine ?? makeEngine(for: item)).cancel()
        }
        items.removeAll { $0.id == item.id }
        save()
        schedule()
    }

    func deleteCompleted() {
        items.removeAll(where: \.isFinished)
        save()
    }

    func open(_ item: DownloadItem) {
        if let url = item.snapshot.fileURL { NSWorkspace.shared.open(url) }
    }

    func reveal(_ item: DownloadItem) {
        if let url = item.snapshot.fileURL {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            NSWorkspace.shared.open(item.directory)
        }
    }

    func setQueued(_ item: DownloadItem, _ queued: Bool) {
        item.inQueue = queued
        save()
    }

    func setSpeedLimit(_ item: DownloadItem, kilobytes: Int?) {
        item.speedLimitKB = kilobytes
        item.engine?.setSpeedLimit(effectiveLimit(for: item))
        save()
    }

    /// Re-applies global options to running engines after the Options window changes them.
    func optionsChanged() {
        applyProxy()
        for item in items { item.engine?.setSpeedLimit(effectiveLimit(for: item)) }
        schedule()
    }

    // MARK: Queue and scheduler

    func startQueue() {
        queueRunning = true
        for item in items where item.inQueue && [.paused, .failed].contains(item.snapshot.status) {
            item.snapshot.status = .queued
            item.snapshot.error = nil
        }
        schedule()
    }

    func stopQueue() {
        queueRunning = false
        for item in items where item.inQueue { stop(item) }
    }

    private func checkSchedule() {
        let now = Date()
        defer { lastScheduleCheck = now }
        guard defaults.bool(forKey: Prefs.schedulerEnabled) else { return }
        if !queueRunning, scheduledToday(now),
           crossed(minuteOfDay: defaults.integer(forKey: Prefs.schedulerStart), since: lastScheduleCheck, now: now) {
            startQueue()
        }
        if queueRunning, defaults.bool(forKey: Prefs.schedulerStopEnabled),
           crossed(minuteOfDay: defaults.integer(forKey: Prefs.schedulerStop), since: lastScheduleCheck, now: now) {
            stopQueue()
        }
    }

    private func scheduledToday(_ date: Date) -> Bool {
        guard defaults.string(forKey: Prefs.schedulerRepeat) == "daily" else { return true }
        let weekday = Calendar.current.component(.weekday, from: date) // 1 = Sunday
        let days = defaults.array(forKey: Prefs.schedulerDays) as? [Int] ?? Array(1...7)
        return days.contains(weekday)
    }

    private func crossed(minuteOfDay: Int, since previous: Date, now: Date) -> Bool {
        guard let target = Calendar.current.date(bySettingHour: minuteOfDay / 60, minute: minuteOfDay % 60, second: 0, of: now)
        else { return false }
        return previous < target && target <= now
    }

    /// Starts queued downloads. Ones added directly start at once; the main queue
    /// runs only while started, up to its own simultaneous-download limit.
    private func schedule() {
        let queueLimit = max(1, defaults.integer(forKey: Prefs.queueMaxConcurrent))
        var queueSlots = queueLimit - items.filter { $0.inQueue && $0.isActive }.count
        for item in items.reversed() where item.snapshot.status == .queued {
            if item.inQueue {
                guard queueRunning, queueSlots > 0 else { continue }
                queueSlots -= 1
            }
            begin(item)
        }
        finishQueueIfDone()
    }

    private func begin(_ item: DownloadItem) {
        let engine = item.engine ?? makeEngine(for: item)
        engine.maxConnections = max(1, defaults.integer(forKey: Prefs.connections))
        engine.replaceExisting = item.replaceExisting ?? (defaults.string(forKey: Prefs.duplicateAction) == "overwrite")
        engine.setSpeedLimit(effectiveLimit(for: item))
        item.engine = engine
        item.snapshot.status = .probing
        item.lastTry = Date()
        engine.start()
    }

    private func finishQueueIfDone() {
        guard queueRunning,
              !items.contains(where: { $0.inQueue && [.queued, .probing, .downloading].contains($0.snapshot.status) })
        else { return }
        queueRunning = false
        guard defaults.bool(forKey: Prefs.schedulerEnabled) else { return }
        switch defaults.string(forKey: Prefs.schedulerWhenDone) {
        case "quit": NSApp.terminate(nil)
        case "sleep": SystemActions.sleep()
        default: break
        }
    }

    private func effectiveLimit(for item: DownloadItem) -> Int64? {
        if let kb = item.speedLimitKB { return Int64(kb) * 1024 }
        guard defaults.bool(forKey: Prefs.speedLimitEnabled) else { return nil }
        return Int64(max(1, defaults.integer(forKey: Prefs.speedLimitKB))) * 1024
    }

    private func applyProxy() {
        let host = defaults.string(forKey: Prefs.proxyHost) ?? ""
        let port = defaults.integer(forKey: Prefs.proxyPort)
        ProxySettings.current = defaults.bool(forKey: Prefs.proxyEnabled) && !host.isEmpty && port > 0
            ? ProxySettings(host: host, port: port) : nil
    }

    private func makeEngine(for item: DownloadItem) -> DownloadJob {
        let connections = max(1, defaults.integer(forKey: Prefs.connections))
        let engine: DownloadJob = item.request.isStream
            ? StreamDownload(id: item.id, request: item.request, directory: item.directory, maxConnections: connections)
            : SegmentedDownload(id: item.id, request: item.request, directory: item.directory, maxConnections: connections)
        engine.onProgress = { [weak self, weak item] snapshot in
            Task { @MainActor in
                guard let self, let item else { return }
                let previous = item.snapshot.status
                item.snapshot = snapshot
                if previous != snapshot.status {
                    self.save()
                    if snapshot.status == .completed { self.completed(item) }
                    self.schedule()
                }
            }
        }
        return engine
    }

    private func completed(_ item: DownloadItem) {
        if defaults.bool(forKey: Prefs.playSound) { NSSound(named: "Glass")?.play() }
        if item.openWhenDone { open(item) }
        if item.showCompleteDialog {
            WindowOpener.open("complete", value: item.id)
        }
        if item.sleepWhenDone { SystemActions.sleep() }
    }

    // MARK: Persistence

    private struct Record: Codable {
        var id: UUID
        var request: DownloadRequest
        var addedAt: Date
        var status: DownloadStatus
        var filename: String?
        var totalBytes: Int64?
        var downloadedBytes: Int64
        var fileURL: URL?
        var error: String?
        var directory: URL?
        var description: String?
        var lastTry: Date?
        var inQueue: Bool?
        var speedLimitKB: Int?
        var showCompleteDialog: Bool?
    }

    func save() {
        let records = items.map {
            Record(id: $0.id, request: $0.request, addedAt: $0.addedAt, status: $0.snapshot.status,
                   filename: $0.snapshot.filename, totalBytes: $0.snapshot.totalBytes,
                   downloadedBytes: $0.snapshot.downloadedBytes, fileURL: $0.snapshot.fileURL, error: $0.snapshot.error,
                   directory: $0.directory, description: $0.itemDescription, lastTry: $0.lastTry,
                   inQueue: $0.inQueue, speedLimitKB: $0.speedLimitKB, showCompleteDialog: $0.showCompleteDialog)
        }
        try? JSONEncoder().encode(records).write(to: storeURL, options: .atomic)
    }

    private func load() {
        guard let data = try? Data(contentsOf: storeURL),
              let records = try? JSONDecoder().decode([Record].self, from: data) else { return }
        items = records.map { record in
            var snapshot = ProgressSnapshot()
            // Anything interrupted by quitting comes back stopped; its saved segments resume it.
            snapshot.status = [.queued, .probing, .downloading].contains(record.status) ? .paused : record.status
            snapshot.filename = record.filename
            snapshot.totalBytes = record.totalBytes
            snapshot.downloadedBytes = record.downloadedBytes
            snapshot.fileURL = record.fileURL
            snapshot.error = record.error
            snapshot.resumable = true
            let item = DownloadItem(
                id: record.id, request: record.request, addedAt: record.addedAt,
                directory: record.directory ?? saveFolder(for: .of(filename: record.filename ?? "")),
                description: record.description ?? "", inQueue: record.inQueue ?? false,
                showCompleteDialog: record.showCompleteDialog ?? true, snapshot: snapshot
            )
            item.lastTry = record.lastTry
            item.speedLimitKB = record.speedLimitKB
            return item
        }
    }

    func pauseAllForQuit() {
        for item in items where item.isActive { item.engine?.pause() }
        save()
    }
}

enum SystemActions {
    static func sleep() {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        process.arguments = ["sleepnow"]
        try? process.run()
    }
}

enum Prefs {
    static let connections = "connections"
    static let directory = "downloadDirectory"
    static let browserBridge = "browserBridge"
    static let showStartDialog = "showStartDialog"
    static let showProgressDialog = "showProgressDialog"
    static let showCompleteDialog = "showCompleteDialog"
    static let duplicateAction = "duplicateAction"
    static let playSound = "playSound"
    static let speedLimitEnabled = "speedLimitEnabled"
    static let speedLimitKB = "speedLimitKB"
    static let proxyEnabled = "proxyEnabled"
    static let proxyHost = "proxyHost"
    static let proxyPort = "proxyPort"
    static let queueMaxConcurrent = "queueMaxConcurrent"
    static let schedulerEnabled = "schedulerEnabled"
    static let schedulerStart = "schedulerStartMinute"
    static let schedulerStopEnabled = "schedulerStopEnabled"
    static let schedulerStop = "schedulerStopMinute"
    static let schedulerRepeat = "schedulerRepeat"
    static let schedulerDays = "schedulerDays"
    static let schedulerWhenDone = "schedulerWhenDone"
    static let menuBarIcon = "menuBarIcon"
    static let browserCapture = "browserCapture"
    static let videoGrabber = "videoGrabber"
    static let captureTypes = "captureTypes"
    static let excludedSites = "excludedSites"
    static let excludedAddresses = "excludedAddresses"
    static let ffmpegPromptDismissed = "ffmpegPromptDismissed"
    static let hideDockWhenClosed = "hideDockWhenClosed"

    static func categoryFolderKey(_ category: FileCategory) -> String { "folder.\(category.rawValue)" }

    static func registerDefaults() {
        UserDefaults.standard.register(defaults: [
            connections: 8, browserBridge: true, showStartDialog: true, showProgressDialog: true,
            showCompleteDialog: true, duplicateAction: "rename", playSound: true,
            speedLimitEnabled: false, speedLimitKB: 512, proxyEnabled: false, proxyPort: 8080,
            queueMaxConcurrent: 1, schedulerEnabled: false, schedulerStart: 2 * 60, schedulerStopEnabled: false,
            schedulerStop: 7 * 60, schedulerRepeat: "once", schedulerDays: Array(1...7), schedulerWhenDone: "nothing",
            menuBarIcon: true, hideDockWhenClosed: true, browserCapture: true, videoGrabber: true,
        ])
    }
}
