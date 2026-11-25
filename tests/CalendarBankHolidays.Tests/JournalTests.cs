using ActivityJournal;

namespace CalendarBankHolidays.Tests;

/// <summary>0.6.0: each entry a run adds, takes over, updates or removes is a line in the shared activity journal
/// (background-tools-lib's ActivityJournal); never a plan or a dry run, and writing it never fails the run.</summary>
public sealed class JournalTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-bank-holidays-journal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCalendar calendar = new();
    private DateTime now = new(2026, 11, 19, 9, 0, 0);
    private string feed = Feed;

    private const string Feed = """
        {"england-and-wales": {"division": "england-and-wales", "events": [
          {"title": "Christmas Day", "date": "2026-12-25", "notes": "", "bunting": true},
          {"title": "Boxing Day", "date": "2026-12-28", "notes": "Substitute day", "bunting": true}]}}
        """;

    private const string Both = "time_zone = \"GMT Standard Time\"\n[block]\nenabled = true\n";

    public JournalTests() => Directory.CreateDirectory(dir);

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

    private string JournalPath => Path.Combine(dir, "state", "journal.jsonl");

    private IReadOnlyList<JournalEntry> Lines() => File.Exists(JournalPath) ? new Journal(JournalPath).Read() : [];

    private int Run(params string[] args) => RunWith($"journal_file = '{dir}\\state\\journal.jsonl'\n", Both, args);

    private int RunWith(string journal, string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, $"log_file = '{dir}\\bh.log'\nstate_file = '{dir}\\state.json'\ncache_file = '{dir}\\cache.json'\n" + journal + settings);
        return Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = new StringWriter(), Err = new StringWriter(), Env = _ => null, Now = () => now, Calendar = _ => calendar, Lock = false,
            Notify = (_, _) => { }, Download = _ => feed,
        });
    }

    [Fact]
    public void A_run_journals_what_it_added_and_took_over_and_a_plan_nothing()
    {
        calendar.Items.Add(FakeCalendar.Hand("Bank holiday", "2026-12-25T09:00", "2026-12-25T17:00"));
        Assert.Equal(0, Run("plan"));
        Assert.Equal(0, Run("run", "--dry-run"));
        Assert.False(File.Exists(JournalPath));

        Assert.Equal(0, Run("run"));
        var lines = Lines();
        Assert.Equal(4, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.Equal("calendar-bank-holidays", l.Tool);
            Assert.Equal(By.Auto, l.By);
            Assert.Equal(Sensitivity.Normal, l.Sensitivity);
            Assert.Null(l.Undo);
        });
        var taken = Assert.Single(lines, l => l.What == "take-over");
        Assert.Equal("Bank holiday, Fri 25 Dec 2026", taken.Subject);
        Assert.Equal("block", taken.Rule);
        Assert.Equal("Christmas Day: your own \"Bank holiday\" taken over as the block", taken.Why);
        Assert.Equal("hand-Bank holiday-2026-12-25T09:00", taken.Item);
        var boxing = Assert.Single(lines, l => l.What == "add" && l.Rule == "all_day" && l.Subject!.StartsWith("Boxing Day", StringComparison.Ordinal));
        Assert.Equal("Boxing Day (substitute day), Mon 28 Dec 2026", boxing.Subject);
        Assert.Equal("Boxing Day", boxing.Why);
        Assert.StartsWith("new", boxing.Item);

        // Unchanged: nothing more.
        Assert.Equal(0, Run("run"));
        Assert.Equal(4, Lines().Count);
    }

    [Fact]
    public void A_day_that_stops_being_one_is_removed_with_why_and_remove_by_hand_has_run_as_undo()
    {
        Run("run");
        feed = Feed.Replace("2026-12-28", "2026-12-29");
        now = now.AddDays(1);   // the list is read at most once a day
        Assert.Equal(0, Run("run"));
        var removed = Lines().Where(l => l.What == "remove").ToList();
        Assert.Equal(2, removed.Count);
        Assert.All(removed, l => Assert.Equal(By.Auto, l.By));
        Assert.Contains(removed, l => l.Subject == "Boxing Day (substitute day), Mon 28 Dec 2026");

        var before = Lines().Count;
        Assert.Equal(0, Run("remove", "--dry-run"));
        Assert.Equal(before, Lines().Count);
        Assert.Equal(0, Run("remove"));
        var byHand = Lines().Skip(before).ToList();
        Assert.Equal(4, byHand.Count);
        Assert.All(byHand, l =>
        {
            Assert.Equal("remove", l.What);
            Assert.Equal(By.Hand, l.By);
            Assert.Equal("by hand", l.Rule);
            Assert.Equal("calendar-bank-holidays run", l.Undo);
        });
    }

    [Fact]
    public void A_create_whose_answer_is_lost_is_journalled_unconfirmed()
    {
        calendar.CreateFail = new M365Graph.GraphDeliveryUnknownException("POST", "/me/events", new TaskCanceledException("timed out"));
        Assert.Equal(1, Run("run"));
        var line = Assert.Single(Lines());
        Assert.Equal("add", line.What);
        Assert.Equal(JournalEntry.Unconfirmed, line.Outcome);
    }

    [Fact]
    public void Without_journal_file_nothing_is_written_and_a_bad_journal_never_fails_the_run()
    {
        Assert.Equal(0, RunWith("", Both, "run"));
        Assert.Empty(Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories));

        calendar.Items.Clear();
        Directory.CreateDirectory(JournalPath);   // a folder where the file should be
        Assert.Equal(0, Run("run"));
        Assert.Equal(4, calendar.Items.Count);
        Assert.Contains("journal: couldn't write", File.ReadAllText(Path.Combine(dir, "bh.log")));
    }

    [Fact]
    public void Journal_file_comes_from_a_base_with_its_path_variables()
    {
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), $"journal_file = '%SHARED%/journal.jsonl'\n[paths]\nSHARED = '{dir}\\shared'\n");
        var path = Path.Combine(dir, "based.toml");
        File.WriteAllText(path, $"base = 'base-config.toml'\nlog_file = '{dir}\\bh.log'\nstate_file = '{dir}\\state.json'\ncache_file = '{dir}\\cache.json'\n");
        Assert.Equal(Path.Combine(dir, "shared", "journal.jsonl"), Path.GetFullPath(Config.Load(path, _ => null).JournalFile!));
    }
}
