import Foundation

/// Reads a static (on-demand) MPEG-DASH manifest into per-track segment lists.
/// Supports SegmentTemplate (with or without SegmentTimeline), SegmentList and single-file
/// representations (SegmentBase / BaseURL). Live (`type="dynamic"`) and DRM-protected
/// manifests are rejected. Only the first Period is used.
enum DASH {
    struct Representation {
        let id: String
        let bandwidth: Int
        let width: Int
        let height: Int
        let isVideo: Bool
        let mimeType: String
        let playlist: () throws -> MediaPlaylist
    }

    static func looksLikeManifest(_ text: String) -> Bool {
        text.prefix(4000).contains("<MPD")
    }

    /// The chosen video (by bandwidth, else the highest) and the best audio track.
    static func tracks(_ data: Data, base: URL, videoBandwidth: Int?) throws -> (video: MediaPlaylist, audio: MediaPlaylist?) {
        let representations = try parse(data, base: base)
        let videos = representations.filter(\.isVideo)
        let audios = representations.filter { !$0.isVideo }
        let video = videoBandwidth.flatMap { bandwidth in videos.first { $0.bandwidth == bandwidth } }
            ?? videos.max { ($0.height, $0.bandwidth) < ($1.height, $1.bandwidth) }
        let audio = audios.max { $0.bandwidth < $1.bandwidth }
        guard let main = video ?? audio else { throw HLSError.empty }
        return (try main.playlist(), video != nil ? try audio?.playlist() : nil)
    }

    static func parse(_ data: Data, base: URL) throws -> [Representation] {
        let document = try XMLDocument(data: data)
        guard let mpd = document.rootElement(), mpd.localName == "MPD" else { throw HLSError.notAPlaylist }
        if mpd.attr("type") == "dynamic" { throw HLSError.live }
        if mpd.descendants(named: "ContentProtection").first != nil {
            throw HLSError.unsupportedEncryption("DRM")
        }
        guard let period = mpd.children(named: "Period").first else { throw HLSError.empty }
        let duration = (period.attr("duration") ?? mpd.attr("mediaPresentationDuration")).flatMap(seconds) ?? 0
        let periodBase = resolve(period, against: resolve(mpd, against: base))

        var result: [Representation] = []
        for set in period.children(named: "AdaptationSet") {
            let setBase = resolve(set, against: periodBase)
            for rep in set.children(named: "Representation") {
                let mime = rep.attr("mimeType") ?? set.attr("mimeType") ?? ""
                let type = set.attr("contentType") ?? String(mime.split(separator: "/").first ?? "")
                guard type == "video" || type == "audio" else { continue }
                let repBase = resolve(rep, against: setBase)
                let id = rep.attr("id") ?? ""
                let bandwidth = Int(rep.attr("bandwidth") ?? "") ?? 0
                let container = mime.contains("webm") ? "webm" : "mp4"
                result.append(Representation(
                    id: id, bandwidth: bandwidth,
                    width: Int(rep.attr("width") ?? set.attr("width") ?? "") ?? 0,
                    height: Int(rep.attr("height") ?? set.attr("height") ?? "") ?? 0,
                    isVideo: type == "video", mimeType: mime,
                    playlist: {
                        var playlist = try segments(rep: rep, set: set, period: period, base: repBase,
                                                    id: id, bandwidth: bandwidth, duration: duration)
                        playlist.container = container
                        playlist.isLive = false
                        return playlist
                    }
                ))
            }
        }
        return result
    }

    // MARK: Segment addressing

    private static func segments(rep: XMLElement, set: XMLElement, period: XMLElement, base: URL,
                                 id: String, bandwidth: Int, duration: Double) throws -> MediaPlaylist {
        // Representation-level settings override AdaptationSet-level ones, which override Period-level.
        let levels = [rep, set, period]
        if let templates = Optional(levels.compactMap { $0.children(named: "SegmentTemplate").first }), !templates.isEmpty {
            return try template(templates, base: base, id: id, bandwidth: bandwidth, duration: duration)
        }
        if let list = levels.lazy.compactMap({ $0.children(named: "SegmentList").first }).first {
            return segmentList(list, base: base)
        }
        // Single file per representation (SegmentBase or just a BaseURL): the whole file is the track.
        return MediaPlaylist(segments: [.init(url: base, key: nil, sequence: 0, range: nil)])
    }

