#!/bin/bash
# Writes windows/src/Purrch/Assets/Emoji.tsv (emoji, name, category) from the Mac app's
# EmojiCatalog, so both apps list the same emoji in the same order. .NET has no Unicode
# emoji properties or character names, so Windows reads this file instead. Needs swiftc (macOS).
#
# Emoji newer than Unicode 15.0 are left out: Segoe UI Emoji on Windows 10 and 11 draws
# them as empty boxes.
set -euo pipefail
cd "$(dirname "$0")/../.."

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The catalog code runs from "enum EmojiCategory" up to the recents store. Country names
# are always English, whatever the Mac's language is.
{
  echo 'import Foundation'
  sed -n '/^enum EmojiCategory/,/^@MainActor/p' Sources/Purrch/Features/Productivity/Emoji.swift \
    | sed '$d' \
    | sed 's/Locale\.current/Locale(identifier: "en_US")/'
} > "$work/Catalog.swift"

cat > "$work/main.swift" <<'EOF'
import Foundation

func supported(_ char: String) -> Bool {
    char.unicodeScalars.allSatisfy { s in
        guard let age = s.properties.age else { return false }
        return age.major < 15 || (age.major == 15 && age.minor == 0)
    }
}

var out = ""
for e in EmojiCatalog.all where supported(e.char) {
    out += "\(e.char)\t\(e.name)\t\(e.category.rawValue)\n"
}
FileHandle.standardOutput.write(out.data(using: .utf8)!)
EOF

swiftc -O -o "$work/catalog" "$work/Catalog.swift" "$work/main.swift"
"$work/catalog" > windows/src/Purrch/Assets/Emoji.tsv
echo "$(wc -l < windows/src/Purrch/Assets/Emoji.tsv | tr -d ' ') emoji written to windows/src/Purrch/Assets/Emoji.tsv"
