using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CalendarBankHolidays;

/// <summary>GOV.UK's list of bank holidays (https://www.gov.uk/bank-holidays.json): each region's dates and names,
/// usually to the end of the year after next, substitute days included.</summary>
public static class Feed
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>A region's days from the feed's JSON. Throws FormatException if it isn't the shape expected, or the
    /// region has no days.</summary>
    public static List<Holiday> Parse(string json, string region)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The bank holiday list isn't a JSON object.");
        var events = root[region]?["events"] as JsonArray ?? throw new FormatException($"The bank holiday list has no {region}.");
        var days = new List<Holiday>();
        foreach (var e in events.OfType<JsonObject>())
        {
            var date = e["date"] is JsonValue d && d.TryGetValue<string>(out var text)
                       && DateOnly.TryParseExact(text, "yyyy-MM-dd", En, DateTimeStyles.None, out var day)
                ? day : throw new FormatException("A bank holiday in the list has no date.");
            var name = e["title"] is JsonValue t && t.TryGetValue<string>(out var title) && title.Length > 0 ? title.Replace('’', '\'') : "Bank holiday";
            var notes = e["notes"] is JsonValue n && n.TryGetValue<string>(out var note) ? note : "";
            days.Add(new Holiday(date, name, notes));
        }
        if (days.Count == 0)
            throw new FormatException($"The bank holiday list has no days for {region}.");
        return days.OrderBy(h => h.Date).ToList();
    }

    /// <summary>The days to use: GOV.UK's, read at most once a day (the copy kept in cache_file otherwise), plus the
    /// settings' extra days, less its skipped ones. A failed download falls back to the kept copy, and says so.</summary>
    public static (List<Holiday> Days, string Note) Get(Config config, Func<string, string> download, DateTime now)
    {
        var today = now.ToString("yyyy-MM-dd", En);
        var cached = File.Exists(config.CacheFile) ? File.ReadAllText(config.CacheFile) : null;
        var state = Cli.LoadState(config.StateFile);
        var fetched = state["fetched"] is JsonValue f && f.TryGetValue<string>(out var when) ? when : null;
        string json;
        string note;
        if (cached is not null && fetched == today)
        {
            json = cached;
            note = "GOV.UK's list, read today";
        }
        else
        {
            try
            {
                json = download(config.FeedUrl);
                Parse(json, config.Region);   // check it before keeping it
                Directory.CreateDirectory(Path.GetDirectoryName(config.CacheFile)!);
                File.WriteAllText(config.CacheFile + ".tmp", json);
                File.Move(config.CacheFile + ".tmp", config.CacheFile, true);
                state["fetched"] = today;
                Cli.SaveState(config.StateFile, state);
                note = "GOV.UK's list, read now";
            }
            catch (Exception error) when (cached is not null && error is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or IOException)
            {
                json = cached;
                note = $"GOV.UK's list couldn't be read ({error.Message}), so the copy read on {fetched ?? "an earlier day"} was used";
            }
        }
        var days = Parse(json, config.Region).Where(h => !config.Skip.Contains(h.Date)).ToList();
        foreach (var extra in config.Extra.Where(x => !days.Any(d => d.Date == x.Date)))
            days.Add(extra);
        return (days.OrderBy(d => d.Date).ToList(), note);
    }

    public static string Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("calendar-bank-holidays (+https://github.com/gareth-jones-zen/calendar-bank-holidays)");
        return http.GetStringAsync(new Uri(url)).GetAwaiter().GetResult();
    }
}

