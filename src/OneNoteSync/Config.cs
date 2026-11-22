using System.Text;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace OneNoteSync;

/// <summary>Settings that can't be used, or a request that can't be met (Python's ValueError and LookupError):
/// the command line reports the message and exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>No settings file (Python's FileNotFoundError from load): exit 2.</summary>
public sealed class NoSettingsException(string message) : UsageException(message);

public sealed record PullSettings(string Folder, string File, int Newest, int EveryMinutes, string HoursFrom, string HoursTo, bool Weekdays)
{
    /// <summary>When the watcher pulls: [schedule] (background-tools-lib's shared scheduling settings, 1.4.0), a
    /// watcher's every_minutes being how often it pulls. Null: from EveryMinutes, HoursFrom-HoursTo and Weekdays, which
    /// until 1.4.0 were [pull] every_minutes, hours and weekdays (still read: the old form).</summary>
    public WinBackground.ScheduleSettings? Schedule { get; init; }

    /// <summary>True when the schedule came from the old [pull] keys (the watcher logs it once at start).</summary>
    public bool ScheduleFromOldKeys { get; init; }

    private static readonly HashSet<DayOfWeek> MonToFri =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>The schedule in force: [schedule]'s, or one made from the fields.</summary>
    public WinBackground.ScheduleSettings Effective => Schedule ?? new WinBackground.ScheduleSettings
    {
        EveryMinutes = EveryMinutes,
        Hours = WinBackground.ScheduleSettings.ParseHours($"{HoursFrom}-{HoursTo}"),
        Days = Weekdays ? MonToFri : null,
    };
}

public sealed record PostSettings(string Section, IReadOnlyList<string> Headings, int SprintWeeks);

