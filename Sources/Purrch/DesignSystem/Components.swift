import SwiftUI

/// The basic surface every module is built from: a soft raised panel with a tinted icon badge.
struct Card<Accessory: View, Content: View>: View {
    @Environment(\.theme) private var theme
    @Environment(\.moduleTint) private var tint
    let title: String
    let icon: String
    let accessory: Accessory
    let content: Content
    @State private var hovering = false

    init(_ title: String, icon: String,
         @ViewBuilder accessory: () -> Accessory,
         @ViewBuilder content: () -> Content) {
        self.title = title
        self.icon = icon
        self.accessory = accessory()
        self.content = content()
    }

    var body: some View {
        let accent = tint ?? theme.accent
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                    .font(.system(size: 10.5, weight: .bold))
                    .foregroundStyle(accent)
                    .frame(width: 22, height: 22)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(accent.opacity(0.16)))
                Text(title)
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundStyle(theme.text.opacity(0.92))
                    .lineLimit(1)
                Spacer(minLength: 4)
                accessory
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText)
            }
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(CardSurface(hovering: hovering))
        .scaleEffect(hovering ? 1.006 : 1)
        .onHover { h in withAnimation(Motion.expoOut(0.35)) { hovering = h } }
    }
}

/// Raised, softly lit background shared by cards and other panels.
struct CardSurface: View {
    @Environment(\.theme) private var theme
    var hovering = false
    var radius: CGFloat = 20

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        shape
            .fill(theme.surface)
            .overlay(shape.fill(LinearGradient(colors: [theme.text.opacity(hovering ? 0.07 : 0.045), .clear],
                                               startPoint: .top, endPoint: .bottom)))
            .overlay(
                // Light catching the top edge, fading out down the sides.
                shape.strokeBorder(LinearGradient(colors: [theme.text.opacity(hovering ? 0.2 : 0.13), theme.text.opacity(0.02)],
                                                  startPoint: .top, endPoint: .bottom), lineWidth: 0.8)
            )
            .overlay {
                // Glowing themes keep their coloured outline.
                if theme.glow || theme.id == .polished {
                    shape.strokeBorder(theme.border.opacity(hovering ? 1 : 0.75), lineWidth: 1)
                }
            }
            .shadow(color: .black.opacity(0.35), radius: hovering ? 12 : 6, y: hovering ? 6 : 3)
            .themeGlow(theme, radius: hovering ? 7 : 3)
    }
}

extension Card where Accessory == EmptyView {
    init(_ title: String, icon: String, @ViewBuilder content: () -> Content) {
        self.init(title, icon: icon, accessory: { EmptyView() }, content: content)
    }
}

/// Circular progress ring with a centred value and a caption below.
struct RingGauge: View {
    @Environment(\.theme) private var theme
    @Environment(\.moduleTint) private var moduleTint
    var value: Double
    var center: String
    var label: String
    var size: CGFloat = 64
    var tint: Color? = nil

    var body: some View {
        let color = tint ?? moduleTint ?? theme.accent
        let line = max(5, size * 0.1)
        VStack(spacing: 6) {
            ZStack {
                Circle().stroke(color.opacity(0.14), lineWidth: line)
                Circle()
                    .trim(from: 0, to: max(0.001, min(1, value)))
                    .stroke(AngularGradient(colors: [color.opacity(0.55), color], center: .center,
                                            startAngle: .degrees(0), endAngle: .degrees(360 * max(0.001, min(1, value)))),
                            style: StrokeStyle(lineWidth: line, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .shadow(color: color.opacity(0.45), radius: 4)
                    .animation(Motion.expoOut(0.6), value: value)
                Text(center)
                    .font(.system(size: size * 0.27, weight: .bold, design: .rounded))
                    .foregroundStyle(theme.text)
                    .monospacedDigit()
                    .contentTransition(.numericText())
                    .animation(Motion.expoOut(0.4), value: center)
            }
            .frame(width: size, height: size)
            Text(label)
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(theme.secondaryText)
        }
    }
}

/// Round icon-only button.
struct IconButton: View {
    @Environment(\.theme) private var theme
    let systemName: String
    var size: CGFloat = 30
    var prominent = false
    var help: String? = nil
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: size * 0.42, weight: .semibold))
                .foregroundStyle(prominent ? theme.background : theme.text)
                .frame(width: size, height: size)
                .background(
                    Circle().fill(prominent ? theme.accent : theme.text.opacity(hovering ? 0.18 : 0.1))
                )
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(help ?? "")
    }
}

/// Capsule button with optional icon. `prominent` fills it with the accent colour.
struct PillButton: View {
    @Environment(\.theme) private var theme
    let title: String
    var icon: String? = nil
    var prominent = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let icon { Image(systemName: icon) }
                Text(title)
            }
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(prominent ? theme.background : theme.text)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(Capsule().fill(prominent ? theme.accent : theme.text.opacity(hovering ? 0.18 : 0.1)))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// Dark rounded search / text field.
struct SearchField: View {
    @Environment(\.theme) private var theme
    @Binding var text: String
    var placeholder = "Search"
    var icon = "magnifyingglass"
    var onSubmit: () -> Void = {}

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: icon).foregroundStyle(theme.secondaryText)
            TextField(placeholder, text: $text)
                .textFieldStyle(.plain)
                .foregroundStyle(theme.text)
                .onSubmit(onSubmit)
            if !text.isEmpty {
                Button { text = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(theme.secondaryText)
                }
                .buttonStyle(.plain)
            }
        }
        .font(.system(size: 13))
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.surface))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(theme.border, lineWidth: 0.8))
    }
}

/// Placeholder for a module with nothing to show yet.
struct EmptyState: View {
    @Environment(\.theme) private var theme
    let icon: String
    let title: String
    var subtitle: String = ""

    var body: some View {
        VStack(spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 28, weight: .light))
                .foregroundStyle(theme.accent)
                .themeGlow(theme)
            Text(title).font(.system(size: 14, weight: .semibold)).foregroundStyle(theme.text)
            if !subtitle.isEmpty {
                Text(subtitle)
                    .font(.system(size: 12))
                    .foregroundStyle(theme.secondaryText)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Row container used by list-style modules (clipboard, captures, downloads...).
struct ListRow<Content: View>: View {
    @Environment(\.theme) private var theme
    let content: Content
    @State private var hovering = false

    init(@ViewBuilder content: () -> Content) { self.content = content() }

    var body: some View {
        content
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(hovering ? theme.text.opacity(0.08) : theme.surface)
            )
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(theme.border.opacity(0.6), lineWidth: 0.6))
            .onHover { hovering = $0 }
    }
}

/// Small rounded tag like "PNG" or "LOCK".
struct Tag: View {
    let text: String
    var color: Color = .orange

    var body: some View {
        Text(text.uppercased())
            .font(.system(size: 9, weight: .bold))
            .tracking(0.6)
            .foregroundStyle(color)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: 4).fill(color.opacity(0.18)))
    }
}

enum Format {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    static func relative(_ date: Date) -> String {
        let seconds = Int(Date().timeIntervalSince(date))
        if seconds < 60 { return "now" }
        if seconds < 3600 { return "\(seconds / 60)m" }
        if seconds < 86400 { return "\(seconds / 3600)h" }
        return "\(seconds / 86400)d"
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let s = max(0, Int(seconds.rounded()))
        if s >= 3600 { return String(format: "%d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60) }
        return String(format: "%d:%02d", s / 60, s % 60)
    }
}
