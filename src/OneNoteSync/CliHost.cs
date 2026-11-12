using M365Graph;
using WinBackground;

namespace OneNoteSync;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes: where it writes, the
/// environment, OneNote, Task Scheduler, the locks and where the running program is.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Config.Environment;

    /// <summary>The OneNote notebook for these settings; interactive false never opens a browser.</summary>
    public Func<Config, bool?, IOneNote> NotebookFor { get; init; } = Cli.GraphNotebookFor;

    /// <summary>Sign in, interactively.</summary>
    public Action<Config> SignIn { get; init; } = Cli.SignIn;

    public Func<ITask> Task { get; init; } = AutoStart.Create;
    public WatchHost Watch { get; init; } = new();

    /// <summary>The run lock: wait up to the seconds given.</summary>
    public Func<string, double, IDisposable?> RunLock { get; init; } = (name, seconds) =>
    {
        var held = new NamedLock(name);
        return held.Acquire(seconds) ? held : null;
    };

    /// <summary>The folder the running program is in, which autostart copies from.</summary>
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;

    internal static GraphClient Client(Config config, bool? interactive) => new(new GraphClientOptions
    {
        CachePath = config.MsalCache ?? Graph.DefaultCachePath,
        Tenant = config.Tenant,
        LoginHint = config.LoginHint,
        Scopes = GraphNotebook.Scopes,
        Interactive = interactive,
        SignInHint = "At a console, run: onenote-sync sign-in",
    });
}
