import AppKit
import SwiftUI

/// Runs the last scheduled block after a short pause. Used for autosave.
@MainActor
final class DebouncedSaver {
    private var work: DispatchWorkItem?
    private let delay: TimeInterval

    init(delay: TimeInterval = 0.6) { self.delay = delay }

    func schedule(_ block: @escaping () -> Void) {
        work?.cancel()
        let item = DispatchWorkItem(block: block)
        work = item
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: item)
    }

    /// Runs a pending save immediately (e.g. when the module disappears).
    func flush() {
        guard let item = work else { return }
        work = nil
        item.perform()
        item.cancel()
    }
}

/// Shown when a module needs an API key or setting that has not been entered yet.
struct MissingSettingView: View {
    let icon: String
    let title: String
    let subtitle: String

    var body: some View {
        VStack(spacing: 12) {
            EmptyState(icon: icon, title: title, subtitle: subtitle)
                .frame(maxHeight: 150)
            PillButton(title: "Open Settings", icon: "gearshape", prominent: true) {
                NotchState.shared.showSettings = true
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Minimal JSON-over-HTTP helper for the integration modules.
enum HTTPJSON {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    static func get(_ url: URL, bearer: String) async throws -> Any {
        var request = URLRequest(url: url, timeoutInterval: 30)
        request.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return try await send(request)
    }

    static func post(_ url: URL, headers: [String: String], body: [String: Any],
                     timeout: TimeInterval = 120) async throws -> Any {
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await send(request)
    }

    static func send(_ request: URLRequest) async throws -> Any {
        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        let json = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        guard (200..<300).contains(status) else {
            throw Failure(message: errorMessage(json, data: data, status: status))
        }
        guard let json else { throw Failure(message: "Unexpected response from server.") }
        return json
    }

    /// Pulls a human readable message out of the common API error shapes.
    static func errorMessage(_ json: Any?, data: Data, status: Int) -> String {
        if let dict = json as? [String: Any] {
            if let error = dict["error"] as? [String: Any], let message = error["message"] as? String {
                return "\(message) (HTTP \(status))"
            }
            if let error = dict["error"] as? String { return "\(error) (HTTP \(status))" }
            if let message = dict["message"] as? String { return "\(message) (HTTP \(status))" }
        }
        if let first = json as? [[String: Any]], let error = first.first?["error"] as? [String: Any],
           let message = error["message"] as? String {
            return "\(message) (HTTP \(status))"
        }
        let text = String(data: data.prefix(200), encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? "Request failed (HTTP \(status))." : "Request failed (HTTP \(status)): \(text)"
    }
}

enum CompactNumber {
    /// 842_200_000 -> "842.2M"
    static func string(_ value: Double) -> String {
        let v = abs(value)
        let sign = value < 0 ? "-" : ""
        func f(_ x: Double, _ suffix: String) -> String {
            let s = x >= 100 ? String(format: "%.0f", x) : String(format: "%.1f", x)
            return sign + (s.hasSuffix(".0") ? String(s.dropLast(2)) : s) + suffix
        }
        if v >= 1e12 { return f(v / 1e12, "T") }
        if v >= 1e9 { return f(v / 1e9, "B") }
        if v >= 1e6 { return f(v / 1e6, "M") }
        if v >= 1e3 { return f(v / 1e3, "K") }
        return sign + String(Int(v))
    }
}
