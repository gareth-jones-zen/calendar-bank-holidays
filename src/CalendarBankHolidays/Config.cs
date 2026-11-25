using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;

namespace CalendarBankHolidays;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>A bank holiday: the date, its name, and GOV.UK's note ("Substitute day", or empty). Also a day added in the
/// settings (a company closure day, say).</summary>
public sealed record Holiday(DateOnly Date, string Name, string Notes = "");

/// <summary>One kind of entry the tool writes, [block] or [all_day]: whether it's on, how it looks, and (a block) its
/// hours, or (all day) whether an all-day entry already there for the day stops it being added.</summary>
public sealed record EntryKind(bool Enabled, string Title, string ShowAs, IReadOnlyList<string> Categories, TimeOnly From, TimeOnly To,
    bool SkipIfExists)
{
    public static EntryKind DefaultBlock { get; } =
        new(false, "Bank holiday", "oof", ["Bank holiday"], new TimeOnly(9, 0), new TimeOnly(17, 0), false);

    public static EntryKind DefaultAllDay { get; } =
        new(true, "{name}{note}", "free", ["Bank holiday"], TimeOnly.MinValue, TimeOnly.MinValue, true);
}

/// <summary>The settings file. See the README and <see cref="Template"/>. Read strictly: an unknown key, or a value of
/// the wrong type, is an error that names it.</summary>
public sealed class Config
{
    public const string EnvConfig = "CALENDAR_BANK_HOLIDAYS_CONFIG";
    public const string DefaultFeed = "https://www.gov.uk/bank-holidays.json";
    public const string DefaultAdopt = @"^\s*bank holiday\b";

    public static readonly string[] Regions = ["england-and-wales", "scotland", "northern-ireland"];
    public static readonly string[] ShowAsValues = ["free", "tentative", "busy", "oof", "workingElsewhere"];

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# calendar-bank-holidays settings. See the README.\n" +
        "# base = \"base-config.toml\"        # settings shared with other tools\n" +
        "# login_hint = \"you@example.com\"\n" +
        "# tenant = \"organizations\"          # or your organisation's tenant id\n" +
        "# time_zone = \"GMT Standard Time\"   # default: this PC's\n" +
        "\n" +
        "enabled = true\n" +
        "region = \"england-and-wales\"        # or scotland, northern-ireland\n" +
        "months_ahead = 12\n" +
        "\n" +
        "[schedule]                         # when the task runs (background-tools-lib's shared keys)\n" +
        "daily_at = \"07:00\"\n" +
        "at_logon = true\n" +
        "after_unlock_minutes = 0          # 0: no run after unlocking\n" +
        "\n" +
        "[all_day]                          # an all-day entry naming the day\n" +
        "enabled = true\n" +
        "title = \"{name}{note}\"             # {note}: \" (substitute day)\" when it is one\n" +
        "show_as = \"free\"\n" +
        "categories = [\"Bank holiday\"]\n" +
        "skip_if_exists = true              # not if an all-day entry with its name is already there\n" +
        "\n" +
        "[block]                            # time blocked out during the day\n" +
        "enabled = false\n" +
        "title = \"Bank holiday\"\n" +
        "hours = \"09:00-17:00\"\n" +
        "show_as = \"oof\"\n" +
        "categories = [\"Bank holiday\"]\n";

    public static readonly string[] KnownKeys =
    [
        "login_hint", "tenant", "msal_cache", "log_file", "state_file", "cache_file", "time_zone", "enabled", "region",
        "months_ahead", "long_range_months_ahead", "feed_url", "daily_at", "after_unlock_minutes", "notify", "max_deletes", "body", "block", "all_day", "adopt",
        "extra", "skip", "schedule", "journal_file",
    ];

    /// <summary>The task's schedule when the settings don't give one: daily at 07:00 and at logon, no unlock run.</summary>
    public static readonly ScheduleSettings ScheduleDefaults = new() { DailyAt = "07:00", AtLogon = true, AfterUnlockMinutes = 0 };

    /// <summary>The schedule keys this tool took at the top level before [schedule] (0.4.0): still read, logged as deprecated.</summary>
    public static readonly string[] OldScheduleKeys = ["daily_at", "after_unlock_minutes"];

