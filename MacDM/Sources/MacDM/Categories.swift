import Foundation

/// File categories used by the sidebar and by per-category save folders.
enum FileCategory: String, CaseIterable, Codable, Identifiable {
    case general, compressed, documents, music, programs, video

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "General"
        case .compressed: "Compressed"
        case .documents: "Documents"
        case .music: "Music"
        case .programs: "Programs"
        case .video: "Video"
        }
    }

    var symbol: String {
        switch self {
        case .general: "doc"
        case .compressed: "archivebox"
        case .documents: "doc.text"
        case .music: "music.note"
        case .programs: "app.badge"
        case .video: "film"
        }
    }

    var extensions: Set<String> {
        switch self {
        case .general: []
        case .compressed: ["zip", "rar", "7z", "gz", "tgz", "bz2", "xz", "tar", "sit", "sitx", "arj", "lzh", "z", "dmg", "iso", "img"]
        case .documents: ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "txt", "epub", "pages", "numbers", "key", "odt", "csv"]
        case .music: ["mp3", "wav", "wma", "aac", "aif", "aiff", "m4a", "flac", "ogg", "opus", "mid", "midi"]
        case .programs: ["exe", "msi", "pkg", "mpkg", "app", "apk", "deb", "rpm", "bin", "jar"]
        case .video: ["mp4", "m4v", "mkv", "mov", "avi", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts", "ogv", "asf", "rm", "qt"]
        }
    }

    static func of(filename: String) -> FileCategory {
        let ext = (filename as NSString).pathExtension.lowercased()
        return allCases.first { $0.extensions.contains(ext) } ?? .general
    }
}

/// A node in the sidebar tree.
struct SidebarNode: Identifiable, Hashable {
    enum Scope: Hashable { case all, unfinished, finished, queue }

    let scope: Scope
    let category: FileCategory?
    let children: [SidebarNode]?

    var id: String { "\(scope)-\(category?.rawValue ?? "root")" }

    var title: String {
        if let category { return category.title }
        switch scope {
        case .all: return "All Downloads"
        case .unfinished: return "Unfinished"
        case .finished: return "Finished"
        case .queue: return "Main Queue"
        }
    }

    var symbol: String {
        if let category { return category.symbol }
        switch scope {
        case .all: return "tray.full"
        case .unfinished: return "arrow.down.circle"
        case .finished: return "checkmark.circle"
        case .queue: return "list.bullet.rectangle"
        }
    }

    static let tree: [SidebarNode] = [.all, .unfinished, .finished].map { scope in
        SidebarNode(scope: scope, category: nil,
                    children: FileCategory.allCases.filter { $0 != .general }.map { SidebarNode(scope: scope, category: $0, children: nil) })
    } + [SidebarNode(scope: .queue, category: nil, children: nil)]

    static let allDownloads = tree[0]
}
