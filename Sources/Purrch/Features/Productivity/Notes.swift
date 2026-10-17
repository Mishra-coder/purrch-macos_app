import SwiftUI

struct NoteCheckItem: Codable, Identifiable, Equatable {
    var id = UUID()
    var text: String
    var done = false
}

struct NoteItem: Codable, Identifiable, Equatable {
    var id = UUID()
    var title = ""
    var body = ""
    var items: [NoteCheckItem] = []
    var isChecklist = true
    var updated = Date()

    var displayTitle: String {
        let t = title.trimmingCharacters(in: .whitespaces)
        return t.isEmpty ? "Untitled" : t
    }

    var preview: String {
        if isChecklist {
            if items.isEmpty { return "Empty list" }
            let done = items.filter(\.done).count
            let next = items.first(where: { !$0.done })?.text ?? "All done"
            return "\(done)/\(items.count) · \(next)"
        }
        let line = body.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
        return line.isEmpty ? "No text" : line
    }
}

@MainActor
final class NotesStore: ObservableObject {
    static let shared = NotesStore()

    @Published var notes: [NoteItem]
    @Published var selection: UUID?
    private let saver = DebouncedSaver(delay: 0.7)

    private init() {
        var loaded = Store.load("notes", default: [NoteItem]())
        if loaded.isEmpty {
            loaded = [NoteItem(title: "Today",
                               items: [NoteCheckItem(text: "Try the checklist"),
                                       NoteCheckItem(text: "Switch to text mode with the toggle")])]
        }
        notes = loaded
        selection = loaded.sorted { $0.updated > $1.updated }.first?.id
    }

    func changed() {
        saver.schedule { [weak self] in
            guard let self else { return }
            Store.save("notes", self.notes)
        }
    }

    func flush() { saver.flush() }

    func add() {
        let note = NoteItem()
        notes.insert(note, at: 0)
        selection = note.id
        changed()
    }

    func delete(_ id: UUID) {
        notes.removeAll { $0.id == id }
        if selection == id { selection = notes.sorted { $0.updated > $1.updated }.first?.id }
        changed()
    }

    func binding(_ id: UUID) -> Binding<NoteItem>? {
        guard notes.contains(where: { $0.id == id }) else { return nil }
        return Binding(
            get: { [weak self] in self?.notes.first(where: { $0.id == id }) ?? NoteItem() },
            set: { [weak self] newValue in
                guard let self, let index = self.notes.firstIndex(where: { $0.id == id }) else { return }
                guard self.notes[index] != newValue else { return }
                var value = newValue
                value.updated = Date()
                self.notes[index] = value
                self.changed()
            }
        )
    }
}

struct NotesModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var store = NotesStore.shared
    @State private var query = ""

    private var filtered: [NoteItem] {
        let sorted = store.notes.sorted { $0.updated > $1.updated }
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return sorted }
        return sorted.filter { note in
            note.title.lowercased().contains(q) || note.body.lowercased().contains(q)
                || note.items.contains { $0.text.lowercased().contains(q) }
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            sidebar.frame(width: 230)
            editor
        }
        .onDisappear { store.flush() }
    }

    private var sidebar: some View {
        VStack(spacing: 8) {
            HStack(spacing: 6) {
                SearchField(text: $query, placeholder: "Search notes")
                IconButton(systemName: "plus", size: 32, prominent: true, help: "New note") {
                    query = ""
                    store.add()
                }
            }
            if filtered.isEmpty {
                EmptyState(icon: "note.text", title: query.isEmpty ? "No notes" : "No matches")
            } else {
                ScrollView {
                    LazyVStack(spacing: 5) {
                        ForEach(filtered) { note in
                            NoteRow(note: note, selected: note.id == store.selection)
                                .onTapGesture { store.selection = note.id }
                                .contextMenu {
                                    Button(note.isChecklist ? "Convert to Text" : "Convert to Checklist") {
                                        store.binding(note.id)?.wrappedValue.isChecklist.toggle()
                                    }
                                    Divider()
                                    Button("Delete", role: .destructive) { store.delete(note.id) }
                                }
                        }
                    }
                }
                .scrollIndicators(.never)
            }
        }
    }

    @ViewBuilder
    private var editor: some View {
        if let id = store.selection, let binding = store.binding(id) {
            NoteEditor(note: binding, onDelete: { store.delete(id) })
                .id(id)
        } else {
            Card("Note", icon: "list.bullet.rectangle") {
                EmptyState(icon: "square.and.pencil", title: "Pick or create a note",
                           subtitle: "Press + to start a new list")
            }
        }
    }
}

private struct NoteRow: View {
    @Environment(\.theme) private var theme
    let note: NoteItem
    let selected: Bool
    @State private var hovering = false

