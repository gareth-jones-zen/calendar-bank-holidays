using System.Globalization;
using ActivityJournal;

namespace CalendarBankHolidays;

/// <summary>
/// The shared activity journal (0.6.0): one line for each entry calendar-bank-holidays added, took over, updated or
/// removed, written through background-tools-lib's ActivityJournal to journal_file (by convention
/// %EA_LOCAL%\journal.jsonl), for a "while you were away" view. Only what was sent: never a plan or a dry run; a change
/// whose answer was lost is marked unconfirmed. Bank holidays are public, so every line is "normal". Writing it never
/// fails the run: a problem is logged.
/// <para><c>what</c> is <c>add</c>, <c>take-over</c>, <c>update</c> or <c>remove</c>; <c>by</c> is auto for <c>run</c> (the
/// task, or the same rules run by hand) and hand for <c>remove</c>.</para>
/// </summary>
internal static class Activity
{
    public const string Tool = "calendar-bank-holidays";

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>A line for a change a run made. Never throws.</summary>
    public static void Changed(Config config, Func<DateTime> now, System.Action<string> log, DayPlan p, string? item, bool unconfirmed = false)
    {
        var title = p.Want is { } want ? (string)want["subject"]! : p.Have!.Subject;
        Write(config, now, log, new JournalEntry
        {
            Tool = Tool,
            What = p.Action switch { Action.Create => "add", Action.Adopt => "take-over", Action.Update => "update", _ => "remove" },
            By = By.Auto,
            Subject = $"{title}, {p.Day.ToString("ddd d MMM yyyy", En)}",
            Rule = p.Kind == Kind.AllDay ? "all_day" : "block",
            Why = p.Action == Action.Adopt ? $"{p.Name}: your own \"{p.Have!.Subject}\" taken over as the block"
                : p.Note is { Length: > 0 } note ? $"{p.Name}: {note}" : p.Name,
            Item = item,
            Outcome = unconfirmed ? JournalEntry.Unconfirmed : null,
        });
    }

    /// <summary>A line for an entry `remove` took out. Never throws.</summary>
    public static void Removed(Config config, Func<DateTime> now, System.Action<string> log, CalEvent e) =>
        Write(config, now, log, new JournalEntry
        {
            Tool = Tool,
            What = "remove",
            By = By.Hand,
            Subject = $"{e.Subject}, {e.OwnedDay!.Value.ToString("ddd d MMM yyyy", En)}",
            Rule = "by hand",
            Why = "calendar-bank-holidays remove",
            Item = e.Id,
            // Undone by a run, which writes them all again (at once: `plan` shows what it would write).
            Undo = "calendar-bank-holidays run",
        });

    private static void Write(Config config, Func<DateTime> now, System.Action<string> log, JournalEntry entry)
    {
        if (config.JournalFile is not { } path)
            return;
        try
        {
            new Journal(path, clock: () => new DateTimeOffset(now())).TryWrite(entry, log);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }
}