    public required string Path { get; init; }
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }
    /// <summary>The shared activity journal (0.6.0; journal_file, usually from the base): one line for each entry added,
    /// taken over, updated or removed. Null writes none.</summary>
    public string? JournalFile { get; init; }
    public required string CacheFile { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public bool Enabled { get; init; } = true;
    public string Region { get; init; } = "england-and-wales";
    public int MonthsAhead { get; init; } = 12;
    public string FeedUrl { get; init; } = DefaultFeed;
    /// <summary>When the task runs ([schedule]; the shared keys are background-tools-lib's ScheduleSettings).</summary>
    public ScheduleSettings Schedule { get; init; } = ScheduleDefaults;
    /// <summary>The old top-level schedule keys this file still uses (empty when it uses [schedule]).</summary>
    public IReadOnlyList<string> DeprecatedScheduleKeys { get; init; } = [];
    public string DailyAt => Schedule.DailyAt ?? "07:00";
    public int? AfterUnlockMinutes => Schedule.AfterUnlockMinutes > 0 ? Schedule.AfterUnlockMinutes : null;
    public bool Notify { get; init; } = true;
    public int MaxDeletes { get; init; } = 5;
    public string Body { get; init; } = "Written by calendar-bank-holidays from GOV.UK's list. Changes made here are overwritten.";
    /// <summary>[block]: time blocked out during the day (off by default).</summary>
    public EntryKind Block { get; init; } = EntryKind.DefaultBlock;

    /// <summary>[all_day]: an all-day entry naming the day (on by default).</summary>
    public EntryKind AllDay { get; init; } = EntryKind.DefaultAllDay;

    /// <summary>Your own timed entries on a bank holiday matching this are taken over as the [block] (made the tool's),
    /// not duplicated.</summary>
    public Regex? Adopt { get; init; } = new(DefaultAdopt, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<Holiday> Extra { get; init; } = [];
    public IReadOnlyList<DateOnly> Skip { get; init; } = [];

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "calendar-bank-holidays"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "calendar-bank-holidays", "config.toml"));
    }

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `calendar-bank-holidays config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");

        var folder = System.IO.Path.GetDirectoryName(full)!;
        var expandWith = env;
        string PathSetting(string value)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, expandWith), expandWith);
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        }
        UsageException Bad(string message) => new($"{full}: {message}");
        string? Text(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v) ? v as string is { Length: > 0 } s ? s : throw Bad($"{where} should be text, and not empty.") : null;
        bool Flag(string key, bool fallback) =>
            raw.TryGetValue(key, out var v) ? v as bool? ?? throw Bad($"{key} should be true or false.") : fallback;
        int Whole(string key, int fallback, int min, int max) =>
            raw.TryGetValue(key, out var v) ? v is long n && n >= min && n <= max ? (int)n : throw Bad($"{key} should be a whole number from {min} to {max}.") : fallback;
        DateOnly Date(object? value, string where) => value switch
        {
            string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
            TomlDateTime { Kind: TomlDateTimeKind.LocalDate } t => new DateOnly(t.DateTime.Year, t.DateTime.Month, t.DateTime.Day),
            _ => throw Bad($"{where} should be a date, as 2027-12-24."),
        };

        TimeZoneInfo zone;
        var zoneName = Text(raw, "time_zone", "time_zone");
        try
        {
            zone = zoneName is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw Bad($"time_zone '{zoneName}' isn't a time zone this PC knows (a Windows name, such as \"GMT Standard Time\").");
        }

        var region = Text(raw, "region", "region") ?? "england-and-wales";
        if (!Regions.Contains(region))
            throw Bad($"region should be one of {string.Join(", ", Regions)}.");
        var oldScheduleKeys = OldScheduleKeys.Where(raw.ContainsKey).ToList();
        ScheduleSettings schedule;
        try
        {
            if (raw.TryGetValue("schedule", out var scheduleTable))
            {
                if (oldScheduleKeys.Count > 0)
                    throw Bad($"{string.Join(" and ", oldScheduleKeys)} and [schedule] are both given: keep [schedule] only.");
                schedule = ScheduleSettings.Parse(scheduleTable as TomlTable ?? throw Bad("schedule should be a [schedule] section."),
                    ScheduleDefaults, TaskKind.Job, $"{full}: [schedule]");
            }
            else
            {
                var old = ScheduleDefaults;
                if (Text(raw, "daily_at", "daily_at") is { } dailyAt)
                {
                    if (!TimeOnly.TryParseExact(dailyAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw Bad("daily_at should be a time, as \"07:00\".");
                    old = old with { DailyAt = dailyAt };
                }
                if (raw.ContainsKey("after_unlock_minutes"))
                    old = old with { AfterUnlockMinutes = Whole("after_unlock_minutes", 0, 0, 60) };
                schedule = ScheduleSettings.Parse(null, old, TaskKind.Job, $"{full}: [schedule]");
            }
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message);
        }

        EntryKind Kind(string name, EntryKind defaults)
        {
            var table = raw.TryGetValue(name, out var t) ? t as TomlTable ?? throw Bad($"{name} should be a [{name}] section.") : [];
            string[] known = name == "block"
                ? ["enabled", "title", "show_as", "categories", "hours"]
                : ["enabled", "title", "show_as", "categories", "skip_if_exists"];
            foreach (var key in table.Keys)
                if (!known.Contains(key))
                    throw Bad($"[{name}] has an unknown setting '{key}' (known: {string.Join(", ", known)}).");
            bool On(string key, bool fallback) =>
                table.TryGetValue(key, out var v) ? v as bool? ?? throw Bad($"{name}.{key} should be true or false.") : fallback;
            var showAs = Text(table, "show_as", $"{name}.show_as") ?? defaults.ShowAs;
            if (!ShowAsValues.Contains(showAs))
                throw Bad($"{name}.show_as should be one of {string.Join(", ", ShowAsValues)}.");
            IReadOnlyList<string> categories = table.TryGetValue("categories", out var c)
                ? c is TomlArray ca && ca.All(x => x is string) ? ca.Select(x => (string)x!).ToList() : throw Bad($"{name}.categories should be a list of text.")
                : defaults.Categories;
            var (from, to) = (defaults.From, defaults.To);
            if (Text(table, "hours", $"{name}.hours") is { } hours)
            {
                var parts = hours.Split('-');
                if (parts.Length != 2
                    || !TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                    || !TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
                    || to <= from)
                    throw Bad($"{name}.hours should be \"hh:mm-hh:mm\", as \"09:00-17:00\".");
            }
            return new EntryKind(On("enabled", defaults.Enabled), Text(table, "title", $"{name}.title") ?? defaults.Title, showAs, categories,
                from, to, On("skip_if_exists", defaults.SkipIfExists));
        }
        var block = Kind("block", EntryKind.DefaultBlock);
        var allDay = Kind("all_day", EntryKind.DefaultAllDay);
        if (!block.Enabled && !allDay.Enabled)
            throw Bad("[block] and [all_day] are both off: turn one on, or set enabled = false to stop the tool.");

        var adopt = raw.TryGetValue("adopt", out var ad) ? ad as TomlTable ?? throw Bad("adopt should be an [adopt] section.") : [];
        foreach (var key in adopt.Keys)
            if (key != "subjects")
                throw Bad($"[adopt] has an unknown setting '{key}' (known: subjects).");
        var adoptText = adopt.TryGetValue("subjects", out var asv) ? asv as string ?? throw Bad("adopt.subjects should be a pattern, or \"\" for none.") : DefaultAdopt;
        Regex? adoptPattern;
        try
        {
            adoptPattern = adoptText.Length == 0 ? null : new Regex(adoptText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException error)
        {
            throw Bad($"adopt.subjects isn't a valid pattern: {error.Message}");
        }

        var extra = new List<Holiday>();
        if (raw.TryGetValue("extra", out var ex))
        {
            IEnumerable<object?> items = ex switch
            {
                TomlArray a => a,
                TomlTableArray t => t,
                _ => throw Bad("extra should be a list, as [ { date = 2027-12-24, name = \"Christmas Eve closure\" } ]."),
            };
            foreach (var item in items)
            {
                if (item is not TomlTable day || day.Keys.Any(k => k is not ("date" or "name")))
                    throw Bad("each of extra should be { date = ..., name = ... }.");
                extra.Add(new Holiday(Date(day.TryGetValue("date", out var dv) ? dv : null, "extra's date"),
                    Text(day, "name", "extra's name") ?? throw Bad("each of extra needs a name.")));
            }
        }
        var skip = new List<DateOnly>();
        if (raw.TryGetValue("skip", out var sk))
        {
            if (sk is not TomlArray sa)
                throw Bad("skip should be a list of dates, as [2027-03-17].");
            skip.AddRange(sa.Select(x => Date(x, "each of skip")));
        }

        return new Config
        {
            Path = full,
            LoginHint = Text(raw, "login_hint", "login_hint"),
            Tenant = Text(raw, "tenant", "tenant") ?? "organizations",
            MsalCache = Text(raw, "msal_cache", "msal_cache") is { } cache ? PathSetting(cache) : null,
            LogFile = Text(raw, "log_file", "log_file") is { } log ? PathSetting(log) : PyPath.Pure(System.IO.Path.Combine(Local(env), "calendar-bank-holidays.log")),
            JournalFile = Text(raw, "journal_file", "journal_file") is { } journal ? PathSetting(journal) : null,
            StateFile = Text(raw, "state_file", "state_file") is { } state ? PathSetting(state) : PyPath.Pure(System.IO.Path.Combine(Local(env), "state.json")),
            CacheFile = Text(raw, "cache_file", "cache_file") is { } cf ? PathSetting(cf) : PyPath.Pure(System.IO.Path.Combine(Local(env), "calendar-bank-holidays.json")),
            TimeZone = zone,
            Enabled = Flag("enabled", true),
            Region = region,
            MonthsAhead = raw.ContainsKey("months_ahead") ? Whole("months_ahead", 12, 1, 36) : Whole("long_range_months_ahead", 12, 1, 36),   // own, else the base's shared long-range value, else 12
            FeedUrl = Text(raw, "feed_url", "feed_url") ?? DefaultFeed,
            Schedule = schedule,
            DeprecatedScheduleKeys = oldScheduleKeys,
            Notify = Flag("notify", true),
            MaxDeletes = Whole("max_deletes", 5, 0, 1000),
            Body = Text(raw, "body", "body") ?? "Written by calendar-bank-holidays from GOV.UK's list. Changes made here are overwritten.",
            Block = block,
            AllDay = allDay,
            Adopt = adoptPattern,
            Extra = extra,
            Skip = skip,
        };
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
