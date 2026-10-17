import AppKit
import SwiftUI

enum ThemeID: String, CaseIterable, Identifiable {
    case minimal, playful, molten, techHUD, polished, bloodMoon
    var id: String { rawValue }
}

/// Colours for the whole notch. Every module reads it from `@Environment(\.theme)`.
struct Theme {
    let id: ThemeID
    let name: String
    let tagline: String
    /// Panel fill (the notch body).
    let background: Color
    /// Card fill.
    let surface: Color
    /// Card stroke.
    let border: Color
    /// Primary accent (rings, active tab, toggles, sliders).
    let accent: Color
    let text: Color
    let secondaryText: Color
    /// Soft outer glow on accented elements.
    let glow: Bool

    /// Accent for a given module. The playful theme gives each module its own colour.
    func accent(for module: ModuleID) -> Color {
        guard id == .playful else { return accent }
        return module.playfulColor
    }

    /// The theme as seen inside one module: in Playful, that module's colour becomes the accent.
    func tinted(for module: ModuleID) -> Theme {
        guard id == .playful else { return self }
        return Theme(id: id, name: name, tagline: tagline, background: background, surface: surface, border: border,
                     accent: module.playfulColor, text: text, secondaryText: secondaryText, glow: glow)
    }

    static func make(_ id: ThemeID) -> Theme {
        switch id {
        case .minimal:
            Theme(id: id, name: "Minimal", tagline: "Black and white, nothing else",
                  background: .black, surface: Color(white: 0.09), border: Color(white: 0.2),
                  accent: .white, text: .white, secondaryText: Color(white: 0.55), glow: false)
        case .playful:
            Theme(id: id, name: "Playful", tagline: "A system colour per module",
                  background: .black, surface: Color(white: 0.1), border: Color(white: 0.2),
                  accent: .blue, text: .white, secondaryText: Color(white: 0.58), glow: false)
        case .molten:
            Theme(id: id, name: "Molten", tagline: "Glowing orange on black",
                  background: Color(red: 0.05, green: 0.025, blue: 0.0),
                  surface: Color(red: 0.12, green: 0.06, blue: 0.02),
                  border: Color(red: 0.95, green: 0.5, blue: 0.15).opacity(0.75),
                  accent: Color(red: 1.0, green: 0.55, blue: 0.18),
                  text: .white, secondaryText: Color(red: 0.75, green: 0.62, blue: 0.52), glow: true)
        case .techHUD:
            Theme(id: id, name: "Tech HUD", tagline: "Cyan on deep teal",
                  background: Color(red: 0.0, green: 0.06, blue: 0.07),
                  surface: Color(red: 0.0, green: 0.12, blue: 0.13),
                  border: Color(red: 0.2, green: 0.9, blue: 0.95).opacity(0.55),
                  accent: Color(red: 0.25, green: 0.95, blue: 1.0),
                  text: Color(red: 0.85, green: 1.0, blue: 1.0),
                  secondaryText: Color(red: 0.45, green: 0.7, blue: 0.72), glow: true)
        case .polished:
            Theme(id: id, name: "Polished", tagline: "Dark green with gold",
                  background: Color(red: 0.02, green: 0.07, blue: 0.05),
                  surface: Color(red: 0.05, green: 0.14, blue: 0.1),
                  border: Color(red: 0.85, green: 0.7, blue: 0.35).opacity(0.5),
                  accent: Color(red: 0.92, green: 0.76, blue: 0.4),
                  text: Color(red: 0.97, green: 0.94, blue: 0.85),
                  secondaryText: Color(red: 0.6, green: 0.66, blue: 0.58), glow: false)
        case .bloodMoon:
            Theme(id: id, name: "Blood Moon", tagline: "Oxblood borders, warm highlights",
                  background: Color(red: 0.06, green: 0.01, blue: 0.015),
                  surface: Color(red: 0.13, green: 0.03, blue: 0.04),
                  border: Color(red: 0.55, green: 0.08, blue: 0.12).opacity(0.85),
                  accent: Color(red: 0.95, green: 0.35, blue: 0.3),
                  text: Color(red: 1.0, green: 0.93, blue: 0.9),
                  secondaryText: Color(red: 0.7, green: 0.5, blue: 0.48), glow: true)
        }
    }
}

private struct ThemeKey: EnvironmentKey {
    static let defaultValue = Theme.make(.minimal)
}

private struct ModuleTintKey: EnvironmentKey {
    static let defaultValue: Color? = nil
}

extension EnvironmentValues {
    var theme: Theme {
        get { self[ThemeKey.self] }
        set { self[ThemeKey.self] = newValue }
    }

    /// Accent of the module on screen (differs per module in the Playful theme).
    var moduleTint: Color? {
        get { self[ModuleTintKey.self] }
        set { self[ModuleTintKey.self] = newValue }
    }
}

extension View {
    /// Adds the theme's glow around accented shapes when the theme wants it.
    func themeGlow(_ theme: Theme, radius: CGFloat = 6) -> some View {
        shadow(color: theme.glow ? theme.accent.opacity(0.6) : .clear, radius: radius)
    }
}

/// Purrch's artwork, bundled in Contents/Resources.
enum Brand {
    /// The mascot with a transparent background.
    static let mascot: NSImage = Bundle.main.image(forResource: "Mascot") ?? NSApp.applicationIconImage

    /// Small full-colour mascot for the menu bar.
    static func menuBarImage() -> NSImage {
        let size = NSSize(width: 22, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            let m = mascot.size
            let scale = min(rect.width / m.width, rect.height / m.height)
            let w = m.width * scale, h = m.height * scale
            mascot.draw(in: NSRect(x: (rect.width - w) / 2, y: (rect.height - h) / 2, width: w, height: h))
            return true
        }
        image.accessibilityDescription = "Purrch"
        return image
    }
}
