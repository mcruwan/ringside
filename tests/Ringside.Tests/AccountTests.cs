using System.IO;
using System.Linq;
using Xunit;

namespace Ringside.Tests;

/// <summary>A second Claude account: its own feed file and its own meter settings.</summary>
public sealed class AccountTests
{
    const string Home = "/Users/someone";

    [Theory]
    [InlineData(null, "claude-statusline.json")]
    [InlineData("", "claude-statusline.json")]
    [InlineData("  ", "claude-statusline.json")]
    [InlineData("/Users/someone/.claude", "claude-statusline.json")]
    [InlineData("/Users/someone/.claude/", "claude-statusline.json")]
    [InlineData("/Users/someone/.claude-company", "claude-statusline-claude-company.json")]
    [InlineData("/Users/someone/.claude-company/", "claude-statusline-claude-company.json")]
    [InlineData("/Volumes/Work Disk/My Claude", "claude-statusline-my-claude.json")]
    [InlineData("/elsewhere/.claude", "claude-statusline.json")]
    [InlineData("/x/...", "claude-statusline.json")]
    public void Each_config_folder_gets_its_own_feed_file(string? configDir, string expected) =>
        Assert.Equal(expected, StatusLineBridge.FeedFileFor(configDir, Home));

    [Fact]
    public void The_plugin_names_feed_files_the_same_way()
    {
        // register.js runs inside Claude Code, not here; this holds its rule to the bridge's.
        var root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "claude-plugin", "hooks", "register.js")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var js = File.ReadAllText(Path.Combine(root!, "claude-plugin", "hooks", "register.js"));
        Assert.Contains("$.env.get('CLAUDE_CONFIG_DIR')", js);
        Assert.Contains("claude-statusline-${slug}.json", js);
        Assert.Contains(".replace(/[^a-z0-9-]+/g, '-')", js);
        Assert.Contains("slug === 'claude'", js);
    }

    [Fact]
    public void The_second_account_has_its_own_switch_and_color()
    {
        var s = new Settings();
        Assert.False(s.Shows(Settings.ClaudeWorkName));
        s.ShowClaudeWork = true;
        s.ClaudeWorkColor = "#112233";
        Assert.True(s.Shows(Settings.ClaudeWorkName));
        Assert.Equal("#112233", s.ColorOf(Settings.ClaudeWorkName));
        Assert.Equal(Palette.ClaudeDefault, s.ColorOf("Claude"));
        Assert.Equal(Palette.CodexDefault, s.ColorOf("Codex"));
    }

    [Fact]
    public void The_second_account_alone_is_a_valid_choice()
    {
        var s = new Settings { ShowClaude = false, ShowCodex = false, ShowClaudeWork = true };
        s.Normalize();
        Assert.False(s.ShowClaude);
        Assert.False(s.ShowCodex);
        Assert.True(s.ShowClaudeWork);
    }

    [Fact]
    public void Bad_second_account_values_fall_back_to_defaults()
    {
        var s = Settings.Parse("""{"Version":2,"ShowClaudeWork":true,"ClaudeWorkColor":"nope","ClaudeWorkAppFolder":"","ClaudeWorkConfigDir":" "}""");
        Assert.Equal(Palette.ClaudeWorkDefault, s.ClaudeWorkColor);
        Assert.Equal("Claude-Company", s.ClaudeWorkAppFolder);
        Assert.Equal(".claude-company", s.ClaudeWorkConfigDir);
        Assert.Equal(Path.Combine(Paths.Home, ".claude-company"), s.ClaudeWorkConfigPath);
    }

    [Fact]
    public void Old_settings_files_keep_the_second_account_off()
    {
        var s = Settings.Parse("""{"Version":2,"OnboardingDone":true,"ShowClaude":true,"ShowCodex":true}""");
        Assert.False(s.ShowClaudeWork);
    }
}

/// <summary>Readings name the feed file they were meant for; the original file can insist on it.</summary>
public sealed class TaggedFeedTests : System.IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ringside-tests-" + System.Guid.NewGuid().ToString("N"));

    public TaggedFeedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static long Ms(System.DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    (string Log, string Feed) Files(System.DateTimeOffset now, string? tag)
    {
        var log = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(log, $$$"""{"samples":[{"t":{{{Ms(now.AddMinutes(-20))}}},"org":"a","u":{"fh":30,"sd":85}}]}""");
        var feed = Path.Combine(_dir, "claude-statusline.json");
        string tagPart = tag is null ? "" : $"\"feed\":\"{tag}\",";
        File.WriteAllText(feed, $$$"""{"updated":{{{now.AddMinutes(-1).ToUnixTimeSeconds()}}},{{{tagPart}}}"five_hour":{"used_percentage":13},"seven_day":{"used_percentage":31}}""");
        return (log, feed);
    }

    [Fact]
    public void One_account_still_takes_untagged_readings()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, null);
        var s = new ClaudeSource(log, feed).Read(now);
        Assert.Equal(ClaudeSource.FeedText, s.Source);
    }

    [Fact]
    public void With_two_accounts_an_untagged_reading_is_turned_away()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, null);
        var s = new ClaudeSource(log, feed) { RequireTaggedFeed = true }.Read(now);
        Assert.Equal(ClaudeSource.DesktopText, s.Source);
        Assert.Equal(30, s.Windows.Single(w => w.Label == "5-hour").Percent);
    }

    [Fact]
    public void With_two_accounts_a_reading_tagged_for_this_file_is_taken()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, StatusLineBridge.DefaultFeedFile);
        var s = new ClaudeSource(log, feed) { RequireTaggedFeed = true }.Read(now);
        Assert.Equal(ClaudeSource.FeedText, s.Source);
        Assert.Equal(13, s.Windows.Single(w => w.Label == "5-hour").Percent);
    }

    [Fact]
    public void A_reading_tagged_for_another_file_is_never_taken()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, "claude-statusline-claude-company.json");
        var s = new ClaudeSource(log, feed).Read(now);
        Assert.Equal(ClaudeSource.DesktopText, s.Source);
    }

    [Fact]
    public void The_plugin_tags_each_reading_with_its_file()
    {
        var root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "claude-plugin", "hooks", "register.js")))
            root = Path.GetDirectoryName(root);
        var js = File.ReadAllText(Path.Combine(root!, "claude-plugin", "hooks", "register.js"));
        Assert.Contains("feed: file", js);
    }
}
