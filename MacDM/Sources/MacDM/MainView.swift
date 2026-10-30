import AppKit
import DownloadEngine
import SwiftUI

/// Menu commands reach the main window (which owns the selection) through notifications.
enum MenuAction: String {
    case add, addFromClipboard, resume, stop, stopAll, delete, deleteWithFile, deleteCompleted
    case redownload, open, reveal, copyAddress, properties, startQueue, stopQueue, addToQueue, removeFromQueue
    case toggleCategories, toggleSpeedLimiter

    static let notification = Notification.Name("MacDMMenuAction")

    func post() { NotificationCenter.default.post(name: Self.notification, object: rawValue) }
}

@MainActor
final class MainModel: ObservableObject {
    @Published var selection: Set<UUID> = []
    @Published var node: SidebarNode? = SidebarNode.allDownloads
    @Published var sortOrder = [KeyPathComparator(\DownloadItem.sortLastTry, order: .reverse)]
    @Published var showCategories = true
}

struct MainView: View {
    @EnvironmentObject private var manager: DownloadManager
    @StateObject private var model = MainModel()
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

    private var rows: [DownloadItem] {
        manager.items(in: model.node ?? .allDownloads).sorted(using: model.sortOrder)
    }

    private var selected: [DownloadItem] { manager.items.filter { model.selection.contains($0.id) } }

    var body: some View {
        VStack(spacing: 0) {
            ToolStrip(selected: selected, perform: perform)
            Divider()
            HSplitView {
                if model.showCategories {
                    CategorySidebar(node: $model.node)
                        .frame(minWidth: 160, idealWidth: 180, maxWidth: 240)
                }
                DownloadTable(rows: rows, model: model, perform: perform)
                    .frame(minWidth: 520)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: MenuAction.notification)) { note in
            if let raw = note.object as? String, let action = MenuAction(rawValue: raw) { perform(action) }
        }
        .onAppear { WindowOpener.action = openWindow }
    }

    private func perform(_ action: MenuAction) {
        switch action {
        case .add:
            openWindow(id: "add", value: AddDownloadView.blank)
        case .addFromClipboard:
            let text = NSPasteboard.general.string(forType: .string) ?? ""
            for word in text.split(whereSeparator: \.isWhitespace) {
                if let url = URL(string: String(word)), ["http", "https"].contains(url.scheme?.lowercased()) {
                    manager.add(DownloadRequest(url: url), startNow: false, toQueue: true)
                }
            }
        case .resume: selected.forEach(manager.resume)
        case .stop: selected.forEach(manager.stop)
        case .stopAll: manager.stopAll()
        case .delete, .deleteWithFile:
            guard !selected.isEmpty, confirmDelete(count: selected.count, withFile: action == .deleteWithFile) else { return }
            selected.forEach { manager.remove($0, deleteFile: action == .deleteWithFile) }
            model.selection.removeAll()
        case .deleteCompleted: manager.deleteCompleted()
        case .redownload: selected.forEach(manager.redownload)
        case .open: selected.forEach(manager.open)
        case .reveal: selected.forEach(manager.reveal)
        case .copyAddress:
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(selected.map(\.request.url.absoluteString).joined(separator: "\n"), forType: .string)
        case .properties: selected.forEach { openWindow(id: "progress", value: $0.id) }
        case .startQueue: manager.startQueue()
        case .stopQueue: manager.stopQueue()
        case .addToQueue: selected.forEach { manager.setQueued($0, true) }
        case .removeFromQueue: selected.forEach { manager.setQueued($0, false) }
        case .toggleCategories: model.showCategories.toggle()
        case .toggleSpeedLimiter:
            let defaults = UserDefaults.standard
            defaults.set(!defaults.bool(forKey: Prefs.speedLimitEnabled), forKey: Prefs.speedLimitEnabled)
            manager.optionsChanged()
        }
    }

    private func confirmDelete(count: Int, withFile: Bool) -> Bool {
        let alert = NSAlert()
        alert.messageText = count == 1 ? "Delete this download from the list?" : "Delete \(count) downloads from the list?"
        alert.informativeText = withFile ? "Downloaded files will be moved to the Trash." : "Downloaded files stay on disk. Partial data is discarded."
        alert.addButton(withTitle: "Delete")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }
}

// MARK: Toolbar

struct ToolStrip: View {
    let selected: [DownloadItem]
    let perform: (MenuAction) -> Void
    @EnvironmentObject private var manager: DownloadManager
    @Environment(\.openSettings) private var openSettings
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 2) {
            ToolButton("Add URL", "plus.circle.fill", .green) { perform(.add) }
            ToolButton("Resume", "play.circle.fill", .blue) { perform(.resume) }
                .disabled(!selected.contains { [.paused, .failed].contains($0.snapshot.status) })
            ToolButton("Stop", "stop.circle.fill", .orange) { perform(.stop) }
                .disabled(!selected.contains { $0.isActive || $0.snapshot.status == .queued })
            ToolButton("Stop All", "xmark.octagon.fill", .red) { perform(.stopAll) }
                .disabled(!manager.items.contains { $0.isActive || $0.snapshot.status == .queued })
            Divider().frame(height: 40)
            ToolButton("Delete", "trash.fill", .gray) { perform(.delete) }
                .disabled(selected.isEmpty)
            ToolButton("Delete Completed", "checkmark.circle.badge.xmark", .gray) { perform(.deleteCompleted) }
                .disabled(!manager.items.contains(where: \.isFinished))
            Divider().frame(height: 40)
            ToolButton("Options", "gearshape.fill", .secondary) { openSettings() }
            ToolButton("Scheduler", "calendar.badge.clock", .purple) { openWindow(id: "scheduler") }
            ToolButton("Start Queue", "play.square.stack.fill", .teal) { perform(.startQueue) }
                .disabled(manager.queueRunning || !manager.items.contains { $0.inQueue && !$0.isFinished })
            ToolButton("Stop Queue", "stop.fill", .pink) { perform(.stopQueue) }
                .disabled(!manager.queueRunning)
            Spacer()
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(.bar)
    }
}

