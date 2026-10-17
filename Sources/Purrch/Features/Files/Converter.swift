import AppKit
import SwiftUI
import ImageIO
import PDFKit
import UniformTypeIdentifiers

enum ConvertFormat: String, CaseIterable, Identifiable {
    case jpeg = "JPEG", png = "PNG", heic = "HEIC", pdf = "PDF"
    var id: String { rawValue }
    var ext: String {
        switch self {
        case .jpeg: "jpg"
        case .png: "png"
        case .heic: "heic"
        case .pdf: "pdf"
        }
    }
    var utType: UTType {
        switch self {
        case .jpeg: .jpeg
        case .png: .png
        case .heic: .heic
        case .pdf: .pdf
        }
    }
    var lossy: Bool { self == .jpeg || self == .heic }
}

struct ConversionResult: Identifiable, Equatable {
    let id = UUID()
    let sources: [URL]
    let output: URL?
    let before: Int64
    let after: Int64
    let error: String?

    var saved: Double { before > 0 ? 1 - Double(after) / Double(before) : 0 }
}

/// Local image conversion with ImageIO / PDFKit.
enum ImageConverter {
    static func convert(_ urls: [URL], to format: ConvertFormat, quality: Double, maxDimension: Int?) -> [ConversionResult] {
        if format == .pdf { return [makePDF(urls, quality: quality, maxDimension: maxDimension)] }
        return urls.map { convertOne($0, to: format, quality: quality, maxDimension: maxDimension) }
    }

    static func loadImage(_ url: URL, maxDimension: Int?) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        var longest = maxDimension
        if longest == nil,
           let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] {
            let w = (props[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue ?? 0
            let h = (props[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue ?? 0
            longest = max(w, h)
        }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: max(1, longest ?? 4096),
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
            ?? CGImageSourceCreateImageAtIndex(source, 0, nil)
    }

    private static func convertOne(_ url: URL, to format: ConvertFormat, quality: Double, maxDimension: Int?) -> ConversionResult {
        let before = FileMeta.size(url)
        guard let image = loadImage(url, maxDimension: maxDimension) else {
            return ConversionResult(sources: [url], output: nil, before: before, after: 0, error: "Couldn't read image")
        }
        let output = uniqueURL(next: url, suffix: "-converted", ext: format.ext)
        guard let dest = CGImageDestinationCreateWithURL(output as CFURL, format.utType.identifier as CFString, 1, nil) else {
            return ConversionResult(sources: [url], output: nil, before: before, after: 0, error: "\(format.rawValue) not supported")
        }
        var props: [CFString: Any] = [:]
        if format.lossy { props[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(dest, image, props as CFDictionary)
        guard CGImageDestinationFinalize(dest) else {
            return ConversionResult(sources: [url], output: nil, before: before, after: 0, error: "Write failed")
        }
        return ConversionResult(sources: [url], output: output, before: before, after: FileMeta.size(output), error: nil)
    }

    private static func makePDF(_ urls: [URL], quality: Double, maxDimension: Int?) -> ConversionResult {
        let before = urls.reduce(Int64(0)) { $0 + FileMeta.size($1) }
        let document = PDFDocument()
        for url in urls {
            guard let cg = loadImage(url, maxDimension: maxDimension) else { continue }
            // Re-encode as JPEG so the quality slider shrinks the PDF too.
            var image = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
            let rep = NSBitmapImageRep(cgImage: cg)
            if let data = rep.representation(using: .jpeg, properties: [.compressionFactor: quality]),
               let jpeg = NSImage(data: data) {
                jpeg.size = image.size
                image = jpeg
            }
            if let page = PDFPage(image: image) { document.insert(page, at: document.pageCount) }
        }
        guard document.pageCount > 0, let first = urls.first else {
            return ConversionResult(sources: urls, output: nil, before: before, after: 0, error: "No readable images")
        }
        let output = uniqueURL(next: first, suffix: urls.count > 1 ? "-combined" : "", ext: "pdf")
        guard document.write(to: output) else {
            return ConversionResult(sources: urls, output: nil, before: before, after: 0, error: "Write failed")
        }
        return ConversionResult(sources: urls, output: output, before: before, after: FileMeta.size(output), error: nil)
    }

    private static func uniqueURL(next url: URL, suffix: String, ext: String) -> URL {
        let dir = url.deletingLastPathComponent()
        let base = url.deletingPathExtension().lastPathComponent + suffix
        var candidate = dir.appendingPathComponent(base).appendingPathExtension(ext)
        var n = 2
        while FileManager.default.fileExists(atPath: candidate.path) || candidate == url {
            candidate = dir.appendingPathComponent("\(base) \(n)").appendingPathExtension(ext)
            n += 1
        }
        return candidate
    }
}

struct ConverterModule: View {
    @Environment(\.theme) private var theme
    @State private var inputs: [URL] = []
    @State private var format: ConvertFormat = .jpeg
    @State private var quality = 0.8
    @State private var maxDimension: Int? = nil
    @State private var results: [ConversionResult] = []
    @State private var converting = false
    @State private var targeted = false

    private let dimensions: [Int?] = [nil, 3840, 2048, 1280, 800]

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            dropZone.frame(width: 230)
            VStack(alignment: .leading, spacing: 10) {
                options
                resultsList
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    // MARK: Drop zone

    private var dropZone: some View {
        VStack(spacing: 8) {
            if inputs.isEmpty {
                Spacer()
                Image(systemName: "photo.badge.arrow.down")
                    .font(.system(size: 30, weight: .light))
                    .foregroundStyle(theme.accent)
                    .themeGlow(theme)
                Text("Drop images")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(theme.text)
                Text("or click to choose")
                    .font(.system(size: 12))
                    .foregroundStyle(theme.secondaryText)
                Spacer()
            } else {
                HStack {
                    Text("\(inputs.count) image\(inputs.count == 1 ? "" : "s")")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(theme.text)
                    Spacer()
                    Button("Clear") { inputs = [] }
                        .buttonStyle(.plain)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(theme.accent)
                }
                ScrollView(.vertical, showsIndicators: false) {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 58), spacing: 6)], spacing: 6) {
                        ForEach(inputs, id: \.self) { url in
                            FileThumbnailView(url: url, size: CGSize(width: 116, height: 116), contentMode: .fill)
                                .frame(width: 58, height: 58)
                                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                                .overlay(alignment: .topTrailing) {
                                    Button { inputs.removeAll { $0 == url } } label: {
                                        Image(systemName: "xmark.circle.fill")
                                            .font(.system(size: 12))
                                            .foregroundStyle(.white, .black.opacity(0.6))
                                    }
                                    .buttonStyle(.plain)
                                    .padding(2)
                                }
                                .help(url.lastPathComponent)
                        }
                    }
                }
                Text("Click to add more")
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .fill(targeted ? theme.accent.opacity(0.1) : theme.surface))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(targeted ? theme.accent : theme.border,
                          style: StrokeStyle(lineWidth: targeted ? 2 : 1, dash: [7, 5])))
        .contentShape(Rectangle())
        .onTapGesture { choose() }
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            DropLoader.fileURLs(from: providers) { add($0) }
            return true
        }
    }

    // MARK: Options

    private var options: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                label("Format")
                ForEach(ConvertFormat.allCases) { f in
                    Chip(title: f.rawValue, selected: format == f) { format = f }
                }
            }
            HStack(spacing: 6) {
                label("Max size")
                ForEach(dimensions, id: \.self) { d in
                    Chip(title: d.map { "\($0)" } ?? "Original", selected: maxDimension == d) { maxDimension = d }
                }
            }
            HStack(spacing: 8) {
                label("Quality")
                Slider(value: $quality, in: 0.1...1)
                    .tint(theme.accent)
                    .disabled(format == .png)
                Text(format == .png ? "Lossless" : "\(Int(quality * 100))%")
                    .font(.system(size: 12, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(theme.text)
                    .frame(width: 58, alignment: .trailing)
                PillButton(title: converting ? "Converting…" : "Convert", icon: "arrow.triangle.2.circlepath",
                           prominent: !inputs.isEmpty) { convert() }
                    .disabled(inputs.isEmpty || converting)
                    .opacity(inputs.isEmpty ? 0.5 : 1)
            }
        }
    }

    private func label(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.system(size: 10, weight: .semibold))
            .tracking(0.8)
            .foregroundStyle(theme.secondaryText)
            .frame(width: 62, alignment: .leading)
    }

    // MARK: Results

    @ViewBuilder
    private var resultsList: some View {
        if results.isEmpty {
            Text("Converted files are saved next to the originals. Nothing leaves your Mac.")
                .font(.system(size: 11))
                .foregroundStyle(theme.secondaryText)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 6) {
                    ForEach(results) { ResultRow(result: $0) }
                }
            }
        }
    }

    // MARK: Actions

    private func add(_ urls: [URL]) {
        let images = urls.filter { UTType(filenameExtension: $0.pathExtension)?.conforms(to: .image) ?? false }
        for url in images where !inputs.contains(url) { inputs.append(url) }
    }

    private func choose() {
        FileMeta.choose(types: [.image]) { add($0) }
    }

    private func convert() {
        guard !inputs.isEmpty, !converting else { return }
        converting = true
        let urls = inputs, format = format, quality = quality, maxDimension = maxDimension
        Task {
            let output = await Task.detached(priority: .userInitiated) {
                ImageConverter.convert(urls, to: format, quality: quality, maxDimension: maxDimension)
            }.value
            withAnimation(.spring(response: 0.3)) {
                results = output + results
                inputs = []
            }
            converting = false
        }
    }
}

