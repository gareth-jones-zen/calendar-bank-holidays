using System.Text.Json.Nodes;
using WinBackground;

namespace CalendarBankHolidays.Tests;

internal sealed class FakeCalendar : ICalendar
{
    public List<CalEvent> Items { get; } = [];
    public List<string> Writes { get; } = [];
    private int next;

    public IReadOnlyList<CalEvent> Events(DateOnly first, DateOnly last) =>
        Items.Where(e => e.End > first.ToDateTime(TimeOnly.MinValue) && e.Start < last.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToList();

    public string Create(JsonObject body)
    {
        var id = $"new{++next}";
        Writes.Add($"create {Describe(body)}");
        Items.Add(Owned(id, body));
        return id;
    }

    public void Update(string id, JsonObject body)
    {
        Writes.Add($"update {Describe(body)}");
        Items.RemoveAll(e => e.Id == id);
        Items.Add(Owned(id, body));
    }

    public void Delete(string id)
    {
        var e = Items.Single(x => x.Id == id);
        Writes.Add($"delete {e.Start:yyyy-MM-dd} {e.Subject}");
        Items.Remove(e);
    }

    /// <summary>"2026-12-25 all-day Christmas Day free [Bank holiday]" or "2027-03-26 09:00-17:00 Bank holiday oof [...]".</summary>
    private static string Describe(JsonObject body)
    {
        var start = (string)body["start"]!["dateTime"]!;
        var end = (string)body["end"]!["dateTime"]!;
        var when = (bool)body["isAllDay"]! ? "all-day" : $"{start[11..16]}-{end[11..16]}";
        return $"{start[..10]} {when} {body["subject"]} {body["showAs"]} [{string.Join(",", body["categories"]!.AsArray().Select(c => (string)c!))}]";
    }

    private static CalEvent Owned(string id, JsonObject body)
    {
        var start = DateTime.Parse((string)body["start"]!["dateTime"]!);
        var allDay = (bool)body["isAllDay"]!;
        return new CalEvent(id, (string)body["subject"]!, start, DateTime.Parse((string)body["end"]!["dateTime"]!), allDay, true, 0, false)
        { OwnedDay = DateOnly.FromDateTime(start), OwnedKind = allDay ? Kind.AllDay : Kind.Block, Hash = Planner.Digest(body) };
    }

    public static CalEvent Hand(string subject, string start, string end, bool allDay = false, bool mine = true, int attendees = 0) =>
        new($"hand-{subject}-{start}", subject, DateTime.Parse(start), DateTime.Parse(end), allDay, mine, attendees, false);
}

public sealed class CalendarBankHolidaysTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-bank-holidays-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCalendar calendar = new();
    private DateTime now = new(2026, 11, 19, 9, 0, 0);
    private int downloads;
    private string feed = Feed2026;
    private bool feedDown;

    private const string Feed2026 = """
        {"england-and-wales": {"division": "england-and-wales", "events": [
          {"title": "Summer bank holiday", "date": "2026-08-31", "notes": "", "bunting": true},
          {"title": "Christmas Day", "date": "2026-12-25", "notes": "", "bunting": true},
          {"title": "Boxing Day", "date": "2026-12-28", "notes": "Substitute day", "bunting": true},
          {"title": "New Year’s Day", "date": "2027-01-01", "notes": "", "bunting": true},
          {"title": "Good Friday", "date": "2027-03-26", "notes": "", "bunting": false},
          {"title": "Early May bank holiday", "date": "2027-05-03", "notes": "", "bunting": true},
          {"title": "Christmas Day", "date": "2027-12-27", "notes": "Substitute day", "bunting": true}]},
         "scotland": {"division": "scotland", "events": [
          {"title": "St Andrew’s Day", "date": "2026-11-30", "notes": "", "bunting": true}]},
         "northern-ireland": {"division": "northern-ireland", "events": [
          {"title": "St Patrick’s Day", "date": "2027-03-17", "notes": "", "bunting": true}]}}
        """;

    private const string Settings = "time_zone = \"GMT Standard Time\"\n";
    private const string Both = Settings + "[block]\nenabled = true\n";

