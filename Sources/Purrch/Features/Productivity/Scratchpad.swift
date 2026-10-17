import AppKit
import SwiftUI

@MainActor
final class ScratchpadStore: ObservableObject {
    static let shared = ScratchpadStore()

    @Published var text: String {
        didSet { if text != oldValue { saver.schedule { [weak self] in self?.save() } } }
    }
    private let saver = DebouncedSaver(delay: 0.6)

    private init() { text = Store.load("scratchpad", default: "") }

    private func save() { Store.save("scratchpad", text) }
    func flush() { saver.flush() }
}

struct ScratchpadModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var store = ScratchpadStore.shared
    @AppStorage("scratchpad.monospaced") private var monospaced = false
    @AppStorage("scratchpad.fontSize") private var fontSize = 14.0
    @State private var confirmClear = false
    @State private var copied = false

    private var words: Int { store.text.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).count }

    var body: some View {
        VStack(spacing: 8) {
            TextEditor(text: $store.text)
                .font(.system(size: fontSize, design: monospaced ? .monospaced : .default))
                .foregroundStyle(theme.text)
                .scrollContentBackground(.hidden)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(theme.surface))
                .overlay(
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .strokeBorder(theme.border, lineWidth: theme.glow ? 1.2 : 0.8)
                )
                .overlay(alignment: .topLeading) {
                    if store.text.isEmpty {
                        Text("Type anything. It's saved automatically.")
                            .font(.system(size: fontSize, design: monospaced ? .monospaced : .default))
                            .foregroundStyle(theme.secondaryText.opacity(0.7))
                            .padding(.horizontal, 13)
                            .padding(.vertical, 8)
                            .allowsHitTesting(false)
                    }
                }
            toolbar
        }
        .onDisappear { store.flush() }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text("\(store.text.count) chars · \(words) words")
                .font(.system(size: 11, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(theme.secondaryText)
            Spacer()
            HStack(spacing: 4) {
                IconButton(systemName: "minus", size: 24, help: "Smaller text") { fontSize = max(10, fontSize - 1) }
                Text("\(Int(fontSize))pt")
                    .font(.system(size: 11, weight: .semibold))
                    .monospacedDigit()
                    .foregroundStyle(theme.secondaryText)
                    .frame(width: 34)
                IconButton(systemName: "plus", size: 24, help: "Larger text") { fontSize = min(28, fontSize + 1) }
            }
            IconButton(systemName: "chevron.left.forwardslash.chevron.right", size: 26,
                       prominent: monospaced, help: "Monospaced") { monospaced.toggle() }
            PillButton(title: copied ? "Copied" : "Copy all", icon: copied ? "checkmark" : "doc.on.doc") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(store.text, forType: .string)
                copied = true
                Task { try? await Task.sleep(for: .seconds(1.5)); copied = false }
            }
            PillButton(title: confirmClear ? "Confirm clear" : "Clear", icon: "trash", prominent: confirmClear) {
                if confirmClear {
                    store.text = ""
                    confirmClear = false
                } else {
                    confirmClear = true
                    Task { try? await Task.sleep(for: .seconds(3)); confirmClear = false }
                }
            }
            .disabled(store.text.isEmpty)
            .opacity(store.text.isEmpty ? 0.5 : 1)
        }
    }
}
