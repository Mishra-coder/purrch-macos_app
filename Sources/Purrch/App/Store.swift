import Foundation
import Security

/// Small JSON persistence in ~/Library/Application Support/Purrch.
enum Store {
    static let directory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("Purrch", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    static func url(_ name: String) -> URL {
        directory.appendingPathComponent(name.hasSuffix(".json") ? name : "\(name).json")
    }

    static func load<T: Decodable>(_ name: String, default value: T) -> T {
        guard let data = try? Data(contentsOf: url(name)),
              let decoded = try? JSONDecoder().decode(T.self, from: data) else { return value }
        return decoded
    }

    static func save<T: Encodable>(_ name: String, _ value: T) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        try? data.write(to: url(name), options: .atomic)
    }
}

/// API keys live in the login keychain, never in plain defaults.
enum Keychain {
    static let service = "com.mishracoder.purrch"

    static func get(_ account: String, service: String = Keychain.service) -> String {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }

    static func set(_ account: String, _ value: String) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(base as CFDictionary)
        guard !value.isEmpty else { return }
        var add = base
        add[kSecValueData as String] = Data(value.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }
}

/// One-time move of data saved under the app's earlier names (newest first).
enum LegacyMigration {
    private static let oldNames = ["Cub" + "by", "Notch" + "yff"]

    static func run() {
        let fm = FileManager.default
        let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let newDir = base.appendingPathComponent("Purrch", isDirectory: true)
        for name in oldNames where !fm.fileExists(atPath: newDir.path) {
            let oldDir = base.appendingPathComponent(name, isDirectory: true)
            if fm.fileExists(atPath: oldDir.path) { try? fm.moveItem(at: oldDir, to: newDir) }
        }

        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: "migratedFromLegacy") else { return }
        for name in oldNames {
            let oldBundleID = "com.mishracoder." + name.lowercased()
            for (key, value) in defaults.persistentDomain(forName: oldBundleID) ?? [:]
            where key != "migratedFromLegacy" && defaults.object(forKey: key) == nil {
                defaults.set(value, forKey: key)
            }
            for account in ["anthropic", "openai", "gemini", "stripe", "plausible"] where Keychain.get(account).isEmpty {
                let value = Keychain.get(account, service: oldBundleID)
                if !value.isEmpty { Keychain.set(account, value) }
            }
        }
        defaults.set(true, forKey: "migratedFromLegacy")
    }
}
