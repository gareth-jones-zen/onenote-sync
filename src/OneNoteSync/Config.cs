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

public sealed record PullSettings(string Folder, string File, int Newest, int EveryMinutes, string HoursFrom, string HoursTo, bool Weekdays);

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
///     every_minutes = 15                      # watch mode
///     hours = "07:00-19:00"                   # watch mode: only between these times...
///     weekdays = true                         # ...and only Monday to Friday
///
///     [post]                                  # optional: your section of the week's page
///     section = "Architecture"                # the heading on the page, and in your markdown file
///     headings = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"]
///     sprint_weeks = 2                        # for a new page's "Sprint N"
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

    public required string Path { get; init; }
    public required string Owner { get; init; }
    public required string Notebook { get; init; }
    public required string Section { get; init; }
    public PullSettings? Pull { get; init; }
    public PostSettings? Post { get; init; }
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
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(full)).Replace("\r\n", "\n");
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException)
        {
            throw new UsageException($"{full}: {error.Message}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var local = Local(env);
        var notebook = raw.TryGetValue("notebook", out var nb) && nb is TomlTable t ? t : [];
        var missing = new[] { "owner", "name", "section" }.Where(k => !Truthy(notebook, k)).ToList();
        if (missing.Count > 0)
            throw new UsageException($"[notebook] in {full} needs {string.Join(", ", missing)}.");

        PullSettings? pull = null;
        if (raw.TryGetValue("pull", out var p) && p is TomlTable pt && pt.Count > 0)
        {
            if (!Truthy(pt, "folder"))
                throw new UsageException($"[pull] in {full} needs a folder.");
            var file = pt.TryGetValue("file", out var f) ? Str(f) : "Weekly update {date}.html";
            if (!file.Contains("{date}", StringComparison.Ordinal))
                throw new UsageException($"[pull] file in {full} must contain {{date}}, or every page would be written to one file.");
            var hours = pt.TryGetValue("hours", out var h) ? Str(h) : "07:00-19:00";
            if (!HoursPattern().IsMatch(hours))
                throw new UsageException($"[pull] hours in {full} should look like \"07:00-19:00\".");
            var parts = hours.Split('-');
            pull = new PullSettings(PathSetting(Str(pt["folder"]), folder, env), file, Int(pt, "newest", 2, full),
                Int(pt, "every_minutes", 15, full), parts[0], parts[1], !pt.TryGetValue("weekdays", out var w) || Bool(w));
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

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
