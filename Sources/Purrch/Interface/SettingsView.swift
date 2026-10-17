import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.theme) private var theme
    @State private var section: Section = .appearance

    enum Section: String, CaseIterable, Identifiable {
        case appearance = "Appearance", behaviour = "Behaviour", modules = "Modules"
        case ai = "MacMate AI", integrations = "Integrations", permissions = "Permissions", about = "About"
        var id: String { rawValue }
        var icon: String {
            switch self {
            case .appearance: "paintpalette"
            case .behaviour: "cursorarrow.motionlines"
            case .modules: "square.grid.3x3"
            case .ai: "sparkles"
            case .integrations: "key"
            case .permissions: "lock.shield"
            case .about: "info.circle"
            }
        }
    }

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(Section.allCases) { s in
                    Button { section = s } label: {
                        Label(s.rawValue, systemImage: s.icon)
                            .font(.system(size: 12, weight: section == s ? .semibold : .regular))
                            .foregroundStyle(section == s ? theme.text : theme.secondaryText)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 8).fill(section == s ? theme.accent.opacity(0.18) : .clear))
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
                Button { NSApp.terminate(nil) } label: {
                    Label("Quit Purrch", systemImage: "power")
                        .font(.system(size: 12))
                        .foregroundStyle(theme.secondaryText)
                        .padding(.horizontal, 10)
                }
                .buttonStyle(.plain)
            }
            .frame(width: 160)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) { content }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.trailing, 6)
            }
            .scrollIndicators(.never)
        }
    }

    @ViewBuilder
    private var content: some View {
        switch section {
        case .appearance:
            Text("Theme").font(.system(size: 13, weight: .semibold))
            ThemePicker()
        case .behaviour:
            toggle("Open on hover", "Off means click the notch to open it.", $settings.openOnHover)
            toggle("Hide in full screen", "Get out of the way of full-screen apps and videos.", $settings.hideInFullscreen)
            toggle("Live activities", "Music, timers, downloads and messages in the closed notch.", $settings.showLiveActivities)
            toggle("Video on hover", "Hovering the notch jumps straight to the playing YouTube video instead of your last tool.", $settings.videoPeekOnHover)
            toggle("Launch at login", "Start Purrch when you log in.", $settings.launchAtLogin)
        case .modules:
            HStack {
                Text("Turn tools on or off. Drag icons in the tab bar to reorder them.")
                    .font(.system(size: 12)).foregroundStyle(theme.secondaryText)
                Spacer()
                PillButton(title: "Reset order", icon: "arrow.counterclockwise") {
                    withAnimation(Motion.pop) { settings.resetModuleOrder() }
                }
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 8)], spacing: 8) {
                ForEach(settings.moduleOrder) { module in
                    let on = Binding(
                        get: { !settings.disabledModules.contains(module) },
                        set: { enabled in
                            if enabled { settings.disabledModules.remove(module) }
                            else if module != .home { settings.disabledModules.insert(module) }
                        }
                    )
                    HStack {
                        Image(systemName: module.icon).frame(width: 18).foregroundStyle(theme.accent(for: module))
                        Text(module.title).font(.system(size: 12))
                        Spacer()
                        Toggle("", isOn: on).toggleStyle(.switch).controlSize(.mini).labelsHidden()
                            .tint(theme.accent).disabled(module == .home)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 9).fill(theme.surface))
                }
            }
        case .ai:
            Text("Model").font(.system(size: 13, weight: .semibold))
            Picker("", selection: $settings.macMateProvider) {
                ForEach(AIProvider.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented).labelsHidden()
            Text("Apple's on-device model needs macOS 26 or later and Apple Intelligence. Other providers use your own API key, stored in the keychain.")
                .font(.system(size: 11)).foregroundStyle(theme.secondaryText)
            secret("Anthropic API key", $settings.anthropicKey)
            secret("OpenAI API key", $settings.openAIKey)
            secret("Gemini API key", $settings.geminiKey)
            toggle("Web search", "Let Claude search the web when it needs fresh information.", $settings.macMateWebSearch)
        case .integrations:
            Text("Revenue (Stripe)").font(.system(size: 13, weight: .semibold))
            secret("Restricted read-only key (rk_...)", $settings.stripeKey)
            plain("Currency", $settings.revenueCurrency)
            Text("Analytics (Plausible)").font(.system(size: 13, weight: .semibold)).padding(.top, 6)
            secret("Plausible API key", $settings.plausibleKey)
            plain("Site domain (example.com)", $settings.plausibleSite)
            plain("Host", $settings.plausibleHost)
            Text("Weather").font(.system(size: 13, weight: .semibold)).padding(.top, 6)
            plain("City (blank = use location)", $settings.weatherCity)
            toggle("Fahrenheit", "", $settings.useFahrenheit)
        case .permissions:
            Text("Everything stays on this Mac. Grant only what you use.")
                .font(.system(size: 12)).foregroundStyle(theme.secondaryText)
            permission("Calendars & Reminders", "Calendar module", "Privacy_Calendars")
            permission("Location", "Local weather", "Privacy_LocationServices")
            permission("Automation", "Control Music and Spotify", "Privacy_Automation")
            permission("Full Disk Access", "Captures outside Desktop", "Privacy_AllFiles")
            permission("Accessibility", "Optional global shortcuts", "Privacy_Accessibility")
        case .about:
            HStack(spacing: 12) {
                Image(nsImage: Brand.mascot).resizable().aspectRatio(contentMode: .fit).frame(width: 64, height: 54)
                    .shadow(color: .purple.opacity(0.4), radius: 8, y: 3)
                VStack(alignment: .leading) {
                    Text("Purrch").font(.system(size: 16, weight: .bold))
                    Text("Version \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev")")
                        .font(.system(size: 12)).foregroundStyle(theme.secondaryText)
                }
            }
            Text("23 tools in your MacBook's notch. Native Swift and SwiftUI. No account, no telemetry — your clipboard, notes, captures and usage stats never leave this Mac.")
                .font(.system(size: 12)).foregroundStyle(theme.secondaryText)
            PillButton(title: "Show welcome again", icon: "hand.wave") { settings.hasCompletedOnboarding = false }
        }
    }

    private func toggle(_ title: String, _ detail: String, _ binding: Binding<Bool>) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13))
                if !detail.isEmpty { Text(detail).font(.system(size: 11)).foregroundStyle(theme.secondaryText) }
            }
            Spacer()
            Toggle("", isOn: binding).toggleStyle(.switch).labelsHidden().tint(theme.accent)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(theme.surface))
    }

    private func secret(_ title: String, _ binding: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 11)).foregroundStyle(theme.secondaryText)
            SecureField("", text: binding)
                .textFieldStyle(.plain)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(theme.surface))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(theme.border, lineWidth: 0.6))
        }
    }

    private func plain(_ title: String, _ binding: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 11)).foregroundStyle(theme.secondaryText)
            TextField("", text: binding)
                .textFieldStyle(.plain)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(theme.surface))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(theme.border, lineWidth: 0.6))
        }
    }

    private func permission(_ title: String, _ detail: String, _ anchor: String) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13))
                Text(detail).font(.system(size: 11)).foregroundStyle(theme.secondaryText)
            }
            Spacer()
            PillButton(title: "Open", icon: "arrow.up.forward") {
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
                    NSWorkspace.shared.open(url)
                }
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(theme.surface))
    }
}

