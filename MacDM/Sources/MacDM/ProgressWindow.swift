import AppKit
import DownloadEngine
import SwiftUI

/// Per-download window: status, speed limiter, completion options, and per-connection progress.
struct ProgressWindow: View {
    let id: UUID
    @EnvironmentObject private var manager: DownloadManager

    var body: some View {
        if let item = manager.item(id) {
            ProgressContent(item: item)
        } else {
            Text("This download was removed.").padding(40)
        }
    }
}

@MainActor
private final class ProgressModel: ObservableObject {
    @Published var showDetails = true
    @Published var tab = 0
    @Published var limitText = ""
}

private struct ProgressContent: View {
    @ObservedObject var item: DownloadItem
    @EnvironmentObject private var manager: DownloadManager
    @StateObject private var model = ProgressModel()
    @Environment(\.dismiss) private var dismiss

    private var snapshot: ProgressSnapshot { item.snapshot }
    private var title: String {
        let percent = snapshot.fraction.map { "\(Int($0 * 100))% " } ?? ""
        return item.isFinished ? "Complete — \(item.displayName)" : "\(percent)\(item.displayName)"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            TabView(selection: $model.tab) {
                statusTab.tabItem { Text("Download status") }.tag(0)
                limiterTab.tabItem { Text("Speed Limiter") }.tag(1)
                completionTab.tabItem { Text("Options on completion") }.tag(2)
            }
            .frame(height: 190)

            ProgressView(value: snapshot.fraction ?? (item.isFinished ? 1 : 0))
                .progressViewStyle(.linear)

            HStack {
                Button(model.showDetails ? "<< Hide details" : "Show details >>") { model.showDetails.toggle() }
                Spacer()
                if item.isFinished {
                    Button("Open") { manager.open(item) }
                    Button("Show in Finder") { manager.reveal(item) }
                } else if item.isActive || snapshot.status == .queued {
                    Button("Pause") { manager.stop(item) }.keyboardShortcut(.defaultAction)
                } else {
                    Button("Resume") { manager.resume(item) }.keyboardShortcut(.defaultAction)
                }
                Button(item.isFinished ? "Close" : "Cancel") {
                    if !item.isFinished && (item.isActive || snapshot.downloadedBytes > 0) { manager.stop(item) }
                    dismiss()
                }
            }

            if model.showDetails {
                details
            }
        }
        .padding(14)
        .frame(width: 540)
        .navigationTitle(title)
    }

    private var statusTab: some View {
        Grid(alignment: .leading, horizontalSpacing: 14, verticalSpacing: 6) {
            Text(item.request.url.absoluteString)
                .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                .gridCellColumns(2)
            row("Status", statusLine)
            row("File size", snapshot.totalBytes.map(format(bytes:)) ?? (snapshot.partsTotal > 0 ? "Stream" : "Unknown"))
            if snapshot.partsTotal > 0 { row("Stream parts", "\(snapshot.partsDone) of \(snapshot.partsTotal)") }
            row("Downloaded", downloadedLine)
            row("Transfer rate", item.isActive ? format(rate: snapshot.bytesPerSecond) : "")
            row("Time left", format(seconds: item.timeLeft))
            row("Resume capability", snapshot.status == .queued || snapshot.status == .probing ? "" : (snapshot.resumable ? "Yes" : "No"))
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var statusLine: String {
        switch snapshot.status {
        case .queued: "Waiting…"
        case .probing: "Connecting…"
        case .downloading: "Receiving data…"
        case .paused: "Paused"
        case .completed: "Complete"
        case .failed: snapshot.error ?? "Error"
        }
    }

    private var downloadedLine: String {
        let done = format(bytes: snapshot.downloadedBytes)
        guard let fraction = snapshot.fraction else { return done }
        return "\(done) (\(String(format: "%.2f", fraction * 100))%)"
    }

    private func row(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary)
            Text(value).monospacedDigit().foregroundStyle(label == "Status" && snapshot.status == .failed ? .red : .primary)
        }
    }

    private var limiterTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            Toggle("Use speed limiter for this download", isOn: Binding(
                get: { item.speedLimitKB != nil },
                set: { manager.setSpeedLimit(item, kilobytes: $0 ? (Int(model.limitText) ?? 512) : nil) }
            ))
            HStack {
                Text("Maximum download speed")
                TextField("512", text: $model.limitText)
                    .frame(width: 80)
                    .onSubmit { if item.speedLimitKB != nil { manager.setSpeedLimit(item, kilobytes: Int(model.limitText) ?? 512) } }
                Text("KB/s")
            }
            .disabled(item.speedLimitKB == nil)
            Text("When off, the global limiter from Options applies.")
                .font(.caption).foregroundStyle(.secondary)
            Spacer()
        }
        .padding(10)
        .onAppear { model.limitText = String(item.speedLimitKB ?? 512) }
    }

    private var completionTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            Toggle("Show download complete dialog", isOn: $item.showCompleteDialog)
            Toggle("Open the file when done", isOn: $item.openWhenDone)
            Toggle("Put the Mac to sleep when done", isOn: $item.sleepWhenDone)
            Spacer()
        }
        .padding(10)
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Start positions and download progress by connections")
                .font(.caption).foregroundStyle(.secondary)
            SegmentBar(snapshot: snapshot).frame(height: 12)
            Table(snapshot.segments.enumerated().map { ConnectionRow(number: $0.offset + 1, segment: $0.element) }) {
                TableColumn("N.") { Text("\($0.number)").monospacedDigit() }.width(32)
                TableColumn("Downloaded") { Text(format(bytes: $0.segment.downloaded)).monospacedDigit() }.width(100)
                TableColumn("Info") { Text($0.segment.info) }
            }
            .frame(height: 170)
        }
    }
}

private struct ConnectionRow: Identifiable {
    let number: Int
    let segment: SegmentSnapshot
    var id: Int { number }
}

/// Shown when a download finishes (unless turned off).
struct CompleteWindow: View {
    let id: UUID
    @EnvironmentObject private var manager: DownloadManager
    @Environment(\.dismiss) private var dismiss
    @AppStorage(Prefs.showCompleteDialog) private var showDialog = true

    var body: some View {
        if let item = manager.item(id) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 12) {
                    Image(nsImage: fileIcon(for: item.displayName)).resizable().frame(width: 48, height: 48)
                    VStack(alignment: .leading) {
                        Text("Download complete").font(.headline)
                        Text(item.displayName).lineLimit(1).truncationMode(.middle)
                    }
                }
                Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 4) {
                    GridRow { Text("Downloaded").foregroundStyle(.secondary); Text(item.snapshot.totalBytes.map(format(bytes:)) ?? format(bytes: item.snapshot.downloadedBytes)) }
                    GridRow { Text("Address").foregroundStyle(.secondary); Text(item.request.url.absoluteString).lineLimit(1).truncationMode(.middle) }
                    GridRow { Text("Saved as").foregroundStyle(.secondary); Text(item.snapshot.fileURL?.path ?? "").lineLimit(1).truncationMode(.head).textSelection(.enabled) }
                }
                .font(.callout)
                Toggle("Don't show this dialog again", isOn: Binding(get: { !showDialog }, set: { showDialog = !$0 }))
                HStack {
                    Spacer()
                    Button("Open Folder") { manager.reveal(item); dismiss() }
                    Button("Open") { manager.open(item); dismiss() }.keyboardShortcut(.defaultAction)
                    Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
                }
            }
            .padding(16)
            .frame(width: 460)
            .navigationTitle("Download complete")
        }
    }
}
