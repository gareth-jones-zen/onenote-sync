using WinBackground;

namespace OneNoteSync.Tests;

/// <summary>[schedule], background-tools-lib's shared scheduling settings (1.4.0), and the old [pull] form.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "onenote-sync-schedule-" + Guid.NewGuid().ToString("N"));
    private const string Notebook = "[notebook]\nowner = \"me\"\nname = \"N\"\nsection = \"S\"\n";
    private const string Pull = "[pull]\nfolder = \"C:/pulled\"\n";
    private static readonly DateTime Saturday = new(2026, 11, 14, 10, 0, 0);
    private static readonly DateTime Monday = new(2026, 11, 9);

    public ScheduleTests() => Directory.CreateDirectory(dir);

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

    private Config Load(string text, string name = "onenote-sync.toml")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return Config.Load(path, _ => null);
    }

    [Fact]
    public void With_no_schedule_the_defaults_are_unchanged()
    {
        var pull = Load(Notebook + Pull).Pull!;
        Assert.Equal((15, "07:00", "19:00", true), (pull.EveryMinutes, pull.HoursFrom, pull.HoursTo, pull.Weekdays));
        Assert.False(pull.ScheduleFromOldKeys);
        Assert.True(Watcher.Due(pull, Monday.AddHours(7)));
        Assert.False(Watcher.Due(pull, Monday.AddHours(19)));
        Assert.False(Watcher.Due(pull, Saturday));
    }

    [Fact]
    public void A_schedule_table_is_read()
    {
        var pull = Load(Notebook + Pull + "\n[schedule]\nevery_minutes = 15\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n").Pull!;
        Assert.Equal((15, "00:00", "24:00", false), (pull.EveryMinutes, pull.HoursFrom, pull.HoursTo, pull.Weekdays));
        Assert.True(Watcher.Due(pull, Saturday));
        Assert.False(pull.ScheduleFromOldKeys);
    }

    [Fact]
    public void The_old_pull_keys_still_work_and_mean_the_same()
    {
        var old = Load(Notebook + Pull + "every_minutes = 15\nhours = \"00:00-24:00\"\nweekdays = false\n").Pull!;
        var now = Load(Notebook + Pull + "\n[schedule]\nevery_minutes = 15\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n", "b.toml").Pull!;
        Assert.True(old.ScheduleFromOldKeys);
        Assert.Equal(now.Effective.Fingerprint(TaskKind.Watcher), old.Effective.Fingerprint(TaskKind.Watcher));
        foreach (var t in new[] { Saturday, Monday.AddHours(3), Monday.AddHours(23).AddMinutes(59) })
            Assert.Equal(Watcher.Due(now, t), Watcher.Due(old, t));
    }

    [Fact]
    public void Both_forms_at_once_are_refused() =>
        Assert.Contains("[schedule]", Assert.Throws<UsageException>(() =>
            Load(Notebook + Pull + "every_minutes = 15\n\n[schedule]\nevery_minutes = 15\n")).Message);

    [Fact]
    public void A_bad_schedule_is_refused()
    {
        Assert.Throws<UsageException>(() => Load(Notebook + Pull + "\n[schedule]\nevery_minute = 15\n"));
        Assert.Throws<UsageException>(() => Load(Notebook + Pull + "\n[schedule]\ndaily_at = \"07:00\"\n", "b.toml"));
    }

    [Fact]
    public void Autostart_status_says_when_the_task_no_longer_matches()
    {
        var path = Path.Combine(dir, "onenote-sync.toml");
        File.WriteAllText(path, Notebook + Pull + "\n[schedule]\nevery_minutes = 15\n");
        var task = new FakeTask();
        string Status()
        {
            var output = new StringWriter();
            Cli.Run(["--config", path, "autostart", "status"], new CliHost { Out = output, Err = output, Task = () => task, Env = _ => null });
            return output.ToString();
        }
        Assert.Contains("registered before its schedule was recorded", Status());
        task.Description = ScheduleDrift.WithFingerprint("x", "v1 job every=15 daily=- logon=yes unlock=0");
        Assert.Contains("settings changed since this task was registered: run `onenote-sync autostart on`", Status());
        task.Description = ScheduleDrift.WithFingerprint("x", "v1 watcher logon=yes");
        Assert.DoesNotContain("autostart on`", Status());
    }

    private sealed class FakeTask : ITask
    {
        public string Description { get; set; } = "x";
        public string Name => AutoStart.TaskName;
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => "";
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() =>
            new Dictionary<string, string> { ["state"] = "Running", ["description"] = Description };
    }
}