    /// `templates` is ordered most specific first; each attribute comes from the first that has it.
    private static func template(_ templates: [XMLElement], base: URL, id: String, bandwidth: Int,
                                 duration: Double) throws -> MediaPlaylist {
        func attr(_ name: String) -> String? { templates.lazy.compactMap { $0.attr(name) }.first }
        guard let media = attr("media") else { throw HLSError.empty }
        let timescale = Double(attr("timescale") ?? "") ?? 1
        let startNumber = Int(attr("startNumber") ?? "") ?? 1
        var playlist = MediaPlaylist()
        if let initialization = attr("initialization") {
            playlist.initSection = URL(string: expand(initialization, id: id, bandwidth: bandwidth, number: 0, time: 0), relativeTo: base)?.absoluteURL
        }

        var entries: [(number: Int, time: Int64)] = []
        if let timeline = templates.lazy.compactMap({ $0.children(named: "SegmentTimeline").first }).first {
            var time: Int64 = 0
            var number = startNumber
            let end = Int64(duration * timescale)
            let items = timeline.children(named: "S")
            for (index, item) in items.enumerated() {
                if let t = item.attr("t").flatMap(Int64.init) { time = t }
                guard let d = item.attr("d").flatMap(Int64.init), d > 0 else { continue }
                var repeats = Int(item.attr("r") ?? "0") ?? 0
                if repeats < 0 {
                    // Repeat until the next S element's start, or the end of the period.
                    let stop = index + 1 < items.count ? items[index + 1].attr("t").flatMap(Int64.init) ?? end : end
                    repeats = max(0, Int((stop - time + d - 1) / d) - 1)
                }
                for _ in 0...repeats {
                    entries.append((number, time))
                    time += d
                    number += 1
                }
            }
        } else if let segmentDuration = Double(attr("duration") ?? ""), segmentDuration > 0, duration > 0 {
            let count = Int((duration * timescale / segmentDuration).rounded(.up))
            entries = (0..<count).map { (startNumber + $0, Int64(Double($0) * segmentDuration)) }
        }
        guard !entries.isEmpty else { throw HLSError.empty }

        playlist.segments = entries.enumerated().compactMap { index, entry in
            URL(string: expand(media, id: id, bandwidth: bandwidth, number: entry.number, time: entry.time), relativeTo: base)
                .map { .init(url: $0.absoluteURL, key: nil, sequence: index, range: nil) }
        }
        return playlist
    }

    private static func segmentList(_ list: XMLElement, base: URL) -> MediaPlaylist {
        var playlist = MediaPlaylist()
        if let initialization = list.children(named: "Initialization").first {
            playlist.initSection = URL(string: initialization.attr("sourceURL") ?? "", relativeTo: base)?.absoluteURL ?? base
            playlist.initRange = initialization.attr("range").flatMap(range)
        }
        playlist.segments = list.children(named: "SegmentURL").enumerated().compactMap { index, item in
            let url = item.attr("media").flatMap { URL(string: $0, relativeTo: base)?.absoluteURL } ?? base
            return .init(url: url, key: nil, sequence: index, range: item.attr("mediaRange").flatMap(range))
        }
        return playlist
    }

    /// Fills $RepresentationID$, $Bandwidth$, $Number$, $Time$ (with optional %0Nd widths) and $$.
    static func expand(_ template: String, id: String, bandwidth: Int, number: Int, time: Int64) -> String {
        var output = ""
        var rest = template[...]
        while let open = rest.firstIndex(of: "$") {
            output += rest[..<open]
            let afterOpen = rest.index(after: open)
            guard let close = rest[afterOpen...].firstIndex(of: "$") else { output += rest[open...]; return output }
            let token = rest[afterOpen..<close]
            let parts = token.split(separator: "%", maxSplits: 1)
            let name = parts.first.map(String.init) ?? ""
            let width = parts.count > 1 ? Int(parts[1].filter(\.isNumber)) ?? 0 : 0
            func padded(_ value: Int64) -> String {
                let digits = String(value)
                return String(repeating: "0", count: max(0, width - digits.count)) + digits
            }
            switch name {
            case "": output += "$"
            case "RepresentationID": output += id
            case "Bandwidth": output += padded(Int64(bandwidth))
            case "Number": output += padded(Int64(number))
            case "Time": output += padded(time)
            default: output += "$\(token)$"
            }
            rest = rest[rest.index(after: close)...]
        }
        return output + rest
    }

    /// ISO 8601 durations like "PT1H2M3.5S" or "P1DT2H".
    static func seconds(_ text: String) -> Double? {
        guard text.hasPrefix("P") else { return nil }
        var total = 0.0
        var number = ""
        var inTime = false
        for character in text.dropFirst() {
            switch character {
            case "T": inTime = true
            case "D": total += (Double(number) ?? 0) * 86400; number = ""
            case "H": total += (Double(number) ?? 0) * 3600; number = ""
            case "M": total += (Double(number) ?? 0) * (inTime ? 60 : 2_592_000); number = ""
            case "S": total += Double(number) ?? 0; number = ""
            default: number.append(character)
            }
        }
        return total
    }

    static func range(_ text: String) -> ClosedRange<Int64>? {
        let parts = text.split(separator: "-").compactMap { Int64($0) }
        return parts.count == 2 && parts[0] <= parts[1] ? parts[0]...parts[1] : nil
    }

    private static func resolve(_ element: XMLElement, against base: URL) -> URL {
        guard let text = element.children(named: "BaseURL").first?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty, let url = URL(string: text, relativeTo: base) else { return base }
        return url.absoluteURL
    }
}

private extension XMLElement {
    func attr(_ name: String) -> String? { attribute(forName: name)?.stringValue }

    func children(named name: String) -> [XMLElement] {
        (children ?? []).compactMap { $0 as? XMLElement }.filter { $0.localName == name }
    }

    func descendants(named name: String) -> [XMLElement] {
        children(named: name) + (children ?? []).compactMap { $0 as? XMLElement }.flatMap { $0.descendants(named: name) }
    }
}