    public CalendarBankHolidaysTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ITask? task;

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, $"log_file = '{dir}\\bh.log'\nstate_file = '{dir}\\state.json'\ncache_file = '{dir}\\cache.json'\n" + settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var notes = new List<string>();
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Env = _ => null, Now = () => now, Calendar = _ => calendar, Lock = false,
            Task = () => task ?? throw new InvalidOperationException("no task in this test"), ProgramDir = dir,
            Notify = (title, _) => notes.Add(title),
            Download = _ =>
            {
                downloads++;
                return feedDown ? throw new HttpRequestException("no network") : feed;
            },
        });
        return (code, output.ToString() + string.Concat(notes.Select(n => $"NOTIFIED {n}\n")), errors.ToString());
    }

    [Fact]
    public void By_default_each_bank_holiday_gets_an_all_day_entry_on_the_right_day()
    {
        var (code, output, error) = Run(Settings, "run");
        Assert.True(code == 0, error);
        Assert.Equal([
            "create 2026-12-25 all-day Christmas Day free [Bank holiday]",
            "create 2026-12-28 all-day Boxing Day (substitute day) free [Bank holiday]",
            "create 2027-01-01 all-day New Year's Day free [Bank holiday]",
            "create 2027-03-26 all-day Good Friday free [Bank holiday]",
            "create 2027-05-03 all-day Early May bank holiday free [Bank holiday]",
        ], calendar.Writes);   // not 31 Aug (past), nor 27 Dec 2027 (more than 12 months on), nor other regions' days
        Assert.Contains("5 added", output);
        calendar.Writes.Clear();
        Run(Settings, "run");
        Assert.Empty(calendar.Writes);
        Assert.Equal(1, downloads);   // read once a day; the kept copy after that
    }

    [Fact]
    public void An_all_day_entry_already_there_with_its_name_stops_another_unless_it_is_on_the_wrong_day()
    {
        // Outlook's holiday calendar: Christmas Day on the day, Boxing Day on the Saturday (the bank holiday is Monday 28th).
        calendar.Items.Add(FakeCalendar.Hand("Christmas Day", "2026-12-25T00:00", "2026-12-26T00:00", allDay: true));
        calendar.Items.Add(FakeCalendar.Hand("Boxing Day", "2026-12-26T00:00", "2026-12-27T00:00", allDay: true));
        calendar.Items.Add(FakeCalendar.Hand("New Year’s Day", "2027-01-01T00:00", "2027-01-02T00:00", allDay: true));
        var (_, output, _) = Run(Settings, "plan");
        Assert.Contains("none added: \"Christmas Day\" is already there", output);
        Run(Settings, "run");
        Assert.DoesNotContain(calendar.Writes, w => w.Contains("2026-12-25") || w.Contains("2027-01-01"));
        Assert.Contains("create 2026-12-28 all-day Boxing Day (substitute day) free [Bank holiday]", calendar.Writes);

        calendar.Writes.Clear();
        Run(Settings + "[all_day]\nskip_if_exists = false\n", "run");
        Assert.Contains(calendar.Writes, w => w.StartsWith("create 2026-12-25", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_during_the_day_its_hours_and_both_kinds_together()
    {
        Run(Settings + "[all_day]\nenabled = false\n[block]\nenabled = true\nhours = \"08:30-17:30\"\ncategories = [\"Holiday\"]\n", "run");
        Assert.Contains("create 2027-03-26 08:30-17:30 Bank holiday oof [Holiday]", calendar.Writes);
        Assert.DoesNotContain(calendar.Writes, w => w.Contains("all-day"));
        calendar.Items.Clear();
        calendar.Writes.Clear();
        Run(Both, "run");
        Assert.Contains("create 2027-03-26 all-day Good Friday free [Bank holiday]", calendar.Writes);
        Assert.Contains("create 2027-03-26 09:00-17:00 Bank holiday oof [Bank holiday]", calendar.Writes);
        Assert.Equal(10, calendar.Writes.Count);
    }

    [Fact]
    public void Switching_a_kind_off_takes_its_entries_out()
    {
        Run(Both, "run");
        calendar.Writes.Clear();
        now = now.AddDays(1);
        Run(Settings, "run");   // the block's off again (its default)
        Assert.Equal(5, calendar.Writes.Count);
        Assert.All(calendar.Writes, w => Assert.Matches(@"^delete \S+ Bank holiday$", w));
    }

    [Fact]
    public void A_hand_made_bank_holiday_block_is_taken_over_not_duplicated()
    {
        calendar.Items.Add(FakeCalendar.Hand("Bank holiday", "2027-03-26T09:00", "2027-03-26T17:00"));
        calendar.Items.Add(FakeCalendar.Hand("Bank holiday drinks", "2027-01-01T18:00", "2027-01-01T20:00", mine: false, attendees: 6));
        var (_, output, _) = Run(Both, "plan");
        Assert.Contains("take over \"Bank holiday\"", output);
        Run(Both, "run");
        Assert.Contains("update 2027-03-26 09:00-17:00 Bank holiday oof [Bank holiday]", calendar.Writes);
        Assert.Single(calendar.Items, e => e.OwnedDay == new DateOnly(2027, 3, 26) && e.OwnedKind == Kind.Block);
        Assert.Contains(calendar.Items, e => e.Subject == "Bank holiday drinks" && e.OwnedDay is null);
    }

    [Fact]
    public void A_moved_day_moves_its_entries_and_an_edited_one_is_put_back()
    {
        Run(Both, "run");
        calendar.Writes.Clear();
        feed = Feed2026.Replace("\"2027-03-26\"", "\"2027-03-29\"");
        now = now.AddDays(1);
        var block = calendar.Items.Single(e => e.OwnedDay == new DateOnly(2027, 1, 1) && e.OwnedKind == Kind.Block);
        calendar.Items.Remove(block);
        calendar.Items.Add(block with { End = block.End.AddHours(-3) });   // shortened by hand
        Run(Both, "run");
        Assert.Equal([
            "create 2027-03-29 09:00-17:00 Bank holiday oof [Bank holiday]",
            "create 2027-03-29 all-day Good Friday free [Bank holiday]",
            "delete 2027-03-26 Bank holiday",
            "delete 2027-03-26 Good Friday",
            "update 2027-01-01 09:00-17:00 Bank holiday oof [Bank holiday]",
        ], calendar.Writes.Order().ToList());
    }

    [Fact]
    public void Region_titles_extra_and_skip_come_from_the_settings()
    {
        Run(Settings + "region = \"northern-ireland\"\nmonths_ahead = 6\n[all_day]\ntitle = \"Bank holiday ({short})\"\n", "run");
        Assert.Equal(["create 2027-03-17 all-day Bank holiday (St Patrick's Day) free [Bank holiday]"], calendar.Writes);
        calendar.Items.Clear();
        calendar.Writes.Clear();
        Run(Settings + "skip = [2027-05-03]\nextra = [ { date = 2026-12-24, name = \"Christmas Eve closure\" } ]\n", "run");
        Assert.Contains("create 2026-12-24 all-day Christmas Eve closure free [Bank holiday]", calendar.Writes);
        Assert.DoesNotContain(calendar.Writes, w => w.Contains("2027-05-03"));
    }

    [Fact]
    public void A_failed_download_uses_the_kept_copy_and_with_none_the_run_fails_and_notifies_once()
    {
        feedDown = true;
        var (code, output, error) = Run(Settings, "run");
        Assert.Equal(1, code);
        Assert.Contains("no network", error);
        Assert.DoesNotContain("NOTIFIED", output);   // 0.1.1: not on the first failure (the network may just not be up yet)
        (_, output, _) = Run(Settings, "run");
        Assert.DoesNotContain("NOTIFIED", output);
        (_, output, _) = Run(Settings, "run");
        Assert.Contains("NOTIFIED calendar-bank-holidays couldn't run", output);   // the third in a row
        (_, output, _) = Run(Settings, "run");
        Assert.DoesNotContain("NOTIFIED", output);   // once only

        feedDown = false;
        Run(Settings, "run");
        feedDown = true;
        now = now.AddDays(1);
        calendar.Writes.Clear();
        (code, _, error) = Run(Settings, "run");
        Assert.True(code == 0, error);
        Assert.Empty(calendar.Writes);
        Assert.Contains("couldn't be read", File.ReadAllText(Path.Combine(dir, "bh.log")));
    }

    [Fact]
    public void Too_many_removals_stop_the_run_and_switched_off_does_nothing_and_remove_takes_out_its_own()
    {
        Run(Both, "run");
        feed = Feed2026.Replace("2027-", "2029-").Replace("2026-12", "2029-12");
        now = now.AddDays(1);
        calendar.Writes.Clear();
        var (code, _, error) = Run("max_deletes = 3\n" + Both, "run");
        Assert.Equal(1, code);
        Assert.Contains("more than max_deletes (3)", error);
        Assert.Empty(calendar.Writes);

        calendar.Items.Add(FakeCalendar.Hand("Christmas Day", "2026-12-25T00:00", "2026-12-26T00:00", allDay: true));
        var (_, output, _) = Run("enabled = false\n" + Both, "run");
        Assert.Contains("Switched off", output);
        (_, output, _) = Run(Both, "remove");
        Assert.Contains("10 removed", output);
        Assert.Single(calendar.Items);
    }

    [Fact]
    public void Without_months_ahead_the_bases_long_range_months_ahead_is_used()
    {
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), "long_range_months_ahead = 6\n");
        var (code, output, error) = Run("base = \"base-config.toml\"\n" + Settings, "plan");
        Assert.True(code == 0, error);
        Assert.Contains("19/11/2026 to 19/05/2027", output);
    }

    [Fact]
    public void Months_ahead_wins_over_the_bases_long_range_months_ahead()
    {
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), "long_range_months_ahead = 6\n");
        var (code, output, error) = Run("base = \"base-config.toml\"\nmonths_ahead = 3\n" + Settings, "plan");
        Assert.True(code == 0, error);
        Assert.Contains("19/11/2026 to 19/02/2027", output);
    }

    [Fact]
    public void Without_either_the_window_is_twelve_months()
    {
        var (code, output, error) = Run(Settings, "plan");
        Assert.True(code == 0, error);
        Assert.Contains("19/11/2026 to 19/11/2027", output);
    }

    [Theory]
    [InlineData("region = \"wales\"", "region should be one of")]
    [InlineData("months_ahead = 0", "months_ahead should be a whole number from 1 to 36")]
    [InlineData("[block]\nhours = \"17:00-09:00\"", "block.hours should be")]
    [InlineData("[all_day]\nshow_as = \"away\"", "all_day.show_as should be one of")]
    [InlineData("[all_day]\nhours = \"09:00-17:00\"", "[all_day] has an unknown setting 'hours'")]
    [InlineData("[all_day]\nenabled = false", "both off")]
    [InlineData("skip = [\"soon\"]", "each of skip should be a date")]
    [InlineData("colour = 1", "unknown setting 'colour'")]
    public void Settings_are_checked_strictly(string settings, string message)
    {
        var (code, _, error) = Run(settings, "plan");
        Assert.Equal(2, code);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Names_titles_and_the_stored_kind()
    {
        var days = Feed.Parse(Feed2026, "england-and-wales");
        var boxing = days.Single(d => d.Date == new DateOnly(2026, 12, 28));
        Assert.Equal("Boxing Day (substitute day)", Planner.Title("{name}{note}", boxing));
        Assert.Equal("Early May", Planner.ShortName("Early May bank holiday"));
        Assert.True(Planner.SameName("New Year’s Day", "New Year's Day"));
        Assert.True(Planner.SameName("Christmas Day (observed)", "Christmas Day"));
        Assert.False(Planner.SameName("Christmas Eve", "Christmas Day"));
        Assert.Throws<FormatException>(() => Feed.Parse("{\"scotland\": {\"events\": []}}", "scotland"));
        var read = GraphCalendar.Read(new JsonObject
        {
            ["id"] = "x", ["subject"] = "Bank holiday", ["isAllDay"] = false,
            ["start"] = new JsonObject { ["dateTime"] = "2027-03-26T09:00:00.0000000" }, ["end"] = new JsonObject { ["dateTime"] = "2027-03-26T17:00:00.0000000" },
            ["singleValueExtendedProperties"] = new JsonArray(new JsonObject { ["id"] = GraphCalendar.DayProp, ["value"] = "2027-03-26 block" }),
        });
        Assert.Equal((new DateOnly(2027, 3, 26), Kind.Block), (read.OwnedDay!.Value, read.OwnedKind));
        var stamped = GraphCalendar.Stamped(new JsonObject { ["start"] = new JsonObject { ["dateTime"] = "2026-12-25T00:00:00" }, ["isAllDay"] = true });
        Assert.Equal("2026-12-25 all-day", (string)stamped["singleValueExtendedProperties"]![0]!["value"]!);
    }

    private sealed class RecordingTask : ITask
    {
        public TaskSchedule? Schedule { get; private set; }
        public string Name => "calendar-bank-holidays run";
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => throw new NotSupportedException();
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Schedule = schedule;
            return exe;
        }
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() => null;
    }

    [Fact]
    public void Autostart_runs_daily_and_at_logon_but_not_after_unlock_unless_asked()
    {
        var recorded = new RecordingTask();
        task = recorded;
        var (code, output, error) = Run(Settings, "autostart", "on");
        Assert.True(code == 0, error);
        Assert.True(recorded.Schedule!.AtLogon);
        Assert.Equal("07:00", recorded.Schedule.DailyAt);
        Assert.Null(recorded.Schedule.AfterUnlock);
        Assert.DoesNotContain("unlock", output);

        (code, output, error) = Run("after_unlock_minutes = 2\n" + Settings, "autostart", "on");
        Assert.True(code == 0, error);
        Assert.Equal(TimeSpan.FromMinutes(2), recorded.Schedule.AfterUnlock);
        Assert.Contains("2 minute(s) after each unlock", output);

        Run("after_unlock_minutes = 0\n" + Settings, "autostart", "on");
        Assert.Null(recorded.Schedule.AfterUnlock);
    }

    private sealed class StatusTask(string? description) : ITask
    {
        public string Name => "calendar-bank-holidays run";
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => throw new NotSupportedException();
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() => new Dictionary<string, string>
        {
            ["state"] = "Ready", ["command"] = "x", ["description"] = description ?? "",
        };
    }

    [Fact]
    public void Without_a_schedule_section_the_task_is_unchanged_and_the_fingerprint_is_recorded()
    {
        var recorded = new RecordingTask();
        task = recorded;
        var (code, output, error) = Run(Settings, "autostart", "on");
        Assert.True(code == 0, error);
        Assert.Equal("07:00", recorded.Schedule!.DailyAt);
        Assert.Null(recorded.Schedule.Every);
        Assert.Null(recorded.Schedule.AfterUnlock);
        Assert.True(recorded.Schedule.AtLogon);
        Assert.Equal("v1 job every=0 daily=07:00 logon=yes unlock=0", recorded.Schedule.Fingerprint);
        Assert.Contains("runs daily at 07:00, at logon, and has run now", output);
    }

    [Fact]
    public void The_schedule_section_sets_the_task()
    {
        var recorded = new RecordingTask();
        task = recorded;
        var (code, output, error) = Run(Settings + "[schedule]\ndaily_at = \"06:15\"\nafter_unlock_minutes = 3\n", "autostart", "on");
        Assert.True(code == 0, error);
        Assert.Equal("06:15", recorded.Schedule!.DailyAt);
        Assert.Equal(TimeSpan.FromMinutes(3), recorded.Schedule.AfterUnlock);
        Assert.Contains("daily at 06:15, at logon, 3 minute(s) after each unlock", output);
    }

    [Fact]
    public void The_old_top_level_keys_still_work_and_are_logged_as_deprecated()
    {
        var recorded = new RecordingTask();
        task = recorded;
        var (code, _, error) = Run("daily_at = \"08:00\"\n" + Settings, "autostart", "on");
        Assert.True(code == 0, error);
        Assert.Equal("08:00", recorded.Schedule!.DailyAt);
        (code, _, error) = Run("daily_at = \"08:00\"\n" + Settings, "run");
        Assert.True(code == 0, error);
        Assert.Contains("daily_at at the top level are deprecated: move them to [schedule]", File.ReadAllText(Path.Combine(dir, "bh.log")));
    }

    [Theory]
    [InlineData("daily_at = \"08:00\"\n" + Settings + "[schedule]\ndaily_at = \"07:00\"\n", "daily_at and [schedule] are both given")]
    [InlineData(Settings + "[schedule]\ncolour = 1\n", "unknown setting colour")]
    [InlineData(Settings + "[schedule]\nevery_minutes = 15\ndaily_at = \"07:00\"\n", "set every_minutes or daily_at, not both")]
    public void A_bad_schedule_is_refused(string settings, string message)
    {
        var (code, _, error) = Run(settings, "plan");
        Assert.Equal(2, code);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Status_says_when_the_task_no_longer_matches_its_schedule()
    {
        task = new StatusTask("calendar-bank-holidays run (schedule: v1 job every=0 daily=07:00 logon=yes unlock=0)");
        var (_, output, _) = Run(Settings, "autostart", "status");
        Assert.DoesNotContain("settings changed", output);
        (_, output, _) = Run(Settings + "[schedule]\ndaily_at = \"06:00\"\n", "autostart", "status");
        Assert.Contains("settings changed since this task was registered: run `calendar-bank-holidays autostart on`", output);
        task = new StatusTask("calendar-bank-holidays run");
        (_, output, _) = Run(Settings, "autostart", "status");
        Assert.Contains("registered before its schedule was recorded", output);
    }

    [Fact]
    public void A_run_outside_the_schedule_does_nothing()
    {
        var (code, output, error) = Run(Settings + "[schedule]\ndaily_at = \"07:00\"\nend_date = \"2026-11-01\"\n", "run");
        Assert.True(code == 0, error);
        Assert.Contains("Outside the [schedule]", output);
        Assert.Empty(calendar.Writes);
    }
}
