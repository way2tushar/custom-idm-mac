import AppKit
import SwiftUI

struct OptionsView: View {
    var body: some View {
        TabView {
            GeneralOptions().tabItem { Label("General", systemImage: "gearshape") }
            SaveToOptions().tabItem { Label("Save To", systemImage: "folder") }
            FileTypeOptions().tabItem { Label("File Types", systemImage: "doc.on.doc") }
            ConnectionOptions().tabItem { Label("Connection", systemImage: "network") }
            DownloadOptions().tabItem { Label("Downloads", systemImage: "arrow.down.circle") }
            ProxyOptions().tabItem { Label("Proxy", systemImage: "server.rack") }
        }
        .frame(width: 560)
    }
}

@MainActor
private final class LoginItemModel: ObservableObject {
    @Published var enabled = LoginItem.isEnabled
    @Published var error: String?

    func set(_ on: Bool) {
        error = LoginItem.set(on)
        enabled = LoginItem.isEnabled
    }
}

private struct GeneralOptions: View {
    @AppStorage(Prefs.browserBridge) private var browserBridge = true
    @AppStorage(Prefs.browserCapture) private var browserCapture = true
    @AppStorage(Prefs.videoGrabber) private var videoGrabber = true
    @AppStorage(Prefs.playSound) private var playSound = true
    @AppStorage(Prefs.menuBarIcon) private var menuBarIcon = true
    @AppStorage(Prefs.hideDockWhenClosed) private var hideDockWhenClosed = true
    @StateObject private var login = LoginItemModel()

