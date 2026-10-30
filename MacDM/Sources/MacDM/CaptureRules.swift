import Foundation

/// Which browser downloads MacDM takes over automatically (Options → File Types).
/// Links sent explicitly ("Download with MacDM", the video button) are never filtered.
struct CaptureRules {
    static let defaultTypes = """
        3gp 7z aac aif aiff apk arj asf avi bin bz2 deb dmg epub exe flac flv gz img iso m4a m4b m4v mkv mov \
        mp3 mp4 mpeg mpg msi ogg ogv opus pdf pkg rar rpm tar tgz tif tiff wav webm wma wmv xz z zip
        """
    static let defaultSites = "*.icloud.com"

    var types: Set<String>
    var sites: [String]
    var addresses: [String]

    static var current: CaptureRules {
        let defaults = UserDefaults.standard
        return CaptureRules(
            types: Set(words(defaults.string(forKey: Prefs.captureTypes) ?? defaultTypes).map { $0.lowercased() }),
            sites: words(defaults.string(forKey: Prefs.excludedSites) ?? defaultSites),
            addresses: defaults.stringArray(forKey: Prefs.excludedAddresses) ?? []
        )
    }

    static func words(_ text: String) -> [String] {
        text.split(whereSeparator: { $0.isWhitespace || $0 == "," }).map(String.init)
    }

    /// False when the address's site or the address itself is on an exclusion list.
    func allows(_ url: URL) -> Bool {
        let host = url.host?.lowercased() ?? ""
        let address = url.absoluteString
        return !sites.contains { Self.matches(site: $0.lowercased(), host: host) }
            && !addresses.contains { Self.wildcard($0).firstMatch(in: address, range: NSRange(address.startIndex..., in: address)) != nil }
    }

    /// "*.example.com" covers example.com and its subdomains; a plain name covers itself and subdomains.
    static func matches(site: String, host: String) -> Bool {
        let bare = site.hasPrefix("*.") ? String(site.dropFirst(2)) : site
        if !bare.contains("*") { return host == bare || host.hasSuffix("." + bare) }
        return wildcard(site).firstMatch(in: host, range: NSRange(host.startIndex..., in: host)) != nil
    }

    /// Whole-string match where `*` stands for any run of characters.
    static func wildcard(_ pattern: String) -> NSRegularExpression {
        let escaped = NSRegularExpression.escapedPattern(for: pattern).replacingOccurrences(of: "\\*", with: ".*")
        return (try? NSRegularExpression(pattern: "^\(escaped)$", options: .caseInsensitive))
            ?? NSRegularExpression()
    }

    /// Sent to the extensions in /ping.
    var json: [String: Any] {
        ["types": types.sorted(), "excludedSites": sites, "excludedAddresses": addresses]
    }
}