    var body: some View {
        let accent = theme.accent(for: .notes)
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 5) {
                Image(systemName: note.isChecklist ? "checklist" : "text.alignleft")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(selected ? accent : theme.secondaryText)
                Text(note.displayTitle)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                Spacer(minLength: 4)
                Text(Format.relative(note.updated))
                    .font(.system(size: 10))
                    .foregroundStyle(theme.secondaryText)
            }
            Text(note.preview)
                .font(.system(size: 11))
                .foregroundStyle(theme.secondaryText)
                .lineLimit(1)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(selected ? accent.opacity(0.16) : (hovering ? theme.text.opacity(0.07) : theme.surface))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(selected ? accent.opacity(0.6) : theme.border.opacity(0.5), lineWidth: 0.7)
        )
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

private struct NoteEditor: View {
    @Environment(\.theme) private var theme
    @Binding var note: NoteItem
    let onDelete: () -> Void
    @State private var newItem = ""
    @FocusState private var addFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                TextField("Title", text: $note.title)
                    .textFieldStyle(.plain)
                    .font(.system(size: 15, weight: .bold))
                    .foregroundStyle(theme.text)
                modeToggle
                IconButton(systemName: "trash", size: 26, help: "Delete note", action: onDelete)
            }
            if note.isChecklist { checklist } else { textEditor }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(theme.surface))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(theme.border, lineWidth: theme.glow ? 1.2 : 0.8))
    }

    private var modeToggle: some View {
        HStack(spacing: 2) {
            modeButton("checklist", active: note.isChecklist, help: "Checklist") { note.isChecklist = true }
            modeButton("text.alignleft", active: !note.isChecklist, help: "Full text") { note.isChecklist = false }
        }
        .padding(2)
        .background(Capsule().fill(theme.text.opacity(0.08)))
    }

    private func modeButton(_ icon: String, active: Bool, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(active ? theme.background : theme.secondaryText)
                .frame(width: 30, height: 22)
                .background(Capsule().fill(active ? theme.accent(for: .notes) : .clear))
        }
        .buttonStyle(.plain)
        .help(help)
    }

    private var checklist: some View {
        VStack(spacing: 6) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach($note.items) { $item in
                            ChecklistRow(item: $item) {
                                note.items.removeAll { $0.id == item.id }
                            }
                            .id(item.id)
                        }
                    }
                }
                .scrollIndicators(.never)
                .onChange(of: note.items.count) { _, _ in
                    if let last = note.items.last { proxy.scrollTo(last.id, anchor: .bottom) }
                }
                .overlay {
                    if note.items.isEmpty {
                        Text("Nothing here yet. Type below and press Return.")
                            .font(.system(size: 12))
                            .foregroundStyle(theme.secondaryText)
                    }
                }
            }
            HStack(spacing: 8) {
                Image(systemName: "plus.circle.fill")
                    .foregroundStyle(theme.accent(for: .notes))
                TextField("Add an item", text: $newItem)
                    .textFieldStyle(.plain)
                    .foregroundStyle(theme.text)
                    .focused($addFocused)
                    .onSubmit(addItem)
                if !note.items.isEmpty {
                    let done = note.items.filter(\.done).count
                    Text("\(done)/\(note.items.count)")
                        .font(.system(size: 11, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(theme.secondaryText)
                    if done > 0 {
                        Button("Clear done") { note.items.removeAll(where: \.done) }
                            .buttonStyle(.plain)
                            .font(.system(size: 11, weight: .medium))
                            .foregroundStyle(theme.secondaryText)
                    }
                }
            }
            .font(.system(size: 13))
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.text.opacity(0.06)))
        }
    }

    private func addItem() {
        let text = newItem.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        note.items.append(NoteCheckItem(text: text))
        newItem = ""
        addFocused = true
    }

    private var textEditor: some View {
        TextEditor(text: $note.body)
            .font(.system(size: 13))
            .foregroundStyle(theme.text)
            .scrollContentBackground(.hidden)
            .scrollIndicators(.never)
            .padding(6)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.text.opacity(0.05)))
    }
}

private struct ChecklistRow: View {
    @Environment(\.theme) private var theme
    @Binding var item: NoteCheckItem
    let onDelete: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 8) {
            Button {
                withAnimation(.easeOut(duration: 0.15)) { item.done.toggle() }
            } label: {
                Image(systemName: item.done ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 15))
                    .foregroundStyle(item.done ? theme.accent(for: .notes) : theme.secondaryText)
            }
            .buttonStyle(.plain)
            TextField("", text: $item.text)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .strikethrough(item.done, color: theme.secondaryText)
                .foregroundStyle(item.done ? theme.secondaryText : theme.text)
            if hovering {
                Button(action: onDelete) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(theme.secondaryText)
                }
                .buttonStyle(.plain)
                .help("Delete item")
            }
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 4)
        .background(RoundedRectangle(cornerRadius: 8).fill(hovering ? theme.text.opacity(0.06) : .clear))
        .onHover { hovering = $0 }
    }
}