    var body: some View {
        Form {
            Section("Running in the background") {
                Toggle("Launch MacDM at login", isOn: Binding(get: { login.enabled }, set: { login.set($0) }))
                if let error = login.error {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
                Toggle("Show MacDM in the menu bar", isOn: $menuBarIcon)
                Toggle("Hide the Dock icon while the window is closed", isOn: $hideDockWhenClosed)
                    .disabled(!menuBarIcon)
                Text("Closing the window keeps MacDM running: downloads continue and browser extensions keep working. Quit from the menu bar icon or with ⌘Q.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Browser integration") {
                Toggle("Accept downloads from browser extensions", isOn: $browserBridge)
                Toggle("Capture downloads started in the browser", isOn: $browserCapture)
                    .disabled(!browserBridge)
                Toggle("Show the \"Download with MacDM\" button over videos", isOn: $videoGrabber)
                    .disabled(!browserBridge)
                Text("The Chrome and Safari extensions connect to http://127.0.0.1:\(String(LocalServer.port)). Changes apply after relaunch. File types to capture are set in each extension's popup.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Video tools") {
                VideoToolsRow()
            }
            Section("Sounds") {
                Toggle("Play a sound when a download completes", isOn: $playSound)
            }
        }
        .formStyle(.grouped)
    }
}

private struct SaveToOptions: View {
    @AppStorage(Prefs.directory) private var directory = FileManager.default
        .urls(for: .downloadsDirectory, in: .userDomainMask)[0].path
    @ObservedObject private var store = FolderStore()

    var body: some View {
        Form {
            Section("Default download folder") {
                folderRow(path: directory) { directory = $0 }
            }
            Section("Category folders") {
                ForEach(FileCategory.allCases) { category in
                    LabeledContent {
                        folderRow(path: store.path(for: category) ?? "Default folder") { store.set($0, for: category) }
                    } label: {
                        Label(category.title, systemImage: category.symbol)
                    }
                    .contextMenu { Button("Use Default Folder") { store.set(nil, for: category) } }
                }
                Text("Right-click a category to go back to the default folder.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private func folderRow(path: String, set: @escaping (String) -> Void) -> some View {
        HStack {
            Text((path as NSString).abbreviatingWithTildeInPath).lineLimit(1).truncationMode(.middle)
            Spacer()
            Button("Browse…") {
                let panel = NSOpenPanel()
                panel.canChooseDirectories = true
                panel.canChooseFiles = false
                panel.canCreateDirectories = true
                if panel.runModal() == .OK, let url = panel.url { set(url.path) }
            }
        }
    }
}

/// Per-category folder overrides, which live under dynamic UserDefaults keys.
@MainActor
private final class FolderStore: ObservableObject {
    func path(for category: FileCategory) -> String? {
        UserDefaults.standard.string(forKey: Prefs.categoryFolderKey(category)).flatMap { $0.isEmpty ? nil : $0 }
    }

    func set(_ path: String?, for category: FileCategory) {
        objectWillChange.send()
        UserDefaults.standard.set(path, forKey: Prefs.categoryFolderKey(category))
    }
}

private struct ConnectionOptions: View {
    @AppStorage(Prefs.connections) private var connections = 8

    var body: some View {
        Form {
            Section("Connections") {
                Picker("Max. connections number", selection: $connections) {
                    ForEach([1, 2, 4, 8, 16, 24, 32], id: \.self) { Text("\($0)").tag($0) }
                }
                Text("Each download is split into this many ranges at most. A connection that finishes takes over half of the largest remaining range. Applies to downloads started or resumed after the change.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct DownloadOptions: View {
    @AppStorage(Prefs.showStartDialog) private var showStartDialog = true
    @AppStorage(Prefs.showProgressDialog) private var showProgressDialog = true
    @AppStorage(Prefs.showCompleteDialog) private var showCompleteDialog = true
    @AppStorage(Prefs.duplicateAction) private var duplicateAction = "rename"
    @AppStorage(Prefs.speedLimitEnabled) private var speedLimitEnabled = false
    @AppStorage(Prefs.speedLimitKB) private var speedLimitKB = 512

    var body: some View {
        Form {
            Section("Dialogs") {
                Toggle("Show start download dialog for browser downloads", isOn: $showStartDialog)
                Toggle("Show download progress dialog", isOn: $showProgressDialog)
                Toggle("Show download complete dialog", isOn: $showCompleteDialog)
            }
            Section("Duplicate file names") {
                Picker("If a file with the same name exists", selection: $duplicateAction) {
                    Text("Add a number to the new file").tag("rename")
                    Text("Replace the existing file").tag("overwrite")
                }
            }
            Section("Speed Limiter") {
                Toggle("Turn on speed limiter", isOn: $speedLimitEnabled)
                Stepper("Maximum download speed: \(speedLimitKB) KB/s", value: $speedLimitKB, in: 16...1_048_576, step: 64)
                    .disabled(!speedLimitEnabled)
                Text("Applies to each download that has no limit of its own.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .onChange(of: speedLimitEnabled) { _, _ in DownloadManager.shared.optionsChanged() }
        .onChange(of: speedLimitKB) { _, _ in DownloadManager.shared.optionsChanged() }
    }
}

private struct ProxyOptions: View {
    @AppStorage(Prefs.proxyEnabled) private var proxyEnabled = false
    @AppStorage(Prefs.proxyHost) private var proxyHost = ""
    @AppStorage(Prefs.proxyPort) private var proxyPort = 8080

    var body: some View {
        Form {
            Section("Proxy server") {
                Toggle("Use a manual HTTP/HTTPS proxy", isOn: $proxyEnabled)
                TextField("Address", text: $proxyHost, prompt: Text("proxy.example.com"))
                    .disabled(!proxyEnabled)
                TextField("Port", value: $proxyPort, format: .number.grouping(.never))
                    .disabled(!proxyEnabled)
                Text("When off, macOS system proxy settings apply.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .onChange(of: proxyEnabled) { _, _ in DownloadManager.shared.optionsChanged() }
        .onChange(of: proxyHost) { _, _ in DownloadManager.shared.optionsChanged() }
        .onChange(of: proxyPort) { _, _ in DownloadManager.shared.optionsChanged() }
    }
}

private struct FileTypeOptions: View {
    @AppStorage(Prefs.captureTypes) private var types = CaptureRules.defaultTypes
    @AppStorage(Prefs.excludedSites) private var sites = CaptureRules.defaultSites
    @StateObject private var addresses = AddressListModel()

    var body: some View {
        Form {
            Section {
                TextEditor(text: $types)
                    .font(.system(.body, design: .monospaced))
                    .frame(height: 90)
                HStack {
                    Spacer()
                    Button("Default") { types = CaptureRules.defaultTypes }
                }
            } header: {
                Text("Automatically download these file types")
            } footer: {
                Text("Extensions separated by spaces. Applies to downloads the browser starts; links you send with \"Download with MacDM\" are always accepted.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                TextEditor(text: $sites)
                    .font(.system(.body, design: .monospaced))
                    .frame(height: 60)
                HStack {
                    Text("Names separated by spaces; *.example.com covers every subdomain.")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("Default") { sites = CaptureRules.defaultSites }
                }
            } header: {
                Text("Don't download automatically from these sites")
            }
            Section("Don't download automatically from these addresses") {
                HStack {
                    Text(addresses.items.isEmpty ? "No addresses" : "\(addresses.items.count) address\(addresses.items.count == 1 ? "" : "es")")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Edit List…") { addresses.editing = true }
                }
            }
        }
        .formStyle(.grouped)
        .sheet(isPresented: $addresses.editing) { AddressListEditor(model: addresses) }
    }
}

@MainActor
private final class AddressListModel: ObservableObject {
    @Published var items: [String] = UserDefaults.standard.stringArray(forKey: Prefs.excludedAddresses) ?? [] {
        didSet { UserDefaults.standard.set(items, forKey: Prefs.excludedAddresses) }
    }
    @Published var editing = false
    @Published var draft = ""
    @Published var selection: Set<String> = []
}

private struct AddressListEditor: View {
    @ObservedObject var model: AddressListModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Addresses MacDM won't take over").font(.headline)
            Text("Full addresses; * matches anything, e.g. https://example.com/files/*")
                .font(.caption).foregroundStyle(.secondary)
            List(model.items, id: \.self, selection: $model.selection) { Text($0).lineLimit(1).truncationMode(.middle) }
                .frame(height: 180)
            HStack {
                TextField("https://", text: $model.draft)
                    .onSubmit(add)
                Button("Add", action: add).disabled(model.draft.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Remove") {
                    model.items.removeAll { model.selection.contains($0) }
                    model.selection.removeAll()
                }
                .disabled(model.selection.isEmpty)
            }
            HStack {
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 480)
    }

    private func add() {
        let address = model.draft.trimmingCharacters(in: .whitespaces)
        guard !address.isEmpty, !model.items.contains(address) else { return }
        model.items.append(address)
        model.draft = ""
    }
}

private struct VideoToolsRow: View {
    @ObservedObject private var setup = FFmpegSetup.shared
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        LabeledContent("ffmpeg") {
            HStack {
                switch setup.state {
                case .found: Label("Installed", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                case .installing: Text("Installing…").foregroundStyle(.secondary)
                default: Label("Not installed", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                }
                if !setup.isInstalled {
                    Button("Install…") { openWindow(id: "ffmpeg") }
                }
            }
        }
        .onAppear { setup.refresh() }
    }
}
