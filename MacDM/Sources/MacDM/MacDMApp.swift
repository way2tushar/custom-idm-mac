import AppKit
import DownloadEngine
import SwiftUI

@main
struct MacDMApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var manager = DownloadManager.shared
    @AppStorage(Prefs.menuBarIcon) private var showMenuBarIcon = true

    var body: some Scene {
        Window("MacDM", id: "main") {
            MainView()
                .environmentObject(manager)
                .frame(minWidth: 860, minHeight: 440)
        }
        .defaultSize(width: 1120, height: 580)
        .commands { MenuCommands() }

        WindowGroup("Download", id: "progress", for: UUID.self) { $id in
            if let id { ProgressWindow(id: id).environmentObject(manager) }
        }
        .windowResizability(.contentSize)

        WindowGroup("Download complete", id: "complete", for: UUID.self) { $id in
            if let id { CompleteWindow(id: id).environmentObject(manager) }
        }
        .windowResizability(.contentSize)

        WindowGroup("Download File Info", id: "add", for: DownloadRequest.self) { $request in
            AddDownloadView(initial: request ?? AddDownloadView.blank).environmentObject(manager)
        }
        .windowResizability(.contentSize)

        Window("Video Tools", id: "ffmpeg") {
            FFmpegSetupView()
        }
        .windowResizability(.contentSize)

        Window("Scheduler", id: "scheduler") {
            SchedulerView().environmentObject(manager)
        }
        .windowResizability(.contentSize)

        Settings {
            OptionsView().environmentObject(manager)
        }

        MenuBarExtra("MacDM", systemImage: "arrow.down.circle", isInserted: $showMenuBarIcon) {
            MenuBarContent().environmentObject(manager)
        }
    }
}

struct MenuCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("Add New Download…") { MenuAction.add.post() }.keyboardShortcut("n")
            Button("Add Links from Clipboard to Queue") { MenuAction.addFromClipboard.post() }
                .keyboardShortcut("v", modifiers: [.command, .shift])
        }
        CommandMenu("Tasks") {
            Button("Start Queue") { MenuAction.startQueue.post() }
            Button("Stop Queue") { MenuAction.stopQueue.post() }
            Divider()
            Button("Scheduler…") { openWindow(id: "scheduler") }.keyboardShortcut("k")
        }
        CommandMenu("Downloads") {
            Button("Resume Download") { MenuAction.resume.post() }.keyboardShortcut("r")
            Button("Stop Download") { MenuAction.stop.post() }.keyboardShortcut(".")
            Button("Stop All") { MenuAction.stopAll.post() }.keyboardShortcut(".", modifiers: [.command, .shift])
            Button("Redownload") { MenuAction.redownload.post() }
            Divider()
            Button("Open") { MenuAction.open.post() }.keyboardShortcut("o")
            Button("Show in Finder") { MenuAction.reveal.post() }.keyboardShortcut("r", modifiers: [.command, .shift])
            Button("Copy Address") { MenuAction.copyAddress.post() }
            Button("Properties") { MenuAction.properties.post() }.keyboardShortcut("i")
            Divider()
            Button("Delete") { MenuAction.delete.post() }.keyboardShortcut(.delete)
            Button("Delete All Completed") { MenuAction.deleteCompleted.post() }
            Divider()
            Button("Turn Speed Limiter On/Off") { MenuAction.toggleSpeedLimiter.post() }.keyboardShortcut("l")
        }
        CommandGroup(after: .sidebar) {
            Button("Show/Hide Categories") { MenuAction.toggleCategories.post() }
                .keyboardShortcut("s", modifiers: [.command, .control])
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var server: LocalServer?
    private var dockPolicy: DockPolicy?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        let server = LocalServer { request in
            Task { @MainActor in
                NSApp.activate(ignoringOtherApps: true)
                DownloadManager.shared.addFromBrowser(request)
            }
        }
        if UserDefaults.standard.bool(forKey: Prefs.browserBridge) { server.start() }
        self.server = server
        dockPolicy = MainActor.assumeIsolated { DockPolicy() }
    }

    /// Handles `macdm://download?url=...` links (used as a fallback by the Safari extension);
    /// `macdm://launch` only starts the app, so the Chrome extension can then use the bridge.
    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where url.scheme == "macdm" {
            let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            func value(_ name: String) -> String? { query.first { $0.name == name }?.value.flatMap { $0.isEmpty ? nil : $0 } }
            guard let target = value("url").flatMap(URL.init(string:)),
                  ["http", "https"].contains(target.scheme?.lowercased()) else { continue }
            let request = DownloadRequest(
                url: target,
                headers: value("referrer").map { ["Referer": $0] } ?? [:],
                filename: value("filename").map { ($0 as NSString).lastPathComponent },
                kind: value("kind").flatMap(DownloadRequest.Kind.init(rawValue:)),
                variantBandwidth: value("variant").flatMap(Int.init)
            )
            Task { @MainActor in DownloadManager.shared.addFromBrowser(request) }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { DownloadManager.shared.pauseAllForQuit() }
        Thread.sleep(forTimeInterval: 0.3) // let engines flush their resume state
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Clicking the Dock icon with no windows open brings the main window back.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows { MainActor.assumeIsolated { WindowOpener.showMain() } }
        return true
    }
}
