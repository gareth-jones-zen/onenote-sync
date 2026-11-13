using PyCompat;
using WinBackground;

namespace OneNoteSync;

/// <summary>The watcher at logon, through Windows Task Scheduler.
///
/// `autostart on` copies both programs to %LOCALAPPDATA%\Programs\onenote-sync (so the task doesn't depend on where
/// a download was unzipped, and an upgrade is "run the new one's autostart on"), then registers the task to run the
/// installed onenote-sync-background.exe --config SETTINGS watch directly: no window, and stopping the task stops
/// the watcher itself. Restarted if it crashes; never stopped for time. The task's name is the Python's, so
/// switching between them needs no change anywhere else.</summary>
public static class AutoStart
{
    public const string TaskName = "onenote-sync watch";
    public const string ConsoleExe = "onenote-sync.exe";
    public const string BackgroundExe = "onenote-sync-background.exe";
    public static readonly string[] Programs = [ConsoleExe, BackgroundExe];

    private static readonly Dictionary<string, string> Results = new() { ["3"] = "another watcher was already running" };

    public static ITask Create() =>
        new ScheduledTask(TaskName, "Pulls a shared OneNote section's newest pages to files (onenote-sync watch).", Results);

    public static string InstallDir(Func<string, string?> env)
    {
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "Programs", "onenote-sync"));
    }

    public static List<string> Arguments(Config config) => ["--config", config.Path, "watch"];

    /// <summary>Copy both programs into installDir, unless they're running from there already, and return the
    /// background program the task should run. Throws IOException if one is missing or in use.</summary>
    public static string Install(string programDir, string installDir)
    {
        if (!PyPath.Same(programDir, installDir))
        {
            var missing = Programs.Where(name => !File.Exists(Path.Combine(programDir, name))).ToList();
            if (missing.Count > 0)
                throw new IOException($"Can't install: {string.Join(" and ", missing)} isn't beside this program ({programDir}).");
            Directory.CreateDirectory(installDir);
            foreach (var name in Programs)
                File.Copy(Path.Combine(programDir, name), Path.Combine(installDir, name), overwrite: true);
        }
        return Path.Combine(installDir, BackgroundExe);
    }
}
