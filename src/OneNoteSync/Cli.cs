using System.Reflection;
using M365Graph;
using WinBackground;

namespace OneNoteSync;

/// <summary>onenote-sync: a shared OneNote section's newest pages, pulled to files; and your section of the week's
/// page, posted. Exit codes as the Python's: 0 done, 1 refused or failed, 2 settings, arguments or sign-in, 3 a
/// second watcher.</summary>
public static class Cli
{
    public const string Prog = "onenote-sync";

    private const string Usage =
        "usage: onenote-sync [-h] [--version] [--config CONFIG] {config,sign-in,pages,pull,post,watch,autostart} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "onenote-sync: a shared OneNote section's newest pages, pulled to files; and your section of the week's page, posted.\n\n" +
        "    onenote-sync config                 create the settings file (if missing) and say where it is\n" +
        "    onenote-sync sign-in                sign in to Microsoft (once; opens a browser)\n" +
        "    onenote-sync pages                  the dated pages in the section, newest last\n" +
        "    onenote-sync pull [--dry-run]       write the newest pages to files, where they've changed\n" +
        "    onenote-sync post <file.md>         show what would be posted to your section (changes nothing)\n" +
        "    onenote-sync post <file.md> --yes   post it (--force replaces a section changed by hand)\n" +
        "    onenote-sync watch                  pull on a timer; what autostart runs\n" +
        "    onenote-sync autostart on           the watcher at every logon, and now (off, status)\n\n" +
        "        --date yyyy-mm-dd               post: the week's Monday, if the file name has no date\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\onenote-sync\\config.toml)\n";

    private static readonly string[] Commands = ["config", "sign-in", "pages", "pull", "post", "watch", "autostart"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private sealed class Options
    {
        public string? Config;
        public string Command = "";
        public bool DryRun, Yes, Force;
        public string? File, Date, Action;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static IOneNote GraphNotebookFor(Config config, bool? interactive) =>
        new GraphNotebook(CliHost.Client(config, interactive), config.Owner, config.Notebook, config.Section);

    public static void SignIn(Config config) =>
        CliHost.Client(config, true).TokenAsync(GraphNotebook.Scopes, true).GetAwaiter().GetResult();

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        if (options.Command == "config")
        {
            host.Out.WriteLine(options.Config ?? OneNoteSync.Config.CreateDefault(host.Env));
            return 0;
        }
        Config config;
        try
        {
            config = OneNoteSync.Config.Load(options.Config, host.Env);
        }
        catch (Exception error) when (error is UsageException or IOException or UnauthorizedAccessException)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        if (options.Command == "autostart")
            return Autostart(options.Action!, config, host);
        if (options.Command == "watch")
        {
            if (config.Pull is null)
            {
                host.Err.WriteLine($"{config.Path} has no [pull] section, so there's nothing to watch.");
                return 2;
            }
            var watchLog = Logger.Create(config.LogFile, host.LogEcho);
            return Watcher.Run(config, c => host.NotebookFor(c, false), watchLog, host.Watch);
        }

        var log = Logger.Create(config.LogFile, host.LogEcho, stampEcho: false);
        try
        {
            if (options.Command == "sign-in")
            {
                host.SignIn(config);
                host.Out.WriteLine("Signed in.");
                return 0;
            }
            var notebook = host.NotebookFor(config, null);
            if (options.Command == "pages")
            {
                foreach (var item in notebook.DatedPages())
                    host.Out.WriteLine(item.Title);
                return 0;
            }
            using (host.RunLock($"onenote-sync-run-{Locks.Key(config.Path)}", 600)
                   ?? throw new TimeoutException("Another onenote-sync run didn't finish within 10 minutes."))
            {
                if (options.Command == "pull")
                    OneNoteSync.Pull.Run(config, notebook, options.DryRun, log);
                else
                {
                    Post.Run(config, notebook, options.File!, options.Date, dryRun: !options.Yes, force: options.Force, log: log);
                    if (!options.Yes)
                        host.Out.WriteLine("\nNothing has changed. Run again with --yes to post.");
                }
            }
            return 0;
        }
        catch (RefusedException error)
        {
            log($"not posted: {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is SignInNeededException or UsageException)
        {
            log(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            // Python printed a traceback here; the type and message are what it ended with.
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");
        var positional = new List<string>();
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            switch (options.Command, arg)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case ("pull", "--dry-run"):
                    options.DryRun = true;
                    break;
                case ("post", "--yes"):
                    options.Yes = true;
                    break;
                case ("post", "--force"):
                    options.Force = true;
                    break;
                case ("post", "--date"):
                    if (++i >= argv.Count)
                        throw new ArgumentsException("argument --date: expected one argument");
                    options.Date = argv[i];
                    break;
                case ("post", _) when arg.StartsWith("--date=", StringComparison.Ordinal):
                    options.Date = arg["--date=".Length..];
                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    positional.Add(arg);
                    break;
            }
        }
        switch (options.Command)
        {
            case "post":
                if (positional.Count == 0)
                    throw new ArgumentsException("the following arguments are required: file");
                options.File = positional[0];
                positional.RemoveAt(0);
                break;
            case "autostart":
                if (positional.Count == 0)
                    throw new ArgumentsException("the following arguments are required: action");
                if (positional[0] is not ("on" or "off" or "status"))
                    throw new ArgumentsException($"argument action: invalid choice: '{positional[0]}' (choose from 'on', 'off', 'status')");
                options.Action = positional[0];
                positional.RemoveAt(0);
                break;
        }
        if (positional.Count > 0)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", positional)}");
        return options;
    }

    private static int Autostart(string action, Config config, CliHost host)
    {
        var task = host.Task();
        if (action == "on")
        {
            if (config.Pull is null)
            {
                host.Err.WriteLine($"{config.Path} has no [pull] section, so there's nothing to watch.");
                return 2;
            }
            string exe;
            try
            {
                exe = AutoStart.Install(host.ProgramDir, AutoStart.InstallDir(host.Env));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                host.Err.WriteLine(error.Message);
                return 1;
            }
            var line = task.Enable(exe, AutoStart.Arguments(config), Path.GetDirectoryName(config.Path));
            host.Out.WriteLine($"Autostart is on: '{AutoStart.TaskName}' runs at every logon, and has started now.\n  {line}");
        }
        else if (action == "off")
            host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
        else
        {
            var info = task.Status();
            if (info is null)
                host.Out.WriteLine("Autostart is off. `onenote-sync autostart on` turns it on.");
            else
            {
                host.Out.WriteLine($"Autostart is on ('{AutoStart.TaskName}').");
                foreach (var name in new[] { "state", "last_run", "last_result", "command" })
                    host.Out.WriteLine($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
            }
        }
        return 0;
    }
}
