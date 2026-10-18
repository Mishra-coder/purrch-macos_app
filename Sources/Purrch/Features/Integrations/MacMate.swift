import AppKit
import Darwin
import IOKit.ps
import SwiftUI
#if canImport(FoundationModels)
import FoundationModels
#endif

struct MacMateMessage: Identifiable, Equatable {
    enum Role { case user, assistant, notice }
    let id = UUID()
    let role: Role
    let text: String
}

@MainActor
final class MacMateService: ObservableObject {
    static let shared = MacMateService()

    @Published private(set) var messages: [MacMateMessage] = []
    @Published private(set) var isThinking = false

    private var task: Task<Void, Never>?
    /// Holds a `LanguageModelSession` on macOS 26+ so on-device chats keep their context.
    private var appleSession: AnyObject?

    private var settings: AppSettings { AppSettings.shared }

    func clear() {
        task?.cancel()
        task = nil
        isThinking = false
        messages = []
        appleSession = nil
    }

    func send(_ raw: String) {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !isThinking else { return }
        let history = messages.filter { $0.role != .notice }
        messages.append(MacMateMessage(role: .user, text: text))
        isThinking = true
        let provider = settings.macMateProvider
        task = Task { [weak self] in
            guard let self else { return }
            let system = await Self.systemPrompt()
            do {
                let reply: String
                switch provider {
                case .apple: reply = try await self.askApple(system: system, prompt: text)
                case .claude: reply = try await self.askClaude(system: system, history: history, prompt: text)
                case .openai: reply = try await self.askOpenAI(system: system, history: history, prompt: text)
                case .gemini: reply = try await self.askGemini(system: system, history: history, prompt: text)
                }
                guard !Task.isCancelled else { return }
                let trimmed = reply.trimmingCharacters(in: .whitespacesAndNewlines)
                self.messages.append(MacMateMessage(role: trimmed.isEmpty ? .notice : .assistant,
                                                    text: trimmed.isEmpty ? "No answer came back. Try again." : trimmed))
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled else { return }
                self.messages.append(MacMateMessage(role: .notice, text: error.localizedDescription))
            }
            self.isThinking = false
        }
    }

    /// A single question with no chat history, for other modules such as Translate.
    /// Web search stays off and the on-device model gets a fresh session.
    func complete(provider: AIProvider, system: String, prompt: String) async throws -> String {
        switch provider {
        case .apple: try await askApple(system: system, prompt: prompt, keepContext: false)
        case .claude: try await askClaude(system: system, history: [], prompt: prompt, webSearch: false)
        case .openai: try await askOpenAI(system: system, history: [], prompt: prompt)
        case .gemini: try await askGemini(system: system, history: [], prompt: prompt)
        }
    }

    // MARK: Context

    static func systemPrompt() async -> String {
        let info = await Task.detached { SystemSnapshot.current() }.value
        let front = NSWorkspace.shared.frontmostApplication.flatMap { app -> String? in
            app.bundleIdentifier == Bundle.main.bundleIdentifier ? nil : app.localizedName
        } ?? "unknown"
        return """
        You are MacMate, a concise, friendly help assistant for macOS that lives in the user's notch. \
        Answer questions about using, fixing and tuning their Mac. Prefer short answers: a sentence or two, \
        then numbered steps or exact keyboard shortcuts / Terminal commands when useful. Use Markdown for \
        **bold** and `code` only; no headings or tables. Mention menu paths for the user's macOS version. \
        Warn before anything destructive.

        Live context about this Mac:
        - macOS: \(ProcessInfo.processInfo.operatingSystemVersionString)
        - Model: \(info.model)
        - Memory: \(info.memory)
        - Free disk: \(info.disk)
        - Battery: \(info.battery)
        - Frontmost app: \(front)
        """
    }

    // MARK: Apple on-device