/// <summary>The settings file.
///
///     login_hint = "you@example.com"          # optional
///     tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
///     msal_cache = "..."                      # optional: share another tool's sign-in
///     state_file = "..."                      # optional: what was last posted to each page
///     log_file = "..."                        # optional
///     backups = "..."                         # optional: each page's HTML, saved before every post
///
///     [notebook]
///     owner = "colleague@example.com"         # whose OneDrive the notebook is in; "me" for your own
///     name = "Weekly Team Updates"
///     section = "Weekly Updates"
///
///     [pull]                                  # optional: the newest pages, to files
///     folder = "~/OneDrive/Weekly updates"
///     file = "Weekly update {date}.html"      # {date} is the yyyy-mm-dd the page's title starts with
///     newest = 2                              # how many of the newest pages
///
///     [schedule]                              # watch mode (1.4.0; [pull] every_minutes, hours, weekdays until then)
///     every_minutes = 15
///     hours = "07:00-19:00"                   # only between these times...
///     days = "Mon-Fri"                        # ...and only on these days
///
///     [post]                                  # optional: your section of the week's page
///     section = "Architecture"                # the heading on the page, and in your markdown file
///     headings = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"]
///     sprint_weeks = 2                        # for a new page's "Sprint N"
///
///     [status]                                # optional: what `status` reports on
///     sections = ["Digital", "Fulfilment", "Assurance", "Architecture"]   # default: [post] headings
///
/// Pages are found by the date their title starts with, "yyyy-mm-dd – Sprint N", one per week.
/// Paths may use ~ and %VARIABLES% (and $VAR, as Python's expandvars); relative paths are relative to the
/// settings file.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "ONENOTE_SYNC_CONFIG";

    // Built with explicit "\n" (never a raw literal, which would take the .cs file's line endings), then written
    // with CRLF, as Python's text mode wrote it.
    public const string Template =
        "# onenote-sync settings. See the README.\n" +
        "# base = \"base-config.toml\"   # settings shared with other tools\n" +
        "# login_hint = \"you@example.com\"\n" +
        "\n" +
        "[notebook]\n" +
        "owner = \"me\"                  # or the email of whoever's OneDrive holds the notebook\n" +
        "name = \"My Notebook\"\n" +
        "section = \"Weekly Updates\"\n" +
        "\n" +
        "# [pull]\n" +
        "# folder = \"~/Documents/Weekly updates\"\n" +
        "# file = \"Weekly update {date}.html\"\n" +
        "\n" +
        "# [post]\n" +
        "# section = \"My team\"\n" +
        "# headings = [\"Summary\", \"My team\"]\n";

    /// <summary>Every top-level setting: what a base file may supply.</summary>
    public static readonly string[] KnownKeys =
        ["login_hint", "tenant", "msal_cache", "state_file", "log_file", "backups", "notebook", "pull", "post", "status", "schedule"];

    /// <summary>The watcher's defaults, as before [schedule] (1.4.0): every 15 minutes, 07:00-19:00, Mon-Fri.</summary>
    public static readonly WinBackground.ScheduleSettings DefaultSchedule = new()
    {
        EveryMinutes = 15,
        Hours = (new TimeSpan(7, 0, 0), new TimeSpan(19, 0, 0)),
        Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
    };

    /// <summary>[schedule] (1.4.0), or null when the file has none (the old [pull] keys, or the defaults, apply).</summary>
    private static WinBackground.ScheduleSettings? ReadSchedule(TomlTable raw, string full)
    {
        if (!raw.TryGetValue("schedule", out var value))
            return null;
        if (value is not TomlTable table)
            throw new UsageException($"schedule in {full} should be a [schedule] table.");
        WinBackground.ScheduleSettings schedule;
        try
        {
            schedule = WinBackground.ScheduleSettings.Parse(table, DefaultSchedule, WinBackground.TaskKind.Watcher, $"[schedule] in {full}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message);
        }
        if (schedule.EveryMinutes is not > 0)
            throw new UsageException($"[schedule] in {full}: every_minutes (how often the watcher pulls) should be at least 1.");
        return schedule;
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 24 ? "24:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    public required string Path { get; init; }
    public required string Owner { get; init; }
    public required string Notebook { get; init; }
    public required string Section { get; init; }
    public PullSettings? Pull { get; init; }
    public PostSettings? Post { get; init; }
    /// <summary>[status] sections: the headings `status` reports on; empty means [post] headings.</summary>
    public IReadOnlyList<string> StatusSections { get; init; } = [];
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string StateFile { get; init; }
    public required string LogFile { get; init; }
    public required string Backups { get; init; }

    /// <summary>The real environment; an empty name is no variable.</summary>
    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%LOCALAPPDATA%\onenote-sync: the default state, log and backups, and the locks' folder.</summary>
    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "onenote-sync"));
    }

    /// <summary>%APPDATA%\onenote-sync\config.toml.</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "onenote-sync", "config.toml"));
    }

    [GeneratedRegex(@"^\d\d:\d\d-\d\d:\d\d\z", RegexOptions.CultureInvariant)]
    private static partial Regex HoursPattern();

    /// <summary>The settings. Throws NoSettingsException or UsageException.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new NoSettingsException($"No settings file at {full}. Run `onenote-sync config` to create one.");
        TomlTable raw;
        try
        {
            // Read as tomllib reads it: strictly (a key or table given twice is refused), with universal newlines,
            // and a byte order mark refused.
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                throw new UsageException($"{full}: Invalid statement (at line 1, column 1)");
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;   // the base's [paths], then the environment
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)   // the base file: the message names it
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var local = Local(env);
        var notebook = raw.TryGetValue("notebook", out var nb) && nb is TomlTable t ? t : [];
        var missing = new[] { "owner", "name", "section" }.Where(k => !Truthy(notebook, k)).ToList();
        if (missing.Count > 0)
            throw new UsageException($"[notebook] in {full} needs {string.Join(", ", missing)}.");

        var schedule = ReadSchedule(raw, full);
        PullSettings? pull = null;
        if (raw.TryGetValue("pull", out var p) && p is TomlTable pt && pt.Count > 0)
        {
            if (!Truthy(pt, "folder"))
                throw new UsageException($"[pull] in {full} needs a folder.");
            var file = pt.TryGetValue("file", out var f) ? Str(f) : "Weekly update {date}.html";
            if (!file.Contains("{date}", StringComparison.Ordinal))
                throw new UsageException($"[pull] file in {full} must contain {{date}}, or every page would be written to one file.");
            var old = new[] { "every_minutes", "hours", "weekdays" }.Where(pt.ContainsKey).ToList();
            if (schedule is not null)
            {
                if (old.Count > 0)
                    throw new UsageException($"{full} sets {string.Join(", ", old)} in [pull] and has a [schedule]: keep them in "
                                             + "[schedule] only (the [pull] keys are the old form).");
                var (from, to) = schedule.Hours is var (a, b) ? (Clock(a), Clock(b)) : ("00:00", "24:00");
                pull = new PullSettings(PathSetting(Str(pt["folder"]), folder, env), file, Int(pt, "newest", 2, full),
                    schedule.EveryMinutes ?? 15, from, to,
                    schedule.Days is { Count: 5 } d && !d.Contains(DayOfWeek.Saturday) && !d.Contains(DayOfWeek.Sunday))
                { Schedule = schedule };
            }
            else
            {
                var hours = pt.TryGetValue("hours", out var h) ? Str(h) : "07:00-19:00";
                if (!HoursPattern().IsMatch(hours))
                    throw new UsageException($"[pull] hours in {full} should look like \"07:00-19:00\".");
                var parts = hours.Split('-');
                pull = new PullSettings(PathSetting(Str(pt["folder"]), folder, env), file, Int(pt, "newest", 2, full),
                    Int(pt, "every_minutes", 15, full), parts[0], parts[1], !pt.TryGetValue("weekdays", out var w) || Bool(w))
                { ScheduleFromOldKeys = old.Count > 0 };
            }
        }

        PostSettings? post = null;
        if (raw.TryGetValue("post", out var q) && q is TomlTable qt && qt.Count > 0)
        {
            if (!Truthy(qt, "section"))
                throw new UsageException($"[post] in {full} needs a section.");
            var section = Str(qt["section"]);
            var headings = qt.TryGetValue("headings", out var hs) && hs is TomlArray array
                ? array.Select(Str).ToList() : [];
            if (headings.Count > 0 && !headings.Select(PyStr.Lower).Contains(PyStr.Lower(section)))
                throw new UsageException($"[post] headings in {full} don't include your section, {Py.ReprString(section)}.");
            post = new PostSettings(section, headings, Int(qt, "sprint_weeks", 2, full));
        }

        IReadOnlyList<string> statusSections = [];
        if (raw.TryGetValue("status", out var st) && st is TomlTable stt && stt.TryGetValue("sections", out var ss))
        {
            if (ss is not TomlArray sectionArray || sectionArray.Count == 0)
                throw new UsageException($"[status] sections in {full} should be a list of headings, such as [\"Digital\", \"Architecture\"].");
            statusSections = sectionArray.Select(Str).ToList();
        }

        return new Config
        {
            Path = full,
            Owner = Str(notebook["owner"]),
            Notebook = Str(notebook["name"]),
            Section = Str(notebook["section"]),
            LoginHint = raw.TryGetValue("login_hint", out var hint) && hint is not null ? Str(hint) : null,
            Tenant = raw.TryGetValue("tenant", out var tenant) ? Str(tenant) : "organizations",
            MsalCache = Truthy(raw, "msal_cache") ? PathSetting(Str(raw["msal_cache"]), folder, env) : null,
            StateFile = Truthy(raw, "state_file") ? PathSetting(Str(raw["state_file"]), folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "state.json")),
            LogFile = Truthy(raw, "log_file") ? PathSetting(Str(raw["log_file"]), folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "onenote-sync.log")),
            Backups = Truthy(raw, "backups") ? PathSetting(Str(raw["backups"]), folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "backups")),
            Pull = pull,
            Post = post,
            StatusSections = statusSections,
        };
    }

    /// <summary>Python's truth test on a setting: missing, "", 0, false and empty arrays or tables are false.</summary>
    private static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    private static string Str(object? value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        null => "None",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    private static bool Bool(object? value) => value switch
    {
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        double d => d != 0,
        _ => value is not null,
    };

    /// <summary>int(value), as Python's int() takes a TOML number, a bool or a string of digits.</summary>
    private static int Int(TomlTable table, string key, int fallback, string file)
    {
        if (!table.TryGetValue(key, out var value))
            return fallback;
        try
        {
            return value switch
            {
                long l => checked((int)l),
                double d => checked((int)Math.Truncate(d)),
                bool b => b ? 1 : 0,
                string s => checked((int)PyStr.Int(s)),   // int() strips, and refuses "" as Python does
                _ => throw new FormatException($"int() can't convert {key}"),
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            throw new UsageException($"{file}: {key}: {error.Message}");
        }
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
        return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
    }

    /// <summary>Create the settings file at the default place if it's missing, and return where it is.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PyStr.WindowsNewlines(Template), new UTF8Encoding(false));
        }
        return path;
    }
}
