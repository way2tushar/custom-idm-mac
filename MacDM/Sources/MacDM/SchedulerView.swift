import SwiftUI

/// Main queue scheduler: when to start and stop, how many files at once, and what to do when done.
struct SchedulerView: View {
    var body: some View {
        TabView {
            ScheduleTab().tabItem { Text("Schedule") }
            QueueFilesTab().tabItem { Text("Files in the queue") }
        }
        .frame(width: 520, height: 470)
        .padding(.top, 8)
    }
}

private struct ScheduleTab: View {
    @EnvironmentObject private var manager: DownloadManager
    @AppStorage(Prefs.schedulerEnabled) private var enabled = false
    @AppStorage(Prefs.schedulerStart) private var startMinute = 120
    @AppStorage(Prefs.schedulerStopEnabled) private var stopEnabled = false
    @AppStorage(Prefs.schedulerStop) private var stopMinute = 420
    @AppStorage(Prefs.schedulerRepeat) private var repeatMode = "once"
    @AppStorage(Prefs.queueMaxConcurrent) private var maxConcurrent = 1
    @AppStorage(Prefs.schedulerWhenDone) private var whenDone = "nothing"
    @ObservedObject private var days = DayStore()

    var body: some View {
        Form {
            Section {
                Toggle("Start download at", isOn: $enabled)
                DatePicker("Start time", selection: time($startMinute), displayedComponents: .hourAndMinute)
                    .disabled(!enabled)
                Picker("Repeat", selection: $repeatMode) {
                    Text("Once").tag("once")
                    Text("Daily").tag("daily")
                }
                .pickerStyle(.radioGroup)
                .disabled(!enabled)
                if repeatMode == "daily" {
                    HStack {
                        ForEach(1...7, id: \.self) { weekday in
                            Toggle(Calendar.current.veryShortWeekdaySymbols[weekday - 1], isOn: days.binding(weekday))
                                .toggleStyle(.button)
                        }
                    }
                    .disabled(!enabled)
                }
            }
            Section {
                Toggle("Stop download at", isOn: $stopEnabled)
                DatePicker("Stop time", selection: time($stopMinute), displayedComponents: .hourAndMinute)
                    .disabled(!stopEnabled)
            }
            Section {
                Stepper("Download \(maxConcurrent) file\(maxConcurrent == 1 ? "" : "s") at the same time",
                        value: $maxConcurrent, in: 1...10)
                Picker("When done", selection: $whenDone) {
                    Text("Do nothing").tag("nothing")
                    Text("Quit MacDM").tag("quit")
                    Text("Put the Mac to sleep").tag("sleep")
                }
            }
            HStack {
                Text(manager.queueRunning ? "The main queue is running." : "The main queue is stopped.")
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Start Now") { manager.startQueue() }.disabled(manager.queueRunning)
                Button("Stop") { manager.stopQueue() }.disabled(!manager.queueRunning)
            }
        }
        .formStyle(.grouped)
    }

    /// Bridges a stored minute-of-day to a DatePicker's Date.
    private func time(_ minutes: Binding<Int>) -> Binding<Date> {
        Binding(
            get: { Calendar.current.date(bySettingHour: minutes.wrappedValue / 60, minute: minutes.wrappedValue % 60, second: 0, of: Date()) ?? Date() },
            set: {
                let parts = Calendar.current.dateComponents([.hour, .minute], from: $0)
                minutes.wrappedValue = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            }
        )
    }
}

@MainActor
private final class DayStore: ObservableObject {
    func binding(_ weekday: Int) -> Binding<Bool> {
        Binding(
            get: { (UserDefaults.standard.array(forKey: Prefs.schedulerDays) as? [Int] ?? []).contains(weekday) },
            set: { on in
                self.objectWillChange.send()
                var days = Set(UserDefaults.standard.array(forKey: Prefs.schedulerDays) as? [Int] ?? [])
                if on { days.insert(weekday) } else { days.remove(weekday) }
                UserDefaults.standard.set(days.sorted(), forKey: Prefs.schedulerDays)
            }
        )
    }
}

private struct QueueFilesTab: View {
    @EnvironmentObject private var manager: DownloadManager

    var body: some View {
        let queued = manager.items(in: SidebarNode(scope: .queue, category: nil, children: nil))
        VStack(alignment: .leading) {
            if queued.isEmpty {
                Text("No files in the main queue. Use \"Download Later\", or right-click a download and choose Add to Main Queue.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(queued) { item in
                    QueueRow(item: item)
                }
            }
        }
        .padding()
    }
}

private struct QueueRow: View {
    @ObservedObject var item: DownloadItem
    @EnvironmentObject private var manager: DownloadManager

    var body: some View {
        HStack {
            Image(nsImage: fileIcon(for: item.displayName)).resizable().frame(width: 16, height: 16)
            Text(item.displayName).lineLimit(1).truncationMode(.middle)
            Spacer()
            Text(statusText(item)).foregroundStyle(.secondary).monospacedDigit()
            Button("Remove") { manager.setQueued(item, false) }.buttonStyle(.borderless)
        }
    }
}
