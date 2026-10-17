import SwiftUI
import EventKit

/// EventKit access: 7-day agenda, incomplete reminders, and the next upcoming event.
@MainActor
final class CalendarService: ObservableObject {
    static let shared = CalendarService()

    let store = EKEventStore()

    @Published private(set) var eventsAuthorized = false
    @Published private(set) var remindersAuthorized = false
    @Published private(set) var eventsDenied = false
    @Published private(set) var events: [EKEvent] = []
    @Published private(set) var reminders: [EKReminder] = []
    /// The next event that hasn't ended yet (not all-day), for the Home widget.
    @Published private(set) var nextEvent: EKEvent?

    private var observer: NSObjectProtocol?

    private init() {
        updateStatus()
        observer = NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: store, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    private func updateStatus() {
        let e = EKEventStore.authorizationStatus(for: .event)
        let r = EKEventStore.authorizationStatus(for: .reminder)
        eventsAuthorized = e == .fullAccess
        remindersAuthorized = r == .fullAccess
        eventsDenied = e == .denied || e == .restricted
    }

    var needsAccess: Bool { !eventsAuthorized || !remindersAuthorized }

    /// Asks for full access to events and reminders (shows the system prompt once).
    func requestAccess() {
        Task { @MainActor in
            NSApp.activate(ignoringOtherApps: true)
            _ = try? await store.requestFullAccessToEvents()
            _ = try? await store.requestFullAccessToReminders()
            updateStatus()
            if eventsDenied {
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Calendars") {
                    NSWorkspace.shared.open(url)
                }
            }
            refresh()
        }
    }

    /// Reloads data if access was already granted. Never prompts.
    func refresh() {
        updateStatus()
        let cal = Calendar.current
        let start = cal.startOfDay(for: Date())
        if eventsAuthorized, let end = cal.date(byAdding: .day, value: 7, to: start) {
            let predicate = store.predicateForEvents(withStart: start, end: end, calendars: nil)
            events = store.events(matching: predicate).sorted { $0.startDate < $1.startDate }
            let now = Date()
            nextEvent = events.first { !$0.isAllDay && $0.endDate > now }
        } else {
            events = []
            nextEvent = nil
        }
        if remindersAuthorized {
            let predicate = store.predicateForIncompleteReminders(withDueDateStarting: nil, ending: nil, calendars: nil)
            store.fetchReminders(matching: predicate) { [weak self] result in
                let items = result ?? []
                DispatchQueue.main.async {
                    self?.reminders = items.sorted { a, b in
                        let da = a.dueDateComponents.flatMap { Calendar.current.date(from: $0) } ?? .distantFuture
                        let db = b.dueDateComponents.flatMap { Calendar.current.date(from: $0) } ?? .distantFuture
                        return da < db
                    }
                }
            }
        } else {
            reminders = []
        }
    }

    func complete(_ reminder: EKReminder) {
        reminder.isCompleted = true
        do {
            try store.save(reminder, commit: true)
            withAnimation { reminders.removeAll { $0.calendarItemIdentifier == reminder.calendarItemIdentifier } }
        } catch {
            reminder.isCompleted = false
            NSLog("Purrch reminders: \(error.localizedDescription)")
        }
    }
}

// MARK: - View

