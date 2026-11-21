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

/// <summary>Limits for one model (the weekly "Fable" one), from Claude Code's usage report.</summary>
public sealed class ModelLimitTests : System.IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ringside-tests-" + System.Guid.NewGuid().ToString("N"));

    public ModelLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    const string Org = "org-work";

    static string State(System.DateTimeOffset fetched, string limits, string cachedAccount = "acct-1") =>
        "{\"oauthAccount\":{\"accountUuid\":\"acct-1\",\"organizationUuid\":\"" + Org + "\"}," +
        "\"cachedUsageUtilization\":{\"fetchedAtMs\":" + fetched.ToUnixTimeMilliseconds() + ",\"accountUuid\":\"" + cachedAccount + "\"," +
        "\"utilization\":{\"five_hour\":{\"utilization\":16,\"resets_at\":\"2030-01-01T00:00:00+00:00\"},\"limits\":[" + limits + "]}}}";

    const string Session = """{"kind":"session","group":"session","percent":16,"resets_at":"2030-01-01T03:40:00.5+00:00","scope":null}""";
    const string Week = """{"kind":"weekly_all","group":"weekly","percent":32,"resets_at":"2030-01-03T09:00:00+00:00","scope":null}""";
    const string Fable = """{"kind":"weekly_scoped","group":"weekly","percent":6,"resets_at":"2030-01-03T09:00:00+00:00","scope":{"model":{"id":null,"display_name":"Fable"},"surface":null}}""";

    [Fact]
    public void The_report_names_each_limit()
    {
        var at = System.DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);
        var a = ClaudeUsageReport.Parse(State(at, $"{Session},{Week},{Fable}"));
        Assert.NotNull(a);
        Assert.Equal(Org, a!.Org);
        Assert.Equal(at, a.AsOf);
        Assert.Equal(new[] { "5-hour", "Weekly", "Fable" }, a.Limits.Select(l => l.Label));
        var fable = a.Limits.Single(l => l.Label == "Fable");
        Assert.Equal(6, fable.Percent);
        Assert.Equal(new System.DateTimeOffset(2030, 1, 3, 9, 0, 0, System.TimeSpan.Zero), fable.ResetsAt);
    }

    [Fact]
    public void An_account_with_no_5_hour_limit_gets_no_5_hour_row()
    {
        var a = ClaudeUsageReport.Parse(State(System.DateTimeOffset.UtcNow, $"{Week},{Fable}"));
        Assert.Equal(new[] { "Weekly", "Fable" }, a!.Limits.Select(l => l.Label));
    }

    [Fact]
    public void An_answer_kept_from_another_sign_in_is_not_used()
    {
        Assert.Null(ClaudeUsageReport.Parse(State(System.DateTimeOffset.UtcNow, Fable, cachedAccount: "acct-2")));
        Assert.Null(ClaudeUsageReport.Parse("""{"oauthAccount":{"organizationUuid":"x"}}"""));
        Assert.Null(ClaudeUsageReport.Parse("not json"));
        Assert.Null(ClaudeUsageReport.Parse(null));
    }

    [Fact]
    public void A_limit_past_its_reset_reads_empty()
    {
        var a = ClaudeUsageReport.Parse(State(System.DateTimeOffset.UtcNow, Fable))!;
        var later = new System.DateTimeOffset(2030, 1, 4, 0, 0, 0, System.TimeSpan.Zero);
        var w = a.Windows(later).Single();
        Assert.True(w.Lapsed);
        Assert.Equal(0, w.Percent);
    }

    [Theory]
    [InlineData("""{"type":"result","num_turns":0,"local_command":"usage","result":"Current week"}""", true)]
    [InlineData("""warning first\n{"type":"result","num_turns":0,"local_command":"usage"}\nhook failed""", true)]
    [InlineData("""{"type":"result","num_turns":1,"result":"I can't run slash commands"}""", false)]
    [InlineData("""{"type":"result","num_turns":0,"local_command":"cost"}""", false)]
    [InlineData("", false)]
    public void Only_claude_codes_own_usage_command_counts_as_an_answer(string output, bool expected) =>
        Assert.Equal(expected, ClaudeUsageReport.AnsweredLocally(output.Replace("\\n", "\n")));

    (string Log, string Feed) Files(System.DateTimeOffset now, string usage, int feedMinutesAgo = -1)
    {
        var log = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(log, $$$"""{"samples":[{"t":{{{now.AddMinutes(-20).ToUnixTimeMilliseconds()}}},"org":"{{{Org}}}","u":{{{usage}}}}]}""");
        var feed = Path.Combine(_dir, "claude-statusline.json");
        if (feedMinutesAgo >= 0)
            File.WriteAllText(feed, $$$"""{"updated":{{{now.AddMinutes(-feedMinutesAgo).ToUnixTimeSeconds()}}},"five_hour":{"used_percentage":13},"seven_day":{"used_percentage":31}}""");
        return (log, feed);
    }

    static ClaudeSource With(string log, string feed, ClaudeUsageReport.Answer? answer)
    {
        var s = new ClaudeSource(log, feed) { AskClaudeCode = true };
        s.Usage.Quiet = true;
        if (answer is not null) s.Usage.Offer(answer);
        return s;
    }

    static ClaudeUsageReport.Answer Answer(System.DateTimeOffset asOf, string org, params (string Label, double Percent)[] limits) =>
        new(limits.Select(l => new ClaudeUsageReport.Limit(l.Label, l.Percent, asOf.AddDays(1))).ToList(), asOf, org);

    [Fact]
    public void A_model_limit_is_added_to_the_meter()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"fh":30,"sd":85}""");
        var s = With(log, feed, Answer(now.AddMinutes(-30), Org, ("5-hour", 16), ("Weekly", 32), ("Fable", 6))).Read(now);
        // The app's log is newer, so it still speaks for the two plan limits.
        Assert.Equal(ClaudeSource.DesktopText, s.Source);
        Assert.Equal(new[] { "5-hour", "Weekly", "Fable" }, s.Windows.Select(w => w.Label));
        Assert.Equal(30, s.Windows.Single(w => w.Label == "5-hour").Percent);
        Assert.Equal(6, s.Windows.Single(w => w.Label == "Fable").Percent);
    }

    [Fact]
    public void A_newer_report_speaks_for_every_limit()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"fh":30,"sd":85}""", feedMinutesAgo: 9);
        var s = With(log, feed, Answer(now.AddMinutes(-2), Org, ("5-hour", 16), ("Weekly", 32), ("Fable", 6))).Read(now);
        Assert.Equal(ClaudeSource.ReportText, s.Source);
        Assert.Equal(new[] { "5-hour", "Weekly", "Fable" }, s.Windows.Select(w => w.Label));
        Assert.Equal(16, s.Windows.Single(w => w.Label == "5-hour").Percent);
    }

    [Fact]
    public void A_newer_status_line_reading_keeps_the_model_limit_beside_it()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"fh":30,"sd":85}""", feedMinutesAgo: 1);
        var s = With(log, feed, Answer(now.AddMinutes(-8), Org, ("5-hour", 16), ("Weekly", 32), ("Fable", 6))).Read(now);
        Assert.Equal(ClaudeSource.FeedText, s.Source);
        Assert.Equal(new[] { "5-hour", "Weekly", "Fable" }, s.Windows.Select(w => w.Label));
        Assert.Equal(13, s.Windows.Single(w => w.Label == "5-hour").Percent);
    }

    [Fact]
    public void An_account_without_a_5_hour_limit_shows_none()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"sd":85,"xu":51.3}""");
        Assert.Equal(new[] { "Weekly" }, new ClaudeSource(log, feed).Read(now).Windows.Select(w => w.Label));
        var s = With(log, feed, Answer(now.AddMinutes(-3), Org, ("Weekly", 86), ("Fable", 40))).Read(now);
        Assert.Equal(new[] { "Weekly", "Fable" }, s.Windows.Select(w => w.Label));
    }

    [Fact]
    public void A_5_hour_limit_the_account_no_longer_has_leaves_the_meter()
    {
        var now = System.DateTimeOffset.UtcNow;
        long Ms(double daysAgo) => now.AddDays(-daysAgo).ToUnixTimeMilliseconds();
        var log = Path.Combine(_dir, "plan-usage-history.json");
        // Three weeks ago the plan had a 5-hour limit; since then the app logs only the weekly one.
        File.WriteAllText(log, $$$"""{"samples":[{"t":{{{Ms(21)}}},"org":"a","u":{"fh":9,"sd":85}},{"t":{{{Ms(19)}}},"org":"a","u":{"sd":100}},{"t":{{{Ms(0.01)}}},"org":"a","u":{"sd":85,"xu":51.3}}]}""");
        var s = new ClaudeSource(log, Path.Combine(_dir, "none.json")).Read(now);
        Assert.Equal(new[] { "Weekly" }, s.Windows.Select(w => w.Label));

        // One reading that leaves the limit out, minutes after one that had it, changes nothing.
        File.WriteAllText(log, $$$"""{"samples":[{"t":{{{Ms(0.02)}}},"org":"a","u":{"fh":9,"sd":85}},{"t":{{{Ms(0.01)}}},"org":"a","u":{"sd":85}}]}""");
        s = new ClaudeSource(log, Path.Combine(_dir, "none.json")).Read(now);
        Assert.Equal(new[] { "5-hour", "Weekly" }, s.Windows.Select(w => w.Label));
    }

    [Fact]
    public void A_report_from_another_account_stays_off_the_meter()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"sd":85}""");
        var s = With(log, feed, Answer(now.AddMinutes(-3), "org-other", ("5-hour", 16), ("Weekly", 32), ("Fable", 6))).Read(now);
        Assert.Equal(new[] { "Weekly" }, s.Windows.Select(w => w.Label));
        Assert.Equal(85, s.Windows.Single().Percent);
    }

    [Fact]
    public void An_old_report_is_dropped()
    {
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"fh":30,"sd":85}""");
        var s = With(log, feed, Answer(now.AddMinutes(-61), Org, ("Fable", 6))).Read(now);
        Assert.Equal(new[] { "5-hour", "Weekly" }, s.Windows.Select(w => w.Label));
    }

    [Fact]
    public void Model_limits_stay_off_until_turned_on()
    {
        Assert.False(new Settings().ModelLimits);
        var now = System.DateTimeOffset.UtcNow;
        var (log, feed) = Files(now, """{"fh":30,"sd":85}""");
        var src = With(log, feed, Answer(now.AddMinutes(-3), Org, ("Fable", 6)));
        src.AskClaudeCode = false;
        Assert.Equal(new[] { "5-hour", "Weekly" }, src.Read(now).Windows.Select(w => w.Label));
    }
}
