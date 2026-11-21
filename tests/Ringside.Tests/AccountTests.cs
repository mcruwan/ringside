using System.IO;
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
