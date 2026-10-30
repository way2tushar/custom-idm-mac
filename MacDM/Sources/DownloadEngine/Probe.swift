import Foundation
import UniformTypeIdentifiers

public struct ProbeResult: Sendable {
    public var finalURL: URL
    public var totalBytes: Int64?
    public var acceptsRanges: Bool
    public var filename: String
    public var etag: String?
    public var lastModified: String?
}

/// Learns size, range support and filename with a one-byte ranged GET.
/// A GET is used instead of HEAD because many CDNs answer HEAD differently from GET.
enum Probe {
    static func run(_ request: DownloadRequest) async throws -> ProbeResult {
        var urlRequest = URLRequest(url: request.url)
        apply(request.headers, to: &urlRequest)
        urlRequest.setValue("bytes=0-0", forHTTPHeaderField: "Range")

        let session = URLSession(configuration: .macdm)
        defer { session.finishTasksAndInvalidate() }
        let (bytes, response) = try await session.bytes(for: urlRequest)
        bytes.task.cancel()

        guard let http = response as? HTTPURLResponse else { throw DownloadError.badStatus(0) }
        guard (200..<300).contains(http.statusCode) else { throw DownloadError.badStatus(http.statusCode) }

        var total: Int64?
        var ranges = false
        if http.statusCode == 206, let contentRange = http.value(forHTTPHeaderField: "Content-Range") {
            total = parseTotal(contentRange: contentRange)
            ranges = total != nil
        } else if http.expectedContentLength > 0 {
            total = http.expectedContentLength
        }

        let finalURL = http.url ?? request.url
        let name = request.filename.flatMap(sanitize)
            ?? http.value(forHTTPHeaderField: "Content-Disposition").flatMap(parseDispositionFilename).flatMap(sanitize)
            ?? sanitize(finalURL.lastPathComponent.removingPercentEncoding ?? finalURL.lastPathComponent)
            ?? "download"

        return ProbeResult(
            finalURL: finalURL,
            totalBytes: total,
            acceptsRanges: ranges,
            filename: withExtension(name, mimeType: http.mimeType),
            etag: http.value(forHTTPHeaderField: "ETag"),
            lastModified: http.value(forHTTPHeaderField: "Last-Modified")
        )
    }

    /// "bytes 0-0/12345" -> 12345; "bytes 0-0/*" -> nil.
    static func parseTotal(contentRange: String) -> Int64? {
        guard let slash = contentRange.lastIndex(of: "/") else { return nil }
        return Int64(contentRange[contentRange.index(after: slash)...].trimmingCharacters(in: .whitespaces))
    }

    static func parseDispositionFilename(_ header: String) -> String? {
        for part in header.split(separator: ";") {
            let pair = part.trimmingCharacters(in: .whitespaces)
            if pair.lowercased().hasPrefix("filename*=") {
                // RFC 5987: charset'lang'percent-encoded
                let value = pair.dropFirst("filename*=".count)
                if let quote = value.range(of: "''") {
                    return String(value[quote.upperBound...]).trimmingCharacters(in: CharacterSet(charactersIn: "\"")).removingPercentEncoding
                }
            }
        }
        for part in header.split(separator: ";") {
            let pair = part.trimmingCharacters(in: .whitespaces)
            if pair.lowercased().hasPrefix("filename=") {
                return String(pair.dropFirst("filename=".count)).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            }
        }
        return nil
    }

    static func sanitize(_ name: String) -> String? {
        let cleaned = name
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: ":", with: "_")
            .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        return cleaned.isEmpty ? nil : String(cleaned.prefix(240))
    }

    static func withExtension(_ name: String, mimeType: String?) -> String {
        guard (name as NSString).pathExtension.isEmpty,
              let mimeType, mimeType != "application/octet-stream",
              let ext = UTType(mimeType: mimeType)?.preferredFilenameExtension
        else { return name }
        return "\(name).\(ext)"
    }
}

func apply(_ headers: [String: String], to request: inout URLRequest) {
    for (name, value) in headers where name.lowercased() != "range" {
        request.setValue(value, forHTTPHeaderField: name)
    }
}

extension URLSessionConfiguration {
    static var macdm: URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.urlCache = nil
        // Cookies come from the browser in the Cookie header; don't let URLSession replace them.
        config.httpShouldSetCookies = false
        config.httpCookieAcceptPolicy = .never
        config.timeoutIntervalForRequest = 30
        config.httpMaximumConnectionsPerHost = 32
        if let proxy = ProxySettings.current {
            config.connectionProxyDictionary = [
                "HTTPEnable": 1, "HTTPProxy": proxy.host, "HTTPPort": proxy.port,
                "HTTPSEnable": 1, "HTTPSProxy": proxy.host, "HTTPSPort": proxy.port,
            ]
        }
        return config
    }
}
