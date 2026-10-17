import Foundation
import ServiceManagement
import SwiftUI

/// User preferences. Plain values persist in UserDefaults, secrets in the keychain.
final class AppSettings: ObservableObject {
    static let shared = AppSettings()
    private let defaults = UserDefaults.standard

    @Published var themeID: ThemeID { didSet { defaults.set(themeID.rawValue, forKey: "themeID") } }
    @Published var openOnHover: Bool { didSet { defaults.set(openOnHover, forKey: "openOnHover") } }
    @Published var hideInFullscreen: Bool { didSet { defaults.set(hideInFullscreen, forKey: "hideInFullscreen") } }
    @Published var showLiveActivities: Bool { didSet { defaults.set(showLiveActivities, forKey: "showLiveActivities") } }
    /// Hovering the notch while a browser video plays shows that video live.
    @Published var videoPeekOnHover: Bool { didSet { defaults.set(videoPeekOnHover, forKey: "videoPeekOnHover") } }
    @Published var previewQuality: PreviewQuality { didSet { defaults.set(previewQuality.rawValue, forKey: "previewQuality") } }
    @Published var disabledModules: Set<ModuleID> {
        didSet { defaults.set(disabledModules.map(\.rawValue), forKey: "disabledModules") }
    }
    /// Tab-bar order, rearranged by dragging icons. Always contains every module once.
    @Published var moduleOrder: [ModuleID] {
        didSet { defaults.set(moduleOrder.map(\.rawValue), forKey: "moduleOrder") }
    }
    @Published var lastModule: ModuleID { didSet { defaults.set(lastModule.rawValue, forKey: "lastModule") } }
    @Published var hasCompletedOnboarding: Bool {
        didSet { defaults.set(hasCompletedOnboarding, forKey: "hasCompletedOnboarding") }
    }

    // Message module: scrolling dot-matrix text in the collapsed notch.
    @Published var messageText: String { didSet { defaults.set(messageText, forKey: "messageText") } }
    @Published var messageEnabled: Bool { didSet { defaults.set(messageEnabled, forKey: "messageEnabled") } }

    // Weather: optional manual city when location is unavailable.
    @Published var weatherCity: String { didSet { defaults.set(weatherCity, forKey: "weatherCity") } }
    @Published var useFahrenheit: Bool { didSet { defaults.set(useFahrenheit, forKey: "useFahrenheit") } }

    // MacMate AI
    @Published var macMateProvider: AIProvider { didSet { defaults.set(macMateProvider.rawValue, forKey: "macMateProvider") } }
    @Published var macMateWebSearch: Bool { didSet { defaults.set(macMateWebSearch, forKey: "macMateWebSearch") } }

    // Integrations (non-secret parts)
    @Published var plausibleSite: String { didSet { defaults.set(plausibleSite, forKey: "plausibleSite") } }
    @Published var plausibleHost: String { didSet { defaults.set(plausibleHost, forKey: "plausibleHost") } }
    @Published var revenueCurrency: String { didSet { defaults.set(revenueCurrency, forKey: "revenueCurrency") } }

    // Secrets (keychain)
    @Published var anthropicKey: String { didSet { Keychain.set("anthropic", anthropicKey) } }
    @Published var openAIKey: String { didSet { Keychain.set("openai", openAIKey) } }
    @Published var geminiKey: String { didSet { Keychain.set("gemini", geminiKey) } }
    @Published var stripeKey: String { didSet { Keychain.set("stripe", stripeKey) } }
    @Published var plausibleKey: String { didSet { Keychain.set("plausible", plausibleKey) } }

    @Published var launchAtLogin: Bool {
        didSet {
            do {
                if launchAtLogin { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("Purrch launch-at-login: \(error.localizedDescription)")
            }
        }
    }

    private init() {
        let d = UserDefaults.standard
        themeID = ThemeID(rawValue: d.string(forKey: "themeID") ?? "") ?? .minimal
        openOnHover = d.object(forKey: "openOnHover") as? Bool ?? true
        hideInFullscreen = d.object(forKey: "hideInFullscreen") as? Bool ?? true
        showLiveActivities = d.object(forKey: "showLiveActivities") as? Bool ?? true
        videoPeekOnHover = d.object(forKey: "videoPeekOnHover") as? Bool ?? false
        previewQuality = PreviewQuality(rawValue: d.string(forKey: "previewQuality") ?? "") ?? .balanced
        disabledModules = Set((d.stringArray(forKey: "disabledModules") ?? []).compactMap(ModuleID.init(rawValue:)))
        let savedOrder = (d.stringArray(forKey: "moduleOrder") ?? []).compactMap(ModuleID.init(rawValue:))
        moduleOrder = Self.completeOrder(savedOrder)
        lastModule = ModuleID(rawValue: d.string(forKey: "lastModule") ?? "") ?? .home
        hasCompletedOnboarding = d.bool(forKey: "hasCompletedOnboarding")
        messageText = d.string(forKey: "messageText") ?? "Hello from Purrch"
        messageEnabled = d.bool(forKey: "messageEnabled")
        weatherCity = d.string(forKey: "weatherCity") ?? ""
        useFahrenheit = d.bool(forKey: "useFahrenheit")
        macMateProvider = AIProvider(rawValue: d.string(forKey: "macMateProvider") ?? "") ?? .apple
        macMateWebSearch = d.bool(forKey: "macMateWebSearch")
        plausibleSite = d.string(forKey: "plausibleSite") ?? ""
        plausibleHost = d.string(forKey: "plausibleHost") ?? "https://plausible.io"
        revenueCurrency = d.string(forKey: "revenueCurrency") ?? "usd"
        anthropicKey = Keychain.get("anthropic")
        openAIKey = Keychain.get("openai")
        geminiKey = Keychain.get("gemini")
        stripeKey = Keychain.get("stripe")
        plausibleKey = Keychain.get("plausible")
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    var enabledModules: [ModuleID] { moduleOrder.filter { !disabledModules.contains($0) } }

    /// Saves a new order for the enabled modules; hidden ones keep their relative spots at the end.
    func reorderEnabled(_ enabled: [ModuleID]) {
        moduleOrder = Self.completeOrder(enabled + moduleOrder.filter { disabledModules.contains($0) })
    }

    func resetModuleOrder() { moduleOrder = ModuleID.allCases }

    /// De-duplicates and appends any modules missing from `order` (e.g. ones added in an update).
    private static func completeOrder(_ order: [ModuleID]) -> [ModuleID] {
        var seen = Set<ModuleID>()
        let unique = order.filter { seen.insert($0).inserted }
        return unique + ModuleID.allCases.filter { !seen.contains($0) }
    }

    var theme: Theme { Theme.make(themeID) }
}

enum AIProvider: String, CaseIterable, Identifiable {
    case apple, claude, openai, gemini
    var id: String { rawValue }
    var title: String {
        switch self {
        case .apple: "Apple (on-device)"
        case .claude: "Claude"
        case .openai: "OpenAI"
        case .gemini: "Gemini"
        }
    }
}