private struct Chip: View {
    @Environment(\.theme) private var theme
    let title: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(selected ? theme.background : theme.text)
                .padding(.horizontal, 9)
                .padding(.vertical, 4)
                .background(Capsule().fill(selected ? theme.accent : theme.text.opacity(0.1)))
        }
        .buttonStyle(.plain)
    }
}

private struct ResultRow: View {
    let result: ConversionResult
    @Environment(\.theme) private var theme

    var body: some View {
        ListRow {
            HStack(spacing: 10) {
                Group {
                    if let output = result.output {
                        FileThumbnailView(url: output, size: CGSize(width: 64, height: 64), contentMode: .fill)
                    } else {
                        Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
                    }
                }
                .frame(width: 30, height: 30)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                VStack(alignment: .leading, spacing: 3) {
                    Text(result.output?.lastPathComponent ?? result.sources.first?.lastPathComponent ?? "")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(theme.text)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if let error = result.error {
                        Text(error).font(.system(size: 11)).foregroundStyle(.orange)
                    } else {
                        HStack(spacing: 5) {
                            Text("\(Format.bytes(result.before)) → \(Format.bytes(result.after))")
                                .font(.system(size: 11))
                                .foregroundStyle(theme.secondaryText)
                            let pct = Int((result.saved * 100).rounded())
                            Tag(text: pct >= 0 ? "-\(pct)%" : "+\(-pct)%", color: pct >= 0 ? .green : .orange)
                        }
                    }
                }
                Spacer()
                if let output = result.output {
                    PillButton(title: "Reveal", icon: "magnifyingglass") { FileMeta.reveal([output]) }
                }
            }
        }
        .onDrag {
            guard let output = result.output else { return NSItemProvider() }
            return NSItemProvider(contentsOf: output) ?? NSItemProvider(object: output as NSURL)
        }
    }
}
