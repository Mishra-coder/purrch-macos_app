import SwiftUI

struct MessageModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var settings = AppSettings.shared

    private let presets = ["On a call", "Focus mode", "Back in 5", "Recording 🔴", "Ship it 🚀"]
    private let maxLength = 80

    var body: some View {
        HStack(spacing: 10) {
            Card("Message", icon: "bubble.left.fill", accessory: {
                Text("\(settings.messageText.count)/\(maxLength)").monospacedDigit()
            }) {
                VStack(alignment: .leading, spacing: 12) {
                    SearchField(text: Binding(
                        get: { settings.messageText },
                        set: { settings.messageText = String($0.prefix(maxLength)) }
                    ), placeholder: "Type a message for the notch", icon: "text.bubble")

                    Text("QUICK PRESETS")
                        .font(.system(size: 10, weight: .semibold))
                        .tracking(1)
                        .foregroundStyle(theme.secondaryText)

                    FlowChips(items: presets, selected: settings.messageText) { preset in
                        settings.messageText = preset
                        settings.messageEnabled = true
                    }

                    Spacer(minLength: 0)

                    HStack(spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Show in notch")
                                .font(.system(size: 13, weight: .semibold))
                                .foregroundStyle(theme.text)
                            Text("Takes priority over timers and music while on.")
                                .font(.system(size: 11))
                                .foregroundStyle(theme.secondaryText)
                        }
                        Spacer()
                        Toggle("", isOn: $settings.messageEnabled)
                            .labelsHidden()
                            .toggleStyle(.switch)
                            .controlSize(.small)
                            .tint(theme.accent)
                            .disabled(settings.messageText.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                }
            }
            .frame(width: 380)

            Card("Preview", icon: "eye") {
                VStack(alignment: .leading, spacing: 14) {
                    Spacer(minLength: 0)
                    preview
                        .frame(maxWidth: .infinity)
                    Spacer(minLength: 0)
                    Label {
                        Text("Your message scrolls across the closed notch as glowing dot-matrix text, so people around you (and you) can see it at a glance.")
                    } icon: {
                        Image(systemName: "info.circle")
                    }
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var preview: some View {
        let text = settings.messageText.isEmpty ? "Type something" : settings.messageText
        return ZStack {
            Capsule().fill(Color.black)
            DotMatrixMarquee(text: text, color: theme.accent)
                .frame(height: 22)
                .padding(.horizontal, 22)
                .opacity(settings.messageEnabled ? 1 : 0.45)
        }
        .frame(width: 300, height: 40)
        .overlay(Capsule().strokeBorder(theme.border.opacity(0.7), lineWidth: 0.8))
        .shadow(color: settings.messageEnabled && theme.glow ? theme.accent.opacity(0.35) : .clear, radius: 10)
    }
}

/// Wrapping row of selectable chips.
private struct FlowChips: View {
    @Environment(\.theme) private var theme
    let items: [String]
    let selected: String
    let action: (String) -> Void

    var body: some View {
        ChipLayout(spacing: 6) {
            ForEach(items, id: \.self) { item in
                let isOn = item == selected
                Button { action(item) } label: {
                    Text(item)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(isOn ? theme.background : theme.text)
                        .padding(.horizontal, 11)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(isOn ? theme.accent : theme.text.opacity(0.1)))
                }
                .buttonStyle(.plain)
            }
        }
    }
}

private struct ChipLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, rowH: CGFloat = 0, maxX: CGFloat = 0
        for v in subviews {
            let s = v.sizeThatFits(.unspecified)
            if x > 0, x + s.width > width { x = 0; y += rowH + spacing; rowH = 0 }
            x += s.width + spacing
            rowH = max(rowH, s.height)
            maxX = max(maxX, x - spacing)
        }
        return CGSize(width: proposal.width ?? maxX, height: y + rowH)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowH: CGFloat = 0
        for v in subviews {
            let s = v.sizeThatFits(.unspecified)
            if x > bounds.minX, x + s.width > bounds.maxX { x = bounds.minX; y += rowH + spacing; rowH = 0 }
            v.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(s))
            x += s.width + spacing
            rowH = max(rowH, s.height)
        }
    }
}
