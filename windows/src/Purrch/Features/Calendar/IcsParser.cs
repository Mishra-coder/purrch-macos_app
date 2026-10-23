using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Purrch.Features.Calendar;

/// One occurrence in the agenda, in local time. All-day events run from midnight to midnight.
public sealed record AgendaEvent(string Id, string Title, DateTime Start, DateTime End, bool IsAllDay,
                                 string? Location, Guid SourceId);

/// An open to-do: a VTODO from a calendar feed, a To Do or Google task, or one added in Purrch (no source).
/// DueIsDate: the due date has no time (To Do and Google Tasks keep only the day).
public sealed record ReminderItem(string Id, string Title, DateTime? Due, Guid? SourceId, bool DueIsDate = false);

/// What one .ics file holds for the coming days.
public sealed record FeedContent(string? Name, List<AgendaEvent> Events, List<ReminderItem> Todos);

/// Reads iCalendar (.ics) text with Ical.Net, which expands repeating events (RRULE, EXDATE and moved
/// single occurrences) and converts time zones.
public static class IcsParser
{
    static IcsParser()
    {
        // Outlook can write zones like "Customized Time Zone" that no database knows; one such event would
        // otherwise fail the whole feed. Those are almost always the sender's own zone, so use this PC's.
        var known = TimeZoneResolvers.Default;
        TimeZoneResolvers.TimeZoneResolver = id =>
        {
            try { return known(id); }
            catch (ArgumentException) { return NodaTime.DateTimeZoneProviders.Tzdb.GetSystemDefault(); }
        };
    }

    /// Events overlapping [from, to) and open to-dos. Throws on text that isn't iCalendar at all.
    public static FeedContent Parse(string text, Guid sourceId, DateTime from, DateTime to)
    {
        var calendar = Ical.Net.Calendar.Load(text) ?? throw new FormatException("Not an iCalendar file");
        var name = calendar.Properties["X-WR-CALNAME"]?.Value?.ToString()?.Trim();

        // Start a day early so events already under way (overnight, multi-day) are found too.
        var start = new CalDateTime(from.AddDays(-1).ToUniversalTime(), "UTC");
        var end = new CalDateTime(to.ToUniversalTime(), "UTC");
        var events = new List<AgendaEvent>();
        foreach (var occurrence in Occurrences(calendar, start, end))
        {
            if (occurrence.Source is not CalendarEvent e) continue;
            if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var allDay = !occurrence.Period.StartTime.HasTime;
            var begin = Local(occurrence.Period.StartTime);
            var finish = occurrence.Period.EffectiveEndTime is { } endTime ? Local(endTime) : begin;
            if (allDay && finish <= begin) finish = begin.AddDays(1);
            // Zero-length events (reminders, deadlines) count when they fall inside the window.
            var overlaps = begin < to && (finish > from || (finish == begin && begin >= from));
            if (!overlaps) continue;
            var location = e.Location?.Trim();
            events.Add(new AgendaEvent($"{e.Uid}@{begin.Ticks}", Clean(e.Summary), begin, finish, allDay,
                string.IsNullOrEmpty(location) ? null : location, sourceId));
        }

        var todos = new List<ReminderItem>();
        foreach (var t in calendar.Todos)
        {
            if (t.Completed != null || string.Equals(t.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            todos.Add(new ReminderItem(t.Uid ?? Clean(t.Summary), Clean(t.Summary), t.Due is { } due ? Local(due) : null, sourceId));
        }
        return new FeedContent(string.IsNullOrEmpty(name) ? null : name, events, todos);
    }

    /// The whole calendar at once, so moved occurrences replace the ones they override. If one odd event
    /// breaks that, fall back to event by event and skip the ones that fail.
    private static IEnumerable<Occurrence> Occurrences(Ical.Net.Calendar calendar, CalDateTime start, CalDateTime end)
    {
        try
        {
            return calendar.GetOccurrences<CalendarEvent>(start).TakeWhileBefore(end).ToList();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException
                                      or OverflowException or NullReferenceException)
        {
            var list = new List<Occurrence>();
            foreach (var ev in calendar.Events)
            {
                try { list.AddRange(ev.GetOccurrences(start).TakeWhileBefore(end)); }
                catch (Exception inner) when (inner is ArgumentException or InvalidOperationException
                                                  or FormatException or OverflowException or NullReferenceException) { }
            }
            return list;
        }
    }

    /// Floating times ("3 pm wherever you are") and dates stay as written; zoned and UTC times become local.
    private static DateTime Local(CalDateTime time)
    {
        if (!time.HasTime) return time.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        if (time.IsFloating) return DateTime.SpecifyKind(time.Value, DateTimeKind.Local);
        return time.AsUtc.ToLocalTime();
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Untitled" : text.Trim().ReplaceLineEndings(" ");
}
