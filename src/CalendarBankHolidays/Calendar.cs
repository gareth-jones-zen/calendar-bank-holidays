using System.Globalization;
using System.Text.Json.Nodes;
using M365Graph;
using WinBackground;

namespace CalendarBankHolidays;

/// <summary>What a run reads and writes in your calendar. GraphCalendar is the real one; tests give a fake.</summary>
public interface ICalendar
{
    /// <summary>Every event from first to last (inclusive), with calendar-bank-holidays' property where it wrote them.</summary>
    IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last);

    string Create(JsonObject body);

    /// <summary>Rewrite an entry, stamping it as calendar-bank-holidays' own (also how a hand-made one is taken over).</summary>
    void Update(string id, JsonObject body);

    void Delete(string id);
}

/// <summary>Your Outlook calendar through Microsoft Graph (Calendars.ReadWrite). An entry is calendar-bank-holidays' only if it
/// carries its extended property (the day it stands for), plus a hash of what was written; the calendar is the only
/// record. Nothing without the property is changed, except a hand-made entry on a bank holiday that the [adopt]
/// pattern matches, which is taken over: rewritten as the tool's own, not deleted.</summary>
public sealed class GraphCalendar(GraphClient graph, TimeZoneInfo zone) : ICalendar
{
    public static readonly string[] Scopes = ["Calendars.ReadWrite"];
    public const string PropertySet = "{2c8e5b41-7d3a-4f19-a6e2-91b0d4c37f58}";   // calendar-bank-holidays' own; never change it
    public const string DayProp = $"String {PropertySet} Name calendar-bank-holidays.day";
    public const string HashProp = $"String {PropertySet} Name calendar-bank-holidays.hash";

    private const string Select = "subject,start,end,isAllDay,isOrganizer,attendees,isCancelled";

    private Dictionary<string, string> Headers => new() { ["Prefer"] = $"outlook.timezone=\"{zone.Id}\"" };

    private string Utc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last)
    {
        var expand = Uri.EscapeDataString($"singleValueExtendedProperties($filter=id eq '{DayProp}' or id eq '{HashProp}')");
        var url = $"/me/calendarView?startDateTime={Utc(first)}&endDateTime={Utc(last.AddDays(1))}&$select={Select}&$expand={expand}&$top=250";
        return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>().Select(Read).ToList();
    }

    public string Create(JsonObject body)
    {
        var reply = graph.RequestAsync("/me/events", HttpMethod.Post, Stamped(body), Scopes, Headers).GetAwaiter().GetResult();
        return Text(reply, "id") ?? "";
    }

    /// <summary>A create is sent once (the library's default for a POST): a 5xx or a lost answer throws
    /// <see cref="GraphDeliveryUnknownException"/>, and the next run reads the calendar back by the stamp, so an entry
    /// that did land is kept, never added twice. An update is retried like a read: it rewrites the whole of one of the
    /// tool's own entries, or a hand-made one of yours with no attendees, so a repeat lands the same and tells nobody.</summary>
    public void Update(string id, JsonObject body) =>
        graph.RequestAsync($"/me/events/{Uri.EscapeDataString(id)}", HttpMethod.Patch, Stamped(body), Scopes, Headers,
            policy: GraphRetryPolicy.Read).GetAwaiter().GetResult();

    public void Delete(string id) =>
        graph.RequestAsync($"/me/events/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, Scopes, Headers).GetAwaiter().GetResult();

    public static JsonObject Stamped(JsonObject body)
    {
        var output = (JsonObject)body.DeepClone();
        output["singleValueExtendedProperties"] = new JsonArray(
            new JsonObject { ["id"] = DayProp, ["value"] = ((string)body["start"]!["dateTime"]!)[..10] + ((bool)body["isAllDay"]! ? " all-day" : " block") },
            new JsonObject { ["id"] = HashProp, ["value"] = Planner.Digest(body) });
        return output;
    }

    internal static CalEvent Read(JsonObject o)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o["singleValueExtendedProperties"] is JsonArray list)
            foreach (var p in list.OfType<JsonObject>())
                if (Text(p, "id") is { } id)
                    props[id] = Text(p, "value") ?? "";
        // "2027-03-26 all-day" or "2027-03-26 block": the day it stands for, and which kind of entry.
        DateOnly? owned = props.TryGetValue(DayProp, out var d) && d.Length >= 10
                          && DateOnly.TryParseExact(d[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        var kind = d is not null && d.EndsWith(" block", StringComparison.Ordinal) ? Kind.Block : Kind.AllDay;
        return new CalEvent(Text(o, "id") ?? "", Text(o, "subject") ?? "", Time(o["start"]), Time(o["end"]), Flag(o, "isAllDay"),
            o["isOrganizer"] is not JsonValue v || !v.TryGetValue<bool>(out var organiser) || organiser,
            (o["attendees"] as JsonArray)?.OfType<JsonObject>().Count(a => Text(a, "type") != "resource") ?? 0,
            Flag(o, "isCancelled"))
        {
            OwnedDay = owned,
            OwnedKind = kind,
            Hash = props.GetValueOrDefault(HashProp, ""),
        };
    }

    internal static DateTime Time(JsonNode? node)
    {
        var text = (node as JsonObject) is { } o ? Text(o, "dateTime") : null;
        return text is null ? default : DateTime.Parse(text[..Math.Min(19, text.Length)], CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    internal static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static GraphClient Client(Config config) => new(new GraphClientOptions
    {
        CachePath = config.MsalCache ?? M365Graph.Graph.DefaultCachePath,
        Tenant = config.Tenant,
        LoginHint = config.LoginHint,
        Scopes = Scopes,
        SignInHint = "At a console, run: calendar-bank-holidays sign-in",
        Log = Logger.Create(config.LogFile),
    });
}