    private func askApple(system: String, prompt: String, keepContext: Bool = true) async throws -> String {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) {
            switch SystemLanguageModel.default.availability {
            case .available:
                break
            case .unavailable(let reason):
                let why: String
                switch reason {
                case .deviceNotEligible: why = "This Mac doesn't support Apple Intelligence."
                case .appleIntelligenceNotEnabled: why = "Apple Intelligence is turned off in System Settings."
                case .modelNotReady: why = "The on-device model is still downloading."
                @unknown default: why = "The on-device model isn't available."
                }
                throw HTTPJSON.Failure(message: "\(why) Pick Claude, OpenAI or Gemini in Settings to use a cloud model.")
            }
            let session: LanguageModelSession
            if keepContext, let existing = appleSession as? LanguageModelSession {
                session = existing
            } else {
                session = LanguageModelSession(instructions: system)
                if keepContext { appleSession = session }
            }
            let response = try await session.respond(to: prompt)
            return response.content
        }
        #endif
        throw HTTPJSON.Failure(message: "On-device Apple Intelligence needs macOS 26 or later. "
            + "Choose Claude, OpenAI or Gemini in Settings to chat with a cloud model.")
    }

    // MARK: Claude

    private func askClaude(system: String, history: [MacMateMessage], prompt: String,
                           webSearch: Bool? = nil) async throws -> String {
        let key = settings.anthropicKey
        var messages: [[String: Any]] = Self.alternating(history, prompt).map {
            ["role": $0.role == .user ? "user" : "assistant", "content": $0.text]
        }
        var body: [String: Any] = [
            "model": "claude-opus-5-5",
            "max_tokens": 16000,
            "system": system,
            "output_config": ["effort": "low"],
            "fallbacks": "default",
        ]
        if webSearch ?? settings.macMateWebSearch {
            body["tools"] = [["type": "web_search_20260209", "name": "web_search", "max_uses": 3]]
        }
        let headers = [
            "x-api-key": key,
            "anthropic-version": "2023-06-01",
            "anthropic-beta": "server-side-fallback-2026-07-01",
        ]
        let url = URL(string: "https://api.anthropic.com/v1/messages")!
        var text = ""
        for _ in 0..<4 {
            body["messages"] = messages
            let json = try await HTTPJSON.post(url, headers: headers, body: body, timeout: 300)
            guard let dict = json as? [String: Any] else { throw HTTPJSON.Failure(message: "Unexpected reply from Claude.") }
            let content = dict["content"] as? [[String: Any]] ?? []
            text += content.filter { $0["type"] as? String == "text" }
                .compactMap { $0["text"] as? String }
                .joined()
            switch dict["stop_reason"] as? String {
            case "refusal":
                return text.isEmpty ? "Claude declined this request. Try rephrasing it." : text
                    + "\n\n_Claude declined to continue this request._"
            case "pause_turn":
                messages.append(["role": "assistant", "content": content])
                continue
            default:
                return text
            }
        }
        return text
    }

    // MARK: OpenAI

    private func askOpenAI(system: String, history: [MacMateMessage], prompt: String) async throws -> String {
        var messages: [[String: Any]] = [["role": "system", "content": system]]
        messages += Self.alternating(history, prompt).map {
            ["role": $0.role == .user ? "user" : "assistant", "content": $0.text]
        }
        let json = try await HTTPJSON.post(
            URL(string: "https://api.openai.com/v1/chat/completions")!,
            headers: ["Authorization": "Bearer \(settings.openAIKey)"],
            body: ["model": "gpt-4o-mini", "messages": messages])
        guard let choices = (json as? [String: Any])?["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any] else {
            throw HTTPJSON.Failure(message: "Unexpected reply from OpenAI.")
        }
        return message["content"] as? String ?? ""
    }

    // MARK: Gemini

    private func askGemini(system: String, history: [MacMateMessage], prompt: String) async throws -> String {
        let contents: [[String: Any]] = Self.alternating(history, prompt).map {
            ["role": $0.role == .user ? "user" : "model", "parts": [["text": $0.text]]]
        }
        // The key travels in a header rather than the URL so it never appears in error strings.
        let json = try await HTTPJSON.post(
            URL(string: "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent")!,
            headers: ["x-goog-api-key": settings.geminiKey],
            body: ["systemInstruction": ["parts": [["text": system]]], "contents": contents])
        guard let candidates = (json as? [String: Any])?["candidates"] as? [[String: Any]],
              let first = candidates.first else {
            throw HTTPJSON.Failure(message: "Gemini returned no answer.")
        }
        let parts = (first["content"] as? [String: Any])?["parts"] as? [[String: Any]] ?? []
        let text = parts.compactMap { $0["text"] as? String }.joined()
        if text.isEmpty, let reason = first["finishReason"] as? String, reason != "STOP" {
            return "Gemini stopped without an answer (\(reason.lowercased()))."
        }
        return text
    }

    /// History as strictly alternating user/assistant turns that start with the user, plus the new prompt.
    private static func alternating(_ history: [MacMateMessage], _ prompt: String) -> [MacMateMessage] {
        var result: [MacMateMessage] = []
        for message in history + [MacMateMessage(role: .user, text: prompt)] {
            if result.isEmpty && message.role != .user { continue }
            if let last = result.last, last.role == message.role {
                result[result.count - 1] = MacMateMessage(role: last.role, text: last.text + "\n\n" + message.text)
            } else {
                result.append(message)
            }
        }
        return result
    }
}

