import AppKit
import Foundation
import SafariServices

/// Native half of the Safari extension. The page scripts call
/// `browser.runtime.sendNativeMessage`, and this forwards to the MacDM app:
/// first over the loopback bridge, and if the app isn't running, by opening a
/// `macdm://download` link, which launches it (cookies are dropped on that path).
final class SafariWebExtensionHandler: NSObject, NSExtensionRequestHandling {
    private static let bridge = URL(string: "http://127.0.0.1:9614")!

    func beginRequest(with context: NSExtensionContext) {
        let item = context.inputItems.first as? NSExtensionItem
        let message = item?.userInfo?[SFExtensionMessageKey] as? [String: Any] ?? [:]

        switch message["type"] as? String {
        case "status":
            ping { alive, switches in Self.reply(context, ["alive": alive].merging(switches) { a, _ in a }) }
        case "download":
            guard message["url"] is String else {
                Self.reply(context, ["accepted": false])
                return
            }
            post(message) { accepted, excluded in
                if accepted || excluded {
                    Self.reply(context, ["accepted": accepted])
                } else {
                    let launched = Self.launchApp(message)
                    Self.reply(context, ["accepted": launched])
                }
            }
        default:
            Self.reply(context, ["error": "unknown message"])
        }
    }

    /// Whether the app answers, plus its capture/grabber switches.
    private func ping(_ done: @escaping (Bool, [String: Any]) -> Void) {
        var request = URLRequest(url: Self.bridge.appendingPathComponent("ping"))
        request.timeoutInterval = 1
        URLSession.shared.dataTask(with: request) { data, response, _ in
            let alive = (response as? HTTPURLResponse)?.statusCode == 200
            let body = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] } ?? [:]
            done(alive, body.filter { ["capture", "grabber", "types"].contains($0.key) })
        }.resume()
    }

    /// Reports (accepted, excluded by the app's File Types rules).
    private func post(_ message: [String: Any], _ done: @escaping (Bool, Bool) -> Void) {
        var request = URLRequest(url: Self.bridge.appendingPathComponent("download"))
        request.httpMethod = "POST"
        request.timeoutInterval = 2
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let fields = ["url", "filename", "referrer", "cookies", "userAgent", "kind", "variant", "auto"]
        request.httpBody = try? JSONSerialization.data(withJSONObject: message.filter { fields.contains($0.key) })
        URLSession.shared.dataTask(with: request) { _, response, _ in
            let status = (response as? HTTPURLResponse)?.statusCode
            done(status == 202, status == 409)
        }.resume()
    }

    /// Carries everything except cookies and the user agent, which don't belong in a URL.
    private static func launchApp(_ message: [String: Any]) -> Bool {
        var components = URLComponents(string: "macdm://download")!
        components.queryItems = ["url", "referrer", "filename", "kind", "variant"].compactMap { name in
            guard let value = message[name], !(value is NSNull) else { return nil }
            return URLQueryItem(name: name, value: "\(value)")
        }
        guard let link = components.url else { return false }
        return NSWorkspace.shared.open(link)
    }

    private static func reply(_ context: NSExtensionContext, _ payload: [String: Any]) {
        let response = NSExtensionItem()
        response.userInfo = [SFExtensionMessageKey: payload]
        context.completeRequest(returningItems: [response], completionHandler: nil)
    }
}
