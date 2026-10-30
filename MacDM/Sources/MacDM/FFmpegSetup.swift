import AppKit
import SwiftUI

/// Finds ffmpeg (used to merge stream audio and convert .ts to .mp4) and installs it with
/// Homebrew when the user asks. Nothing is installed without the Install button.
@MainActor
final class FFmpegSetup: ObservableObject {
    static let shared = FFmpegSetup()

    enum State: Equatable {
        case found(String)
        case missing
        case installing
        case failed(String)
    }

    @Published private(set) var state: State = .missing
    @Published private(set) var log = ""

    /// Same places the download engine looks.
    static let searchPaths = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]
    static let brewPaths = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]

    private init() { refresh() }

    var brewPath: String? { Self.brewPaths.first(where: FileManager.default.isExecutableFile) }
    var isInstalled: Bool { if case .found = state { true } else { false } }

    /// Whether to open the setup window at launch.
    var shouldPromptAtLaunch: Bool {
        !isInstalled && !UserDefaults.standard.bool(forKey: Prefs.ffmpegPromptDismissed)
    }

    func refresh() {
        guard state != .installing else { return }
        // `-MacDMSimulateMissingFFmpeg YES` on the command line shows the missing state for testing.
        let simulateMissing = UserDefaults.standard.bool(forKey: "MacDMSimulateMissingFFmpeg")
        if !simulateMissing, let path = Self.searchPaths.first(where: FileManager.default.isExecutableFile) {
            state = .found(path)
        } else {
            state = .missing
        }
    }

    func install() {
        guard let brew = brewPath, state != .installing else { return }
        state = .installing
        log = "$ brew install ffmpeg\n"

        let process = Process()
        process.executableURL = URL(fileURLWithPath: brew)
        process.arguments = ["install", "ffmpeg"]
        var environment = ProcessInfo.processInfo.environment
        // Apps launched from Finder get a minimal PATH; Homebrew needs its own bin directory.
        let brewBin = (brew as NSString).deletingLastPathComponent
        environment["PATH"] = "\(brewBin):/usr/bin:/bin:/usr/sbin:/sbin"
        environment["HOMEBREW_NO_ENV_HINTS"] = "1"
        process.environment = environment

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        pipe.fileHandleForReading.readabilityHandler = { handle in
            let text = String(decoding: handle.availableData, as: UTF8.self)
            guard !text.isEmpty else { return }
            Task { @MainActor in FFmpegSetup.shared.append(text) }
        }
        process.terminationHandler = { finished in
            pipe.fileHandleForReading.readabilityHandler = nil
            let status = finished.terminationStatus
            Task { @MainActor in FFmpegSetup.shared.finished(status: status) }
        }
        do {
            try process.run()
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    private func append(_ text: String) {
        log += text
        if log.count > 20_000 { log = String(log.suffix(20_000)) } // keep the view light
    }

    private func finished(status: Int32) {
        state = .missing
        refresh()
        if !isInstalled {
            state = .failed(status == 0 ? "Homebrew finished, but ffmpeg wasn't found." : "brew exited with status \(status).")
        }
    }
}

/// "Video tools" window: shown at first launch when ffmpeg is missing, and from Options.
struct FFmpegSetupView: View {
    @ObservedObject private var setup = FFmpegSetup.shared
    @AppStorage(Prefs.ffmpegPromptDismissed) private var dismissed = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: "film.stack").font(.system(size: 34)).foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Video tools").font(.title3.bold())
                    Text("MacDM uses the free ffmpeg tool to join video streams with their audio and save them as MP4.")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            switch setup.state {
            case .found(let path):
                Label("ffmpeg is installed (\(path)).", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
            case .missing where setup.brewPath == nil:
                Label("ffmpeg isn't installed, and neither is Homebrew, which installs it.", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                Text("Install Homebrew from brew.sh (it asks for your Mac password in Terminal), then click Check Again.")
                    .font(.callout).foregroundStyle(.secondary)
            case .missing:
                Label("ffmpeg isn't installed. Click Install to add it with Homebrew; this takes a few minutes.", systemImage: "info.circle")
            case .installing:
                HStack {
                    ProgressView().controlSize(.small)
                    Text("Installing ffmpeg with Homebrew…")
                }
            case .failed(let reason):
                Label("Installation didn't finish: \(reason)", systemImage: "xmark.octagon.fill")
                    .foregroundStyle(.red)
            }

            if !setup.log.isEmpty {
                ScrollViewReader { proxy in
                    ScrollView {
                        Text(setup.log)
                            .font(.system(size: 11, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .textSelection(.enabled)
                        Color.clear.frame(height: 1).id("end")
                    }
                    .frame(height: 160)
                    .background(Color(nsColor: .textBackgroundColor))
                    .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color(nsColor: .separatorColor)))
                    .onChange(of: setup.log) { _, _ in proxy.scrollTo("end", anchor: .bottom) }
                }
            }

            Text("Without ffmpeg, ordinary downloads still work. Streams are saved as .ts files, and streams with a separate audio track are saved as two files.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                if !setup.isInstalled {
                    Toggle("Don't show this at launch", isOn: $dismissed).toggleStyle(.checkbox)
                }
                Spacer()
                switch setup.state {
                case .found:
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
                case .installing:
                    Button("Close") { dismiss() } // the install keeps running
                case _ where setup.brewPath == nil:
                    Button("Not Now") { dismiss() }
                    Button("Check Again") { setup.refresh() }
                    Button("Open brew.sh") { NSWorkspace.shared.open(URL(string: "https://brew.sh")!) }
                        .keyboardShortcut(.defaultAction)
                default:
                    Button("Not Now") { dismiss() }
                    Button("Install") { setup.install() }.keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(18)
        .frame(width: 520)
        .navigationTitle("Video Tools")
        .onAppear { setup.refresh() }
    }
}
