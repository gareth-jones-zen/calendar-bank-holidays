# calendar-bank-holidays

Keeps every bank holiday for the months ahead in your Outlook calendar, from
[GOV.UK's list](https://www.gov.uk/bank-holidays.json), so you never type them in, and anything that reads your
calendar sees the same days. Outlook's own holiday calendar is an import that goes stale, mixes regions (Northern
Ireland's days turn up for England), and puts a holiday on the weekend rather than its substitute day.

**What it writes is up to you**, as people differ: an **all-day entry** naming the day (on by default, and not
added where one is already there), a **block of time** during the day so you show as off (off by default, hours of
your choosing), or both; each with its own title, show-as and category.

```
calendar-bank-holidays plan
```

```
england-and-wales, 19/11/2026 to 19/11/2027 (GOV.UK's list, read now):
  Fri 25 Dec 2026  all day  Christmas Day                         none added: "Christmas Day" is already there
  Fri 25 Dec 2026  block    Bank holiday                          add
  Mon 28 Dec 2026  all day  Boxing Day (substitute day)           add
  Mon 28 Dec 2026  block    Bank holiday                          add
  Fri 26 Mar 2027  all day  Good Friday                           unchanged
  Fri 26 Mar 2027  block    Bank holiday                          take over "Bank holiday"
19/11/2026 to 19/11/2027: 3 to add, 1 to take over, 0 to update, 0 to remove, 1 unchanged, 1 already there (nothing changed)
```

## Install

Windows (x64) only; nothing else is needed.

1. Download `calendar-bank-holidays-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/calendar-bank-holidays/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\calendar-bank-holidays`.
3. `calendar-bank-holidays config` creates the settings file; `calendar-bank-holidays sign-in` signs you in (once).
4. `calendar-bank-holidays plan` shows what it would do; `calendar-bank-holidays run` does it; `calendar-bank-holidays autostart on` keeps it up
   to date (when `[schedule]` says: daily at 07:00 and at logon by default).

**It needs Calendars.ReadWrite** on Microsoft's "Graph Command Line Tools" app; `sign-in` asks for it.

## What it does

- **Each bank holiday from today to `months_ahead` months on (if unset, `long_range_months_ahead`; default 12), on the day GOV.UK gives** (so a
  substitute day, not the weekend), gets:
  - **`[all_day]`, on by default:** an all-day entry, by default its name ("Good Friday", "Boxing Day (substitute
    day)"), shown as free, in a "Bank holiday" category. **Not added when an all-day entry with that name is already
    there on the day** (`skip_if_exists`, default true), such as one from Outlook's holiday calendar; names match
    ignoring case, the kind of apostrophe and "(observed)".
  - **`[block]`, off by default:** time blocked out during the day, `hours` (default 09:00-17:00), titled "Bank
    holiday", shown as out of office, in the same category.
  - Titles are templates: `{name}` is GOV.UK's name, `{short}` without a trailing "bank holiday" ("Early May"),
    `{note}` " (substitute day)" when it is one.
- **Switching a kind off takes its entries out** at the next run.
- **Your region** is `region`: `england-and-wales` (default), `scotland` or `northern-ireland`. **`extra`** adds
  days (a company closure day), **`skip`** leaves some out.
- **A "Bank holiday" block you made by hand on the day is taken over** as the `[block]` (when it's on): rewritten as
  the tool's own, not deleted and not duplicated. Only your own timed entries with no one else invited, whose subject
  matches `[adopt] subjects` (default: starting "Bank holiday"). Outlook's own holiday entries are never changed.
- **It only ever changes its own entries**, which carry a hidden property (the day, and a hash of what was written).
  The calendar is the only record: one deleted by hand is written again, one edited is put back, and a day that stops
  being a bank holiday (GOV.UK moves one) loses its entry. Past days are never touched.
- **GOV.UK's list is read at most once a day**, and the last good copy is kept: if it can't be read, the copy is used
  (and the log says so); with no copy at all, the run fails.
- **Safety:** a run that would remove more than `max_deletes` (default 5) entries changes nothing and says why. A run that fails is logged, and
  notified on the third failure in a row (a run on waking can find the network not up yet), or at once for a
  sign-in or a safety stop; once only, until a run succeeds.
- **A write that may have reached Microsoft is never resent.** Reads, updates and removals are retried on a 429, a
  5xx or a dropped connection (an update rewrites one of its own entries, or a taken-over one of yours with no
  attendees, so a repeat tells nobody). A new entry is sent once: only a 429 or a request that never connected is tried
  again. If adding one gets a 5xx, times out or loses its connection, the run stops there and fails (logged as
  "couldn't confirm ... may have gone"), and the next run reads the calendar back by the hidden property: an entry that
  landed is kept, one that didn't is added, and a second one for a day is removed.

## The shared activity journal (0.6.0)

With **`journal_file`** set (top level, or in the base file so every tool shares it: by convention
`journal_file = "%EA_LOCAL%/journal.jsonl"`), each entry it adds, takes over, updates or removes is also **one JSON line
in a journal shared by the tools**, written through background-tools-lib's
[ActivityJournal](https://github.com/gareth-jones-zen/background-tools-lib#activityjournal), for a "while you were
away" view. Without it, nothing is written (the default).

```json
{"id":"c2e94b0f7a15","when":"2026-11-19T07:00:03.441+00:00","tool":"calendar-bank-holidays","what":"add","by":"auto","subject":"Boxing Day (substitute day), Mon 28 Dec 2026","rule":"all_day","why":"Boxing Day","item":"AAMkAD...","sensitivity":"normal"}
```

| `what` | When | `undo` |
|---|---|---|
| `add`, `take-over`, `update`, `remove` | `run` (by the task, or by hand: the same rules), `by` `auto` | none: the next run would put it back as the rules say |
| `remove` | `remove`, `by` `hand`, `rule` `by hand` | `calendar-bank-holidays run` (acts at once; `plan` shows what it would write) |

- **Only what was sent**: never `plan` or a `--dry-run`. A change whose answer was lost (it may or may not have
  landed) has `"outcome": "unconfirmed"`; the next run reads the calendar back as always.
- `subject` is the entry's title and its day; `rule` is `all_day` or `block`; `why` is GOV.UK's name for the day, with
  the reason for a removal (`not a bank holiday`, `switched off`, `a second one for the day`) or the entry of yours
  taken over;
  `item` is the event id.
- **Every line is `"sensitivity": "normal"`**: bank holidays are public.
- **Writing it never fails the run**: a problem is logged (`journal: couldn't write to ...`) and the run carries on.

## Commands

```
calendar-bank-holidays plan               each bank holiday, and what a run would change
calendar-bank-holidays run [--dry-run]    make the calendar match
calendar-bank-holidays remove [--dry-run] take out its entries from today on
calendar-bank-holidays autostart on       run daily and at logon (off, restart, status)
calendar-bank-holidays sign-in            sign in to Microsoft (once; opens a browser)
calendar-bank-holidays config             create the settings file (if missing) and say where it is
```

**Exit codes:** 0 done; 1 failed, or stopped for safety; 2 settings, arguments or sign-in.

## Settings

`%APPDATA%\calendar-bank-holidays\config.toml` by default (`--config` or `CALENDAR_BANK_HOLIDAYS_CONFIG` for another). Read strictly:
an unknown setting, or one of the wrong type, is an error that names it.

```toml
base = "base-config.toml"         # optional: settings shared with other tools (background-tools-lib's BaseSettings)
login_hint = "you@example.com"
tenant = "organizations"
msal_cache = "..."                # optional: another tool's token cache, to share its sign-in
time_zone = "GMT Standard Time"   # optional (a Windows name); default this PC's
log_file = "..."                  # default %LOCALAPPDATA%\calendar-bank-holidays\calendar-bank-holidays.log
state_file = "..."                # whether runs are failing, and the day the list was last read
cache_file = "..."                # the last good copy of GOV.UK's list
journal_file = "..."              # optional: the shared activity journal (0.6.0; usually from the base file)
enabled = true                    # false: runs do nothing (`remove` takes its entries out)
region = "england-and-wales"      # or scotland, northern-ireland
months_ahead = 12
feed_url = "https://www.gov.uk/bank-holidays.json"
notify = true
max_deletes = 5
body = "Written by calendar-bank-holidays from GOV.UK's list. Changes made here are overwritten."
extra = [ { date = 2027-12-24, name = "Christmas Eve closure" } ]
skip = []

[all_day]
enabled = true
title = "{name}{note}"
show_as = "free"                  # free, tentative, busy, oof or workingElsewhere
categories = ["Bank holiday"]
skip_if_exists = true

[block]
enabled = false
title = "Bank holiday"
hours = "09:00-17:00"
show_as = "oof"
categories = ["Bank holiday"]

[adopt]
subjects = '^\s*bank holiday\b'   # "" to take over nothing (only ever as the block)

[schedule]                        # when the task runs: see below
daily_at = "07:00"
```

### [schedule]

When the task runs, in the keys every background-tools-lib tool shares (`every_minutes`, `daily_at`, `hours`, `days`,
`at_logon`, `after_unlock_minutes`, `end_date`): see background-tools-lib's README, "[schedule]: the shared scheduling
settings". This tool's defaults:

```toml
[schedule]
daily_at = "07:00"
at_logon = true
after_unlock_minutes = 0          # 0: no run after unlocking
```

`autostart on` registers the task from it and records it; `autostart status` says when the settings have changed since
(run `autostart on` again). A run outside `hours` or `days`, or after `end_date`, does nothing. Before 0.4.0,
`daily_at` and `after_unlock_minutes` were top-level settings: they still work, logged as deprecated, but not together
with `[schedule]`.

## Building

.NET 11 (see `global.json`), with the shared libraries from
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo:

```
dotnet test -c Release
dotnet publish src/CalendarBankHolidays.Console -c Release -r win-x64 -o out    # Native AOT
```

A tag `vX.Y.Z` builds a release (`.github/workflows/release.yml`), against the library commit pinned there.

GOV.UK's bank holiday data is published under the [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).
