using M365Graph;
using WinBackground;

namespace OneNoteSync;

/// <summary>What the watcher touches outside itself, so tests can run it on a fake clock.</summary>
public sealed class WatchHost
{
    /// <summary>Seconds since the epoch (Python's time.time()), for the schedule.</summary>
    public Func<double> Time { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Local time, for the working hours.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Checked before each tick; tests use it to stop. The real watcher never stops by itself.</summary>
    public Func<bool> Stop { get; init; } = () => false;

    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;
    public Func<string, NamedLock?> Lock { get; init; } = Locks.SingleInstance;
    public Func<string, Config> Reload { get; init; } = path => Config.Load(path);
}

/// <summary>Watch mode: pull every every_minutes, in working hours, so the files are never far behind the page.
/// - One watcher per settings file. A second exits at once (code 3), whichever language it's written in: the
///   lock's name is the Python's.
/// - It never opens a browser. A lapsed sign-in is logged and notified once, and it keeps trying.
/// - It logs `alive` once an hour, so a silent log means a stuck watcher, not a quiet page.
/// - A failure is notified only after three in a row (a sign-in problem at once), and once until a run succeeds. A
///   single dropped connection that the next run gets past isn't something to act on (11 Nov 2026).</summary>
public static class Watcher
{
    public const int AlreadyRunning = 3;

    public static bool Due(PullSettings pull, DateTime now)
    {
        if (pull.Weekdays && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        var time = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return string.CompareOrdinal(pull.HoursFrom, time) <= 0 && string.CompareOrdinal(time, pull.HoursTo) < 0;
    }

    public static string LockName(Config config) => $"onenote-sync-watch-{Locks.Key(config.Path)}";

    /// <summary>notebookFor(config) makes a fresh notebook each time, so a settings change is picked up.</summary>
    public static int Run(Config config, Func<Config, IOneNote> notebookFor, Action<string> log, WatchHost? host = null)
    {
        host ??= new WatchHost();
        using var held = host.Lock(LockName(config));   // held until the process ends
        if (held is null)
        {
            log("another watcher is already running for these settings; not starting a second");
            return AlreadyRunning;
        }
        var pull = config.Pull!;
        log($"watching '{config.Notebook}' / '{config.Section}' every {pull.EveryMinutes} min, "
            + $"{pull.HoursFrom}-{pull.HoursTo}{(pull.Weekdays ? " Mon-Fri" : "")}");
        double nextPull = 0, beat = host.Time();
        string? failing = null;
        var streak = 0;   // failures in a row
        while (!host.Stop())
        {
            if (host.Time() >= nextPull)
            {
                nextPull = host.Time() + pull.EveryMinutes * 60;
                if (Due(pull, host.Now()))
                {
                    try
                    {
                        config = host.Reload(config.Path);
                        // A reload without [pull] fails this run; the schedule keeps the last good settings (the
                        // Python crashed on the next tick).
                        pull = config.Pull ?? throw new UsageException($"{config.Path} has no [pull] section.");
                        Pull.Run(config, notebookFor(config), log: log);
                        (failing, streak) = (null, 0);
                    }
                    catch (SignInNeededException error)
                    {
                        log($"sign-in needed: {error.Message}");
                        if (failing != "sign-in")
                            host.Notify("onenote-sync needs a sign-in", [Cut(error.Message, 300)]);
                        failing = "sign-in";
                    }
                    catch (Exception error)
                    {
                        var what = $"{error.GetType().Name}: {error.Message}";
                        log($"pull failed: {what}");
                        streak++;
                        if (streak == 3)
                            host.Notify("onenote-sync couldn't pull", [Cut(what, 300), $"Log: {config.LogFile}"]);
                        failing = error.GetType().Name;
                    }
                }
            }
            if (host.Time() - beat >= 3600)
            {
                beat = host.Time();
                log("alive");
            }
            host.Sleep(TimeSpan.FromSeconds(30));
        }
        return 0;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
