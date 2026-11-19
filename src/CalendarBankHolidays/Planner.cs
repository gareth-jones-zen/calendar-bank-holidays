using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CalendarBankHolidays;

/// <summary>The two kinds of entry the tool writes.</summary>
public enum Kind { AllDay, Block }

/// <summary>A calendar event as read, local time. <see cref="OwnedDay"/> and <see cref="OwnedKind"/> are set when
/// calendar-bank-holidays wrote it.</summary>
public sealed record CalEvent(string Id, string Subject, DateTime Start, DateTime End, bool AllDay, bool Mine, int Attendees, bool Cancelled)
{
    public DateOnly? OwnedDay { get; init; }
    public Kind OwnedKind { get; init; }
    public string Hash { get; init; } = "";
}

public enum Action { Keep, Create, Update, Adopt, Delete, Skip }

/// <summary>One entry for a bank holiday (or one of the tool's for a day that no longer is one) and what to do.</summary>
public sealed record DayPlan(DateOnly Day, Kind Kind, string Name, JsonObject? Want, CalEvent? Have, Action Action, string Note = "");

public static partial class Planner
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>What to change, from first to last. Each bank holiday gets an all-day entry ([all_day], unless one with
    /// its name is already there and skip_if_exists) and a time block ([block]), each when switched on; a hand-made
    /// timed entry matching [adopt] is taken over as the block; the tool's entries for a day that isn't one, or a kind
    /// switched off, go. Pure.</summary>
    public static List<DayPlan> Plan(Config config, IReadOnlyList<Holiday> holidays, IReadOnlyList<CalEvent> events, DateOnly first, DateOnly last)
    {
        var owned = events.Where(e => e.OwnedDay is { } d && d >= first && d <= last)
            .GroupBy(e => (e.OwnedDay!.Value, e.OwnedKind)).ToDictionary(g => g.Key, g => g.ToList());
        var others = events.Where(e => e.OwnedDay is null && !e.Cancelled).ToList();
        var plans = new List<DayPlan>();
        var days = holidays.Where(h => h.Date >= first && h.Date <= last).ToList();
        var wanted = new HashSet<(DateOnly, Kind)>();
        foreach (var holiday in days)
        {
            foreach (var kind in new[] { Kind.AllDay, Kind.Block })
            {
                var settings = kind == Kind.AllDay ? config.AllDay : config.Block;
                if (!settings.Enabled)
                    continue;
                wanted.Add((holiday.Date, kind));
                var want = Body(config, kind, holiday);
                var have = owned.GetValueOrDefault((holiday.Date, kind)) ?? [];
                foreach (var extra in have.Skip(1))
                    plans.Add(new DayPlan(holiday.Date, kind, holiday.Name, null, extra, Action.Delete, "a second one for the day"));
                if (have.FirstOrDefault() is { } current)
                {
                    plans.Add(new DayPlan(holiday.Date, kind, holiday.Name, want, current, Differs(current, want) ? Action.Update : Action.Keep));
                    continue;
                }
                if (kind == Kind.AllDay && settings.SkipIfExists
                    && others.FirstOrDefault(e => e.AllDay && DateOnly.FromDateTime(e.Start) <= holiday.Date && DateOnly.FromDateTime(e.End.AddMinutes(-1)) >= holiday.Date
                                                  && SameName(e.Subject, holiday.Name)) is { } there)
                {
                    plans.Add(new DayPlan(holiday.Date, kind, holiday.Name, want, null, Action.Skip, $"\"{there.Subject}\" is already there"));
                    continue;
                }
                var hand = kind == Kind.Block && config.Adopt is { } adopt
                    ? others.FirstOrDefault(e => e.Mine && e.Attendees == 0 && !e.AllDay && adopt.IsMatch(e.Subject) && DateOnly.FromDateTime(e.Start) == holiday.Date)
                    : null;
                plans.Add(hand is not null
                    ? new DayPlan(holiday.Date, kind, holiday.Name, want, hand, Action.Adopt)
                    : new DayPlan(holiday.Date, kind, holiday.Name, want, null, Action.Create));
            }
        }
        foreach (var ((day, kind), entries) in owned)
            if (!wanted.Contains((day, kind)))
                foreach (var e in entries)
                    plans.Add(new DayPlan(day, kind, e.Subject, null, e, Action.Delete,
                        days.Any(h => h.Date == day) ? "switched off" : "not a bank holiday"));
        return plans.OrderBy(p => p.Day).ThenBy(p => p.Kind).ToList();
    }

    /// <summary>Whether an entry's subject is this holiday's name: ignoring case, the kind of apostrophe, and a trailing
    /// "(observed)" or "(substitute day)". Outlook's holiday calendar writes "Christmas Day", "New Year's Day".</summary>
    public static bool SameName(string subject, string name) => Plain(subject) == Plain(name);

    private static string Plain(string text) =>
        Spaces().Replace(Suffix().Replace(text.Replace('’', '\''), ""), " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\((observed|substitute day)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The name for a title: GOV.UK's, without a trailing "bank holiday" ("Early May bank holiday" is "Early
    /// May"), unless that's all it is.</summary>
    public static string ShortName(string name)
    {
        var trimmed = BankHolidaySuffix().Replace(name, "");
        return trimmed.Length > 0 ? trimmed : name;
    }

    [GeneratedRegex(@"\s*bank holiday\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BankHolidaySuffix();

    /// <summary>A title from a template: {name} is the holiday's full name, {short} without a trailing "bank holiday",
    /// {note} " (substitute day)" when it is one.</summary>
    public static string Title(string template, Holiday holiday) =>
        template.Replace("{name}", holiday.Name, StringComparison.Ordinal)
            .Replace("{short}", ShortName(holiday.Name), StringComparison.Ordinal)
            .Replace("{note}", holiday.Notes.Contains("substitute", StringComparison.OrdinalIgnoreCase) ? " (substitute day)" : "", StringComparison.Ordinal);

    public static JsonObject Body(Config config, Kind kind, Holiday holiday)
    {
        var settings = kind == Kind.AllDay ? config.AllDay : config.Block;
        var (start, end) = kind == Kind.AllDay
            ? (holiday.Date.ToDateTime(TimeOnly.MinValue), holiday.Date.AddDays(1).ToDateTime(TimeOnly.MinValue))
            : (holiday.Date.ToDateTime(settings.From), holiday.Date.ToDateTime(settings.To));
        return new JsonObject
        {
            ["subject"] = Title(settings.Title, holiday),
            ["start"] = new JsonObject { ["dateTime"] = start.ToString("yyyy-MM-ddTHH:mm:ss", En), ["timeZone"] = config.TimeZone.Id },
            ["end"] = new JsonObject { ["dateTime"] = end.ToString("yyyy-MM-ddTHH:mm:ss", En), ["timeZone"] = config.TimeZone.Id },
            ["isAllDay"] = kind == Kind.AllDay,
            ["showAs"] = settings.ShowAs,
            ["categories"] = new JsonArray(settings.Categories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["sensitivity"] = "normal",
            ["isReminderOn"] = false,
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = config.Body },
        };
    }

    public static string Digest(JsonObject body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToJsonString())))[..16];

    /// <summary>Whether an entry needs rewriting: what it should say changed, or someone edited its title or times.</summary>
    public static bool Differs(CalEvent have, JsonObject want) =>
        have.Hash != Digest(want) || have.Subject != (string)want["subject"]! || have.AllDay != (bool)want["isAllDay"]!
        || have.Start != DateTime.Parse((string)want["start"]!["dateTime"]!, En) || have.End != DateTime.Parse((string)want["end"]!["dateTime"]!, En);
}