struct CalendarModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var service = CalendarService.shared

    var body: some View {
        Group {
            if !service.eventsAuthorized && !service.remindersAuthorized {
                Card("Calendar", icon: "calendar") {
                    VStack(spacing: 12) {
                        EmptyState(icon: "calendar.badge.lock", title: "Calendar access needed",
                                   subtitle: "Allow Purrch to read your events and reminders.")
                            .frame(maxHeight: 120)
                        PillButton(title: "Grant access", icon: "lock.open", prominent: true) { service.requestAccess() }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            } else {
                HStack(spacing: 10) {
                    agendaCard
                    remindersCard.frame(width: 270)
                }
            }
        }
        .onAppear { service.refresh() }
    }

    // MARK: Agenda

    private var groupedDays: [(day: Date, events: [EKEvent])] {
        let cal = Calendar.current
        let today = cal.startOfDay(for: Date())
        var map: [Date: [EKEvent]] = [:]
        for event in service.events {
            let day = max(today, cal.startOfDay(for: event.startDate))
            map[day, default: []].append(event)
        }
        return map.keys.sorted().map { ($0, map[$0] ?? []) }
    }

    private var agendaCard: some View {
        Card("Next 7 days", icon: "calendar", accessory: {
            if !service.eventsAuthorized {
                Button("Grant access") { service.requestAccess() }.buttonStyle(.plain).foregroundStyle(theme.accent)
            } else {
                Text("\(service.events.count) events")
            }
        }) {
            if !service.eventsAuthorized {
                EmptyState(icon: "calendar.badge.lock", title: "No calendar access")
            } else if service.events.isEmpty {
                EmptyState(icon: "calendar.badge.checkmark", title: "Nothing scheduled", subtitle: "Your week is clear")
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        ForEach(groupedDays, id: \.day) { group in
                            VStack(alignment: .leading, spacing: 5) {
                                Text(dayTitle(group.day).uppercased())
                                    .font(.system(size: 10, weight: .bold)).tracking(0.8)
                                    .foregroundStyle(Calendar.current.isDateInToday(group.day) ? theme.accent : theme.secondaryText)
                                ForEach(group.events, id: \.calendarItemIdentifier) { event in
                                    eventRow(event)
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private func eventRow(_ event: EKEvent) -> some View {
        HStack(spacing: 10) {
            Capsule()
                .fill(Color(nsColor: event.calendar?.color ?? .systemGray))
                .frame(width: 4, height: 26)
            VStack(alignment: .leading, spacing: 1) {
                Text(event.title ?? "Untitled")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                Text(timeText(event))
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText)
                    .monospacedDigit()
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            if let loc = event.location, !loc.isEmpty {
                Image(systemName: "mappin.and.ellipse").font(.system(size: 10)).foregroundStyle(theme.secondaryText)
                    .help(loc)
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.text.opacity(0.05)))
    }

    private func dayTitle(_ day: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(day) { return "Today" }
        if cal.isDateInTomorrow(day) { return "Tomorrow" }
        return day.formatted(.dateTime.weekday(.wide).day().month(.abbreviated))
    }

    private func timeText(_ event: EKEvent) -> String {
        if event.isAllDay { return "All day" }
        let f = Date.FormatStyle.dateTime.hour().minute()
        return "\(event.startDate.formatted(f)) – \(event.endDate.formatted(f))"
    }

    // MARK: Reminders

    private var remindersCard: some View {
        Card("Reminders", icon: "checklist", accessory: {
            if !service.remindersAuthorized {
                Button("Grant access") { service.requestAccess() }.buttonStyle(.plain).foregroundStyle(theme.accent)
            } else {
                Text("\(service.reminders.count)")
            }
        }) {
            if !service.remindersAuthorized {
                EmptyState(icon: "checklist", title: "No reminders access")
            } else if service.reminders.isEmpty {
                EmptyState(icon: "checkmark.circle", title: "All done")
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(service.reminders, id: \.calendarItemIdentifier) { reminder in
                            ReminderRow(reminder: reminder) { service.complete(reminder) }
                        }
                    }
                }
            }
        }
    }
}

private struct ReminderRow: View {
    @Environment(\.theme) private var theme
    let reminder: EKReminder
    let onComplete: () -> Void
    @State private var checked = false
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Button {
                withAnimation(.easeOut(duration: 0.2)) { checked = true }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { onComplete() }
            } label: {
                Image(systemName: checked ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 15))
                    .foregroundStyle(checked || hovering ? theme.accent : Color(nsColor: reminder.calendar?.color ?? .systemGray))
            }
            .buttonStyle(.plain)
            .onHover { hovering = $0 }
            VStack(alignment: .leading, spacing: 1) {
                Text(reminder.title ?? "Untitled")
                    .font(.system(size: 13))
                    .foregroundStyle(checked ? theme.secondaryText : theme.text)
                    .strikethrough(checked)
                    .lineLimit(2)
                if let due = reminder.dueDateComponents.flatMap({ Calendar.current.date(from: $0) }) {
                    Text(due.formatted(.dateTime.weekday(.abbreviated).hour().minute()))
                        .font(.system(size: 10))
                        .foregroundStyle(due < Date() ? Color.red : theme.secondaryText)
                }
            }
            Spacer(minLength: 0)
        }
    }
}
