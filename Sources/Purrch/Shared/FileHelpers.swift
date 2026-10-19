import AppKit
import SwiftUI
import QuickLookThumbnailing
import UniformTypeIdentifiers

/// QuickLook thumbnails with an in-memory cache, falling back to the Finder icon.
@MainActor
enum Thumbnails {
    private static let cache = NSCache<NSString, NSImage>()

    static func image(for url: URL, size: CGSize) async -> NSImage {
        let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate)?
            .timeIntervalSince1970 ?? 0
        let key = "\(url.path)|\(Int(size.width))x\(Int(size.height))|\(modified)" as NSString
        if let cached = cache.object(forKey: key) { return cached }
        let scale = NSScreen.main?.backingScaleFactor ?? 2
        let request = QLThumbnailGenerator.Request(fileAt: url, size: size, scale: scale,
                                                   representationTypes: .all)
        if let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request) {
            let image = rep.nsImage
            cache.setObject(image, forKey: key)
            return image
        }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        cache.setObject(icon, forKey: key)
        return icon
    }
}

/// Shows a file's QuickLook thumbnail (or its icon while loading).
struct FileThumbnailView: View {
    let url: URL
    var size: CGSize = CGSize(width: 64, height: 64)
    var contentMode: ContentMode = .fit
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: contentMode)
            } else {
                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path))
                    .resizable().aspectRatio(contentMode: .fit)
            }
        }
        .task(id: url) { image = await Thumbnails.image(for: url, size: size) }
    }
}

enum FileMeta {
    static func size(_ url: URL) -> Int64 {
        let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey, .totalFileAllocatedSizeKey])
        if values?.isDirectory == true { return directorySize(url) }
        return Int64(values?.fileSize ?? 0)
    }

    static func directorySize(_ url: URL, limit: Int = 5000) -> Int64 {
        guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey],
                                                     options: [], errorHandler: nil) else { return 0 }
        var total: Int64 = 0
        var count = 0
        for case let file as URL in e {
            total += Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
            count += 1
            if count > limit { break }
        }
        return total
    }

    static func isDirectory(_ url: URL) -> Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &dir) && dir.boolValue
            && !(NSWorkspace.shared.isFilePackage(atPath: url.path))
    }

    /// Short uppercase label like "PNG" or "FOLDER".
    static func label(_ url: URL) -> String {
        if isDirectory(url) { return "Folder" }
        let ext = url.pathExtension
        return ext.isEmpty ? "File" : ext
    }

    /// Stable colour per file extension.
    static func color(for label: String) -> Color {
        switch label.lowercased() {
        case "png", "jpg", "jpeg", "heic", "gif", "webp", "tiff", "image": return .pink
        case "mov", "mp4", "m4v": return .purple
        case "pdf": return .red
        case "zip", "dmg", "pkg", "gz", "tar": return .yellow
        case "folder": return .blue
        case "text", "txt", "md", "rtf": return .teal
        case "link", "url": return .green
        case "swift", "json", "js", "ts", "py", "html", "css", "lock", "xcodeproj": return .orange
        default:
            let palette: [Color] = [.mint, .cyan, .indigo, .orange, .green]
            let sum = label.unicodeScalars.reduce(0) { $0 + Int($1.value) }
            return palette[sum % palette.count]
        }
    }

    static func reveal(_ urls: [URL]) {
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    static func isImage(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.contentTypeKey]).contentType)?.conforms(to: .image) ?? false
    }

    /// Copies the files. A single image is copied as pixels instead, so ⌘V pastes the picture into chats and docs
    /// (most apps only read the first pasteboard item and ignore an image sitting behind a file URL).
    static func copyFiles(_ urls: [URL]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if urls.count == 1, let url = urls.first, isImage(url),
           let tiff = NSImage(contentsOf: url)?.tiffRepresentation {
            let item = NSPasteboardItem()
            if let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                item.setData(png, forType: .png)
            }
            item.setData(tiff, forType: .tiff)
            pb.writeObjects([item])
            return
        }
        pb.writeObjects(urls as [NSURL])
    }

    static func airDrop(_ urls: [URL]) {
        NSSharingService(named: .sendViaAirDrop)?.perform(withItems: urls)
    }

    /// Runs an open panel above the notch.
    @MainActor
    static func choose(types: [UTType] = [], multiple: Bool = true, completion: @escaping ([URL]) -> Void) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = multiple
        panel.canChooseDirectories = types.isEmpty
        panel.canChooseFiles = true
        if !types.isEmpty { panel.allowedContentTypes = types }
        panel.level = .modalPanel
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { response in
            guard response == .OK else { return }
            completion(panel.urls)
        }
    }
}

/// Loads file URLs out of drop providers and hands them back on the main actor.
enum DropLoader {
    static func fileURLs(from providers: [NSItemProvider], completion: @escaping @MainActor ([URL]) -> Void) {
        let group = DispatchGroup()
        let lock = NSLock()
        var urls: [URL] = []
        for provider in providers where provider.canLoadObject(ofClass: URL.self) {
            group.enter()
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                if let url, url.isFileURL {
                    lock.lock(); urls.append(url); lock.unlock()
                }
                group.leave()
            }
        }
        group.notify(queue: .main) {
            let result = urls
            MainActor.assumeIsolated { completion(result) }
        }
    }
}
