import AppKit
import DownloadEngine
import SwiftUI

@MainActor
private final class AddModel: ObservableObject {
    @Published var address = ""
    @Published var filename = ""
    @Published var category: FileCategory = .general
    @Published var folder = ""
    @Published var itemDescription = ""
    @Published var rememberFolder = false
    @Published var replaceDuplicate = false
    var userPickedCategory = false
    var userPickedFolder = false
}

/// Download file info dialog: address, category, save location and description,
/// with Start Download / Download Later / Cancel.
struct AddDownloadView: View {
    /// Placeholder request meaning "empty form", since window values must be concrete.
    static let blank = DownloadRequest(url: URL(string: "about:blank")!)

    let initial: DownloadRequest
    @EnvironmentObject private var manager: DownloadManager
    @StateObject private var model = AddModel()
    @Environment(\.dismiss) private var dismiss

    private var url: URL? {
        URL(string: model.address.trimmingCharacters(in: .whitespacesAndNewlines))
            .flatMap { ["http", "https"].contains($0.scheme?.lowercased()) ? $0 : nil }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Form {
                TextField("URL", text: $model.address, prompt: Text("https://example.com/file.zip"))
                    .onChange(of: model.address) { _, _ in guessFromAddress() }
                Picker("Category", selection: Binding(get: { model.category }, set: { pickCategory($0) })) {
                    ForEach(FileCategory.allCases) { Label($0.title, systemImage: $0.symbol).tag($0) }
                }
                LabeledContent("Save As") {
                    HStack {
                        TextField("", text: $model.folder).labelsHidden()
                        Button("…", action: chooseFolder)
                    }
                }
                TextField("File name", text: $model.filename, prompt: Text("Detected from the server"))
                Toggle("Remember this path for \"\(model.category.title)\" category", isOn: $model.rememberFolder)
                TextField("Description", text: $model.itemDescription, axis: .vertical)
                    .lineLimit(2...3)
                if fileExists || listedAlready {
                    Section {
                        Label(duplicateMessage, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                        if fileExists {
                            Picker("Save", selection: $model.replaceDuplicate) {
                                Text("As \"\(numberedName)\"").tag(false)
                                Text("Replace the existing file").tag(true)
                            }
                            .pickerStyle(.radioGroup)
                        }
                    }
                }
            }
            .formStyle(.grouped)

            HStack {
                if let size = initialSizeHint { Text(size).font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Download Later") { submit(now: false) }.disabled(url == nil)
                Button("Start Download") { submit(now: true) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(url == nil)
            }
            .padding([.horizontal, .bottom], 16)
        }
        .frame(width: 560)
        .navigationTitle("Download File Info")
        .onAppear(perform: fill)
    }

    private var initialSizeHint: String? { nil }

    private var folderURL: URL {
        URL(fileURLWithPath: (model.folder as NSString).expandingTildeInPath, isDirectory: true)
    }

    /// The name the file will get, as far as it can be known before the server answers.
    private var plannedName: String {
        let given = model.filename.trimmingCharacters(in: .whitespaces)
        let name = given.isEmpty ? (url?.lastPathComponent ?? "") : given
        let isStream = initial.isStream || url?.pathExtension.lowercased() == "m3u8"
        return isStream && (name as NSString).pathExtension.lowercased() != "mp4"
            ? "\((name as NSString).deletingPathExtension).mp4" : name
    }

    private var fileExists: Bool {
        !plannedName.isEmpty && FileManager.default.fileExists(atPath: folderURL.appendingPathComponent(plannedName).path)
    }

    private var listedAlready: Bool {
        guard let url else { return false }
        return manager.items.contains { $0.request.url == url }
    }

    private var duplicateMessage: String {
        switch (fileExists, listedAlready) {
        case (true, true): "This address is already in your downloads, and \"\(plannedName)\" exists in this folder."
        case (true, false): "\"\(plannedName)\" already exists in this folder."
        default: "This address is already in your downloads. It will be downloaded again as a separate file."
        }
    }

    /// Same numbering the engine uses: "name (1).ext", "name (2).ext", …
    private var numberedName: String {
        let base = (plannedName as NSString).deletingPathExtension
        let ext = (plannedName as NSString).pathExtension
        var counter = 1
        var candidate = plannedName
        while FileManager.default.fileExists(atPath: folderURL.appendingPathComponent(candidate).path) {
            candidate = ext.isEmpty ? "\(base) (\(counter))" : "\(base) (\(counter)).\(ext)"
            counter += 1
        }
        return candidate
    }

    private func fill() {
        if initial != Self.blank {
            model.address = initial.url.absoluteString
            model.filename = initial.filename ?? ""
        } else if let clip = NSPasteboard.general.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines),
                  clip.hasPrefix("http"), !clip.contains(" ") {
            model.address = clip
        }
        guessFromAddress()
    }

    /// Picks the category and folder from the file extension until the user chooses one.
    private func guessFromAddress() {
        let name = model.filename.isEmpty ? (url?.lastPathComponent ?? "") : model.filename
        if !model.userPickedCategory { model.category = .of(filename: name) }
        if !model.userPickedFolder { model.folder = manager.saveFolder(for: model.category).path }
    }

    private func pickCategory(_ category: FileCategory) {
        model.category = category
        model.userPickedCategory = true
        if !model.userPickedFolder { model.folder = manager.saveFolder(for: category).path }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: model.folder)
        if panel.runModal() == .OK, let chosen = panel.url {
            model.folder = chosen.path
            model.userPickedFolder = true
        }
    }

    private func submit(now: Bool) {
        guard let url else { return }
        let folder = URL(fileURLWithPath: (model.folder as NSString).expandingTildeInPath, isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if model.rememberFolder {
            UserDefaults.standard.set(folder.path, forKey: Prefs.categoryFolderKey(model.category))
        }
        var request = initial == Self.blank ? DownloadRequest(url: url) : initial
        request.url = url
        let name = model.filename.trimmingCharacters(in: .whitespaces)
        request.filename = name.isEmpty ? nil : name
        manager.add(request, directory: folder, description: model.itemDescription, startNow: now, toQueue: !now,
                    replaceExisting: fileExists ? model.replaceDuplicate : nil)
        dismiss()
    }
}
