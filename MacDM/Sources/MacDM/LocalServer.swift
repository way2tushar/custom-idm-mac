import DownloadEngine
import Foundation
import Network

/// Minimal HTTP/1.1 endpoint on 127.0.0.1 that the browser extension posts downloads to.
///
///   GET  /ping      -> {"app":"MacDM","version":"1.0","capture":true,"grabber":true,
///                       "types":[...],"excludedSites":[...],"excludedAddresses":[...]}
///                      (the app's switches and File Types rules for the extensions)
///   POST /download  {"url": "...", "filename": "...", "referrer": "...", "cookies": "...", "userAgent": "...",
///                    "kind": "file" | "hls" | "dash", "variant": <BANDWIDTH of the chosen quality>,
///                    "auto": true for automatic captures (answered 409 when excluded)}
///
/// Only loopback connections are accepted, and browser requests must come from an extension
/// origin, so ordinary web pages cannot push downloads into the app.
final class LocalServer: @unchecked Sendable {
    static let port: UInt16 = 9614

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "macdm.server")
    private let onDownload: @Sendable (DownloadRequest) -> Void

    init(onDownload: @escaping @Sendable (DownloadRequest) -> Void) {
        self.onDownload = onDownload
    }

    func start() {
        guard listener == nil else { return }
        do {
            let parameters = NWParameters.tcp
            parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: Self.port)!)
            parameters.allowLocalEndpointReuse = true
            let listener = try NWListener(using: parameters)
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
            self.listener = listener
        } catch {
            NSLog("MacDM: could not start browser bridge: \(error)")
        }
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: queue)
        receive(on: connection, buffer: Data())
    }

    private func receive(on connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            if buffer.count > 1_000_000 || error != nil {
                connection.cancel()
                return
            }
            if let request = HTTPRequest(buffer) {
                self.respond(to: request, on: connection)
            } else if isComplete {
                connection.cancel()
            } else {
                self.receive(on: connection, buffer: buffer)
            }
        }
    }

    private func respond(to request: HTTPRequest, on connection: NWConnection) {
        let origin = request.headers["origin"]
        let trusted = origin == nil || origin!.hasPrefix("chrome-extension://") || origin!.hasPrefix("moz-extension://")
        var response: (Int, String)

        if !trusted {
            response = (403, #"{"error":"forbidden origin"}"#)
        } else if request.method == "OPTIONS" {
            response = (204, "")
        } else if request.method == "GET", request.path == "/ping" {
            let defaults = UserDefaults.standard
            var status: [String: Any] = [
                "app": "MacDM", "version": "1.0",
                "capture": defaults.object(forKey: Prefs.browserCapture) as? Bool ?? true,
                "grabber": defaults.object(forKey: Prefs.videoGrabber) as? Bool ?? true,
            ]
            status.merge(CaptureRules.current.json) { current, _ in current }
            let body = (try? JSONSerialization.data(withJSONObject: status, options: [.sortedKeys])) ?? Data()
            response = (200, String(decoding: body, as: UTF8.self))
        } else if request.method == "POST", request.path == "/download" {
            if let payload = try? JSONDecoder().decode(BridgePayload.self, from: request.body),
               let url = URL(string: payload.url), ["http", "https"].contains(url.scheme?.lowercased()) {
                // Automatic captures respect Options → File Types exclusions; the browser keeps those.
                if payload.auto == true && !CaptureRules.current.allows(url) {
                    response = (409, #"{"accepted":false,"excluded":true}"#)
                } else {
                    onDownload(payload.downloadRequest(url: url))
                    response = (202, #"{"accepted":true}"#)
                }
            } else {
                response = (400, #"{"error":"expected JSON with an http(s) url"}"#)
            }
        } else {
            response = (404, #"{"error":"not found"}"#)
        }

        var head = "HTTP/1.1 \(response.0) \(response.0 < 300 ? "OK" : "Error")\r\n"
        head += "Content-Type: application/json\r\nConnection: close\r\nContent-Length: \(response.1.utf8.count)\r\n"
        if trusted, let origin {
            head += "Access-Control-Allow-Origin: \(origin)\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Methods: GET, POST\r\n"
        }
        head += "\r\n"
        connection.send(content: Data((head + response.1).utf8), completion: .contentProcessed { _ in connection.cancel() })
    }
}

private struct BridgePayload: Decodable {
    var url: String
    var filename: String?
    var referrer: String?
    var cookies: String?
    var userAgent: String?
    var kind: String?
    var variant: Int?
    /// True when the extension captured this on its own, rather than the user picking "Download with MacDM".
    var auto: Bool?

    func downloadRequest(url: URL) -> DownloadRequest {
        var headers: [String: String] = [:]
        if let referrer, !referrer.isEmpty { headers["Referer"] = referrer }
        if let cookies, !cookies.isEmpty { headers["Cookie"] = cookies }
        if let userAgent, !userAgent.isEmpty { headers["User-Agent"] = userAgent }
        let name = filename.map { ($0 as NSString).lastPathComponent }.flatMap { $0.isEmpty ? nil : $0 }
        return DownloadRequest(url: url, headers: headers, filename: name, kind: kind.flatMap(DownloadRequest.Kind.init(rawValue:)), variantBandwidth: variant)
    }
}

/// Parses one complete request (headers plus Content-Length body), or returns nil if more bytes are needed.
private struct HTTPRequest {
    var method: String
    var path: String
    var headers: [String: String]
    var body: Data

    init?(_ data: Data) {
        guard let separator = data.range(of: Data("\r\n\r\n".utf8)),
              let head = String(data: data[..<separator.lowerBound], encoding: .utf8) else { return nil }
        let lines = head.components(separatedBy: "\r\n")
        let requestLine = lines[0].split(separator: " ")
        guard requestLine.count >= 2 else { return nil }
        method = String(requestLine[0])
        path = String(requestLine[1].split(separator: "?").first ?? "")
        headers = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        let length = Int(headers["content-length"] ?? "0") ?? 0
        let bodyStart = separator.upperBound
        guard data.count - bodyStart >= length else { return nil }
        body = data[bodyStart..<(bodyStart + length)]
    }
}
