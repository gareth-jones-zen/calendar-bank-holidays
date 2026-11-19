// calendar-bank-holidays-background.exe: no window, ever. What the scheduled task runs (`run`); its log is the settings' log_file.
return CalendarBankHolidays.Cli.Run(args, new CalendarBankHolidays.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