/// A quick read of the machine for the assistant's system prompt.
private struct SystemSnapshot {
    var model: String
    var memory: String
    var disk: String
    var battery: String

    static func current() -> SystemSnapshot {
        SystemSnapshot(model: hwModel(), memory: memory(), disk: disk(), battery: battery())
    }

    private static func hwModel() -> String {
        var size = 0
        guard sysctlbyname("hw.model", nil, &size, nil, 0) == 0, size > 0 else { return "Mac" }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname("hw.model", &buffer, &size, nil, 0) == 0 else { return "Mac" }
        return String(cString: buffer)
    }

    private static func memory() -> String {
        let total = ProcessInfo.processInfo.physicalMemory
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        let totalText = Format.bytes(Int64(total))
        guard result == KERN_SUCCESS else { return "\(totalText) total" }
        let page = UInt64(vm_kernel_page_size)
        let used = (UInt64(stats.active_count) + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)) * page
        return "\(Format.bytes(Int64(min(used, total)))) used of \(totalText)"
    }

    private static func disk() -> String {
        let url = URL(fileURLWithPath: "/")
        guard let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey,
                                                             .volumeTotalCapacityKey]),
              let free = values.volumeAvailableCapacityForImportantUsage else { return "unknown" }
        let total = values.volumeTotalCapacity.map { " of \(Format.bytes(Int64($0)))" } ?? ""
        return "\(Format.bytes(free)) free\(total)"
    }

    private static func battery() -> String {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else {
            return "no battery"
        }
        for source in list {
            guard let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
                  let current = desc[kIOPSCurrentCapacityKey] as? Int,
                  let max = desc[kIOPSMaxCapacityKey] as? Int, max > 0 else { continue }
            let charging = desc[kIOPSIsChargingKey] as? Bool ?? false
            let onAC = (desc[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue
            return "\(current * 100 / max)%" + (charging ? ", charging" : onAC ? ", on power adapter" : ", on battery")
        }
        return "no battery (desktop Mac)"
    }
}

// MARK: - View

struct MacMateModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var service = MacMateService.shared
    @ObservedObject private var settings = AppSettings.shared
    @State private var input = ""
    @FocusState private var inputFocused: Bool

    private static let suggestions = ["Why is my Mac slow?", "Free up disk space",
                                      "Keyboard shortcut for emoji", "How do I show hidden files?"]

    private var missingKey: Bool {
        switch settings.macMateProvider {
        case .apple: false
        case .claude: settings.anthropicKey.isEmpty
        case .openai: settings.openAIKey.isEmpty
        case .gemini: settings.geminiKey.isEmpty
        }
    }

    var body: some View {
        VStack(spacing: 8) {
            header
            if missingKey {
                MissingSettingView(icon: "key", title: "\(settings.macMateProvider.title) needs an API key",
                                   subtitle: "Add your key in Settings, or switch to Apple (on-device).")
            } else if service.messages.isEmpty && !service.isThinking {
                emptyChat
                inputBar
            } else {
                transcript
                inputBar
            }
        }
        .onAppear { inputFocused = true }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Menu {
                Picker("Provider", selection: $settings.macMateProvider) {
                    ForEach(AIProvider.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.inline)
                if settings.macMateProvider == .claude {
                    Divider()
                    Toggle("Web search", isOn: $settings.macMateWebSearch)
                }
            } label: {
                HStack(spacing: 5) {
                    Circle().fill(theme.accent(for: .macmate)).frame(width: 6, height: 6)
                    Text(settings.macMateProvider.title)
                    if settings.macMateProvider == .claude && settings.macMateWebSearch {
                        Image(systemName: "globe")
                    }
                    Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
                }
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(theme.text)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(Capsule().fill(theme.accent(for: .macmate).opacity(0.15)))
                .overlay(Capsule().strokeBorder(theme.accent(for: .macmate).opacity(0.4), lineWidth: 0.7))
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            Spacer()
            if !service.messages.isEmpty {
                PillButton(title: "Clear chat", icon: "trash") { service.clear() }
            }
        }
    }

    private var emptyChat: some View {
        VStack(spacing: 12) {
            EmptyState(icon: "sparkles", title: "Ask anything about your Mac",
                       subtitle: "MacMate knows your macOS version, model, storage and battery.")
                .frame(maxHeight: 110)
            HStack(spacing: 6) {
                ForEach(Self.suggestions, id: \.self) { suggestion in
                    Button { service.send(suggestion) } label: {
                        Text(suggestion)
                            .font(.system(size: 12, weight: .medium))
                            .foregroundStyle(theme.text)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(Capsule().fill(theme.text.opacity(0.08)))
                            .overlay(Capsule().strokeBorder(theme.border.opacity(0.7), lineWidth: 0.7))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 8) {
                    ForEach(service.messages) { message in
                        ChatBubble(message: message).id(message.id)
                    }
                    if service.isThinking {
                        HStack { TypingIndicator(); Spacer() }.id("typing")
                    }
                }
                .padding(.vertical, 2)
            }
            .scrollIndicators(.never)
            .onChange(of: service.messages.count) { _, _ in scrollToEnd(proxy) }
            .onChange(of: service.isThinking) { _, _ in scrollToEnd(proxy) }
            .onAppear { scrollToEnd(proxy) }
        }
    }

    private func scrollToEnd(_ proxy: ScrollViewProxy) {
        withAnimation(.easeOut(duration: 0.2)) {
            if service.isThinking {
                proxy.scrollTo("typing", anchor: .bottom)
            } else if let last = service.messages.last {
                proxy.scrollTo(last.id, anchor: .bottom)
            }
        }
    }

    private var inputBar: some View {
        HStack(spacing: 8) {
            SearchField(text: $input, placeholder: "Ask MacMate…", icon: "sparkles", onSubmit: submit)
                .focused($inputFocused)
            IconButton(systemName: service.isThinking ? "ellipsis" : "arrow.up", size: 32, prominent: true,
                       help: "Send", action: submit)
                .disabled(service.isThinking)
        }
    }

    private func submit() {
        guard !service.isThinking, !input.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        service.send(input)
        input = ""
    }
}

private struct ChatBubble: View {
    @Environment(\.theme) private var theme
    let message: MacMateMessage

    private var rendered: AttributedString {
        (try? AttributedString(markdown: message.text,
                               options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(message.text)
    }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            if message.role == .user { Spacer(minLength: 120) }
            Group {
                switch message.role {
                case .user:
                    Text(message.text)
                        .foregroundStyle(theme.background)
                        .padding(.horizontal, 11)
                        .padding(.vertical, 7)
                        .background(bubble(theme.accent(for: .macmate)))
                case .assistant:
                    Text(rendered)
                        .foregroundStyle(theme.text)
                        .padding(.horizontal, 11)
                        .padding(.vertical, 7)
                        .background(bubble(theme.text.opacity(0.08)))
                case .notice:
                    Label(message.text, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(theme.secondaryText)
                        .padding(.horizontal, 11)
                        .padding(.vertical, 7)
                        .background(bubble(Color.orange.opacity(0.12)))
                }
            }
            .font(.system(size: 13))
            .textSelection(.enabled)
            if message.role != .user { Spacer(minLength: 80) }
        }
    }

    private func bubble(_ fill: Color) -> some View {
        RoundedRectangle(cornerRadius: 14, style: .continuous)
            .fill(fill)
    }
}

private struct TypingIndicator: View {
    @Environment(\.theme) private var theme

    var body: some View {
        TimelineView(.animation) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: 4) {
                ForEach(0..<3) { i in
                    Circle()
                        .fill(theme.secondaryText)
                        .frame(width: 6, height: 6)
                        .opacity(0.35 + 0.65 * max(0, sin((t * 5) - Double(i) * 0.8)))
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(theme.text.opacity(0.08)))
        }
    }
}
