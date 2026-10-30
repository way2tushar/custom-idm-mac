import AppKit
import DownloadEngine
import ServiceManagement
import SwiftUI

/// Opens SwiftUI windows from anywhere, including while the main window is closed.
/// The action is captured from the first view that appears (main window or menu bar menu).
@MainActor
enum WindowOpener {
    static var action: OpenWindowAction?

    static func showMain() {
        NSApp.setActivationPolicy(.regular)
        action?(id: "main")
        NSApp.activate(ignoringOtherApps: true)
    }

    static func open<Value: Codable & Hashable>(_ id: String, value: Value) {
        action?(id: id, value: value)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// Menu bar icon menu: status line and quick actions, available while the window is closed.
struct MenuBarContent: View {
    @EnvironmentObject private var manager: DownloadManager
    @Environment(\.openWindow) private var openWindow

    private var active: [DownloadItem] { manager.items.filter(\.isActive) }

    var body: some View {
        Group {
            if active.isEmpty {
                Text("No active downloads")
            } else {
                let speed = active.reduce(0) { $0 + $1.snapshot.bytesPerSecond }
                Text("\(active.count) downloading · \(format(rate: speed))")
                ForEach(active.prefix(5)) { item in
                    Text("\(item.displayName) — \(percentText(item.snapshot))")
                }
            }
            Divider()
            Button("Open MacDM") { WindowOpener.showMain() }
            Button("Add URL…") { WindowOpener.open("add", value: AddDownloadView.blank) }
            Divider()
            Button("Stop All") { manager.stopAll() }.disabled(active.isEmpty)
            Button(manager.queueRunning ? "Stop Queue" : "Start Queue") {
                manager.queueRunning ? manager.stopQueue() : manager.startQueue()
            }
            Divider()
            Button("Quit MacDM") { NSApp.terminate(nil) }.keyboardShortcut("q")
        }
        .onAppear { if WindowOpener.action == nil { WindowOpener.action = openWindow } }
    }
}

/// Hides the Dock icon while the main window is closed (when that option is on), and brings
/// it back when the window opens, so MacDM keeps working from the menu bar like a tray app.
@MainActor
final class DockPolicy {
    private var observers: [NSObjectProtocol] = []

    init() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                guard let window = note.object as? NSWindow, Self.isMain(window) else { return }
                if UserDefaults.standard.bool(forKey: Prefs.hideDockWhenClosed) && UserDefaults.standard.bool(forKey: Prefs.menuBarIcon) {
                    NSApp.setActivationPolicy(.accessory)
                }
            }
        })
        observers.append(center.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                guard let window = note.object as? NSWindow, Self.isMain(window) else { return }
                if NSApp.activationPolicy() != .regular { NSApp.setActivationPolicy(.regular) }
            }
        })
    }

    private static func isMain(_ window: NSWindow) -> Bool {
        window.identifier?.rawValue.hasPrefix("main") == true
    }
}

enum LoginItem {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    /// Returns an error message, or nil on success.
    static func set(_ enabled: Bool) -> String? {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            return nil
        } catch {
            return error.localizedDescription
        }
    }
}