struct ToolButton: View {
    let title: String
    let symbol: String
    let tint: Color
    let action: () -> Void
    @Environment(\.isEnabled) private var isEnabled

    init(_ title: String, _ symbol: String, _ tint: Color, action: @escaping () -> Void) {
        self.title = title
        self.symbol = symbol
        self.tint = tint
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            VStack(spacing: 3) {
                Image(systemName: symbol)
                    .font(.system(size: 24))
                    .foregroundStyle(isEnabled ? tint : Color.secondary.opacity(0.5))
                    .frame(height: 28)
                Text(title)
                    .font(.system(size: 11))
                    .lineLimit(1)
                    .fixedSize()
            }
            .frame(minWidth: 58)
            .padding(.horizontal, 4)
            .padding(.vertical, 4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .help(title)
    }
}

// MARK: Sidebar

struct CategorySidebar: View {
    @Binding var node: SidebarNode?
    @EnvironmentObject private var manager: DownloadManager

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Categories")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10)
                .padding(.top, 6)
            List(SidebarNode.tree, children: \.children, selection: $node) { entry in
                HStack {
                    Label(entry.title, systemImage: entry.symbol)
                    Spacer()
                    let count = manager.items(in: entry).count
                    if count > 0 {
                        Text("\(count)").font(.caption).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                .tag(entry)
            }
            .listStyle(.sidebar)
        }
    }
}

// MARK: Table

struct DownloadTable: View {
    let rows: [DownloadItem]
    @ObservedObject var model: MainModel
    let perform: (MenuAction) -> Void
    @EnvironmentObject private var manager: DownloadManager

    var body: some View {
        Table(rows, selection: $model.selection, sortOrder: $model.sortOrder) {
            TableColumn("File Name", value: \.displayName) { NameCell(item: $0) }
                .width(min: 140, ideal: 210)
            TableColumn("Q") { QueueCell(item: $0) }
                .width(18)
            TableColumn("Size", value: \.sortSize) { item in
                Cell(item: item) { sizeText($0.snapshot) }
            }
            .width(min: 55, ideal: 72)
            TableColumn("Status", value: \.sortProgress) { item in Cell(item: item) { statusText($0) } }
                .width(min: 55, ideal: 68)
            TableColumn("Time left", value: \.sortTimeLeft) { item in Cell(item: item) { format(seconds: $0.timeLeft) } }
                .width(min: 50, ideal: 64)
            TableColumn("Transfer rate", value: \.sortSpeed) { item in
                Cell(item: item) { $0.isActive ? format(rate: $0.snapshot.bytesPerSecond) : "" }
            }
            .width(min: 60, ideal: 82)
            TableColumn("Last Try Date", value: \.sortLastTry) { item in
                Cell(item: item) { ($0.lastTry ?? $0.addedAt).formatted(date: .abbreviated, time: .shortened) }
            }
            .width(min: 90, ideal: 118)
            TableColumn("Description", value: \.itemDescription) { item in Cell(item: item) { $0.itemDescription } }
                .width(min: 70, ideal: 110)
        }
        .contextMenu(forSelectionType: UUID.self) { ids in
            if !ids.isEmpty {
                Button("Open") { perform(.open) }
                Button("Show in Finder") { perform(.reveal) }
                Divider()
                Button("Resume Download") { perform(.resume) }
                Button("Stop Download") { perform(.stop) }
                Button("Redownload") { perform(.redownload) }
                Divider()
                Button("Add to Main Queue") { perform(.addToQueue) }
                Button("Remove from Queue") { perform(.removeFromQueue) }
                Divider()
                Button("Copy Address") { perform(.copyAddress) }
                Button("Delete") { perform(.delete) }
                Button("Delete and Move File to Trash") { perform(.deleteWithFile) }
                Divider()
                Button("Properties") { perform(.properties) }
            } else {
                Button("Add URL…") { perform(.add) }
            }
        } primaryAction: { ids in
            for item in manager.items where ids.contains(item.id) {
                item.isFinished ? manager.open(item) : perform(.properties)
            }
        }
    }
}

/// Re-renders when its item's progress changes, without rebuilding the whole table.
private struct Cell: View {
    @ObservedObject var item: DownloadItem
    let text: (DownloadItem) -> String

    var body: some View {
        Text(text(item)).lineLimit(1).monospacedDigit()
            .foregroundStyle(item.snapshot.status == .failed ? .red : .primary)
            .help(item.snapshot.error ?? "")
    }
}

private struct NameCell: View {
    @ObservedObject var item: DownloadItem

    var body: some View {
        HStack(spacing: 6) {
            Image(nsImage: fileIcon(for: item.displayName)).resizable().frame(width: 16, height: 16)
            Text(item.displayName).lineLimit(1).truncationMode(.middle)
        }
    }
}

private struct QueueCell: View {
    @ObservedObject var item: DownloadItem

    var body: some View {
        if item.inQueue {
            Image(systemName: "clock").font(.caption).foregroundStyle(.secondary).help("In the main queue")
        }
    }
}