/// Six theme swatches; picking one recolours the notch immediately.
struct ThemePicker: View {
    @EnvironmentObject private var settings: AppSettings

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 10)], spacing: 10) {
            ForEach(ThemeID.allCases) { id in
                let t = Theme.make(id)
                Button { settings.themeID = id } label: {
                    VStack(alignment: .leading, spacing: 8) {
                        HStack(spacing: 6) {
                            ForEach(0..<3, id: \.self) { i in
                                Circle()
                                    .trim(from: 0, to: [0.75, 0.5, 0.9][i])
                                    .stroke(id == .playful ? [Color.blue, .orange, .pink][i] : t.accent, lineWidth: 3)
                                    .rotationEffect(.degrees(-90))
                                    .frame(width: 22, height: 22)
                                    .themeGlow(t, radius: 3)
                            }
                            Spacer()
                            if settings.themeID == id {
                                Image(systemName: "checkmark.circle.fill").foregroundStyle(t.accent)
                            }
                        }
                        Text(t.name).font(.system(size: 13, weight: .bold)).foregroundStyle(t.text)
                        Text(t.tagline).font(.system(size: 10)).foregroundStyle(t.secondaryText)
                    }
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 12).fill(t.surface))
                    .overlay(RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(settings.themeID == id ? t.accent : t.border, lineWidth: settings.themeID == id ? 2 : 0.8))
                    .background(RoundedRectangle(cornerRadius: 12).fill(t.background))
                }
                .buttonStyle(.plain)
            }
        }
    }
}

struct OnboardingView: View {
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(alignment: .top, spacing: 20) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    Image(nsImage: Brand.mascot).resizable().aspectRatio(contentMode: .fit).frame(width: 56, height: 48)
                        .shadow(color: .purple.opacity(0.4), radius: 8, y: 3)
                    Text("Welcome to Purrch").font(.system(size: 22, weight: .bold))
                }
                Text("Your notch is now a control centre with 23 tools. Hover over it to open, move away to close. Drop files on it to park them on the shelf.")
                    .font(.system(size: 13)).foregroundStyle(theme.secondaryText)
                Label("Everything stays on your Mac", systemImage: "lock.fill").font(.system(size: 12))
                Label("Esc closes, the pin keeps it open", systemImage: "pin").font(.system(size: 12))
                Label("Gear → Modules to hide tools you don't need", systemImage: "square.grid.3x3").font(.system(size: 12))
                Spacer()
                PillButton(title: "Get started", icon: "arrow.right", prominent: true) {
                    settings.hasCompletedOnboarding = true
                }
            }
            .frame(width: 280)
            VStack(alignment: .leading, spacing: 8) {
                Text("Pick a look").font(.system(size: 13, weight: .semibold))
                ScrollView { ThemePicker() }.scrollIndicators(.never)
            }
        }
        .padding(.top, 6)
    }
}
