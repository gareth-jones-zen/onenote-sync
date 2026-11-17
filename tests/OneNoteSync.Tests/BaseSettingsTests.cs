namespace OneNoteSync.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "onenote-sync-base-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        login_hint = "me@example.com"
        tenant = "contoso"
        msal_cache = "%EA_LOCAL%/msal-cache.json"
        time_zone = "GMT Standard Time"

        [paths]
        EA_LOCAL = "C:/Users/me/AppData/Local/claude-ea"

        [pull]
        folder = "%EA_LOCAL%/pulled"
        newest = 5

        [git]
        push = true
        """;

    private const string Notebook = "[notebook]\nowner = \"me\"\nname = \"N\"\nsection = \"S\"\n";

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), Base);
    }

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

    private static string? Env(string name) => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null;

    private Config Load(string text)
    {
        var path = Path.Combine(dir, "onenote-sync.toml");
        File.WriteAllText(path, text);
        return Config.Load(path, Env);
    }

    [Fact]
    public void Shared_sign_in_and_paths_come_from_the_base()
    {
        var config = Load("base = \"base-config.toml\"\nstate_file = \"%EA_LOCAL%/onenote-sync-state.json\"\n" + Notebook);
        Assert.Equal("me@example.com", config.LoginHint);
        Assert.Equal("contoso", config.Tenant);
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-ea\msal-cache.json", config.MsalCache);
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-ea\onenote-sync-state.json", config.StateFile);
        Assert.Equal(@"C:\Users\me\AppData\Local\onenote-sync\onenote-sync.log", config.LogFile);
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("base = \"base-config.toml\"\nlogin_hint = \"own@example.com\"\ntenant = \"own\"\n" + Notebook);
        Assert.Equal("own@example.com", config.LoginHint);
        Assert.Equal("own", config.Tenant);
    }

    [Fact]
    public void A_table_applies_only_where_the_tool_has_the_section()
    {
        Assert.Null(Load("base = \"base-config.toml\"\n" + Notebook).Pull);
        var pull = Load("base = \"base-config.toml\"\n" + Notebook + "[pull]\nnewest = 3\n").Pull!;
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-ea\pulled", pull.Folder);
        Assert.Equal(3, pull.Newest);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<UsageException>(() => Load("base = \"nope.toml\"\n" + Notebook));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
