using System;
using System.Collections.Generic;

namespace Ringside.Ui;

/// <summary>One poll's worth of data: both apps' limits plus each one's pace forecast.</summary>
public sealed record Reading(Snapshot Claude, Snapshot Codex, DateTimeOffset Now, IReadOnlyDictionary<string, DateTimeOffset?> RunsOut)
{
    /// <summary>The second Claude account, when its meter is on.</summary>
    public Snapshot? ClaudeWork { get; init; }

    public Snapshot Of(string provider) => provider switch
    {
        "Codex" => Codex,
        Settings.ClaudeWorkName => ClaudeWork ?? Snapshot.Empty(Settings.ClaudeWorkName, "Waiting for the second Claude account", ""),
        _ => Claude,
    };

    /// <summary>Every snapshot in this reading, the second Claude account's when present.</summary>
    public IEnumerable<Snapshot> All()
    {
        yield return Claude;
        if (ClaudeWork is not null) yield return ClaudeWork;
        yield return Codex;
    }

    public DateTimeOffset? RunsOutFor(string provider) => RunsOut.TryGetValue(provider, out var t) ? t : null;

    public static Reading Waiting(DateTimeOffset now) => new(
        Snapshot.Empty("Claude", "Waiting for the Claude app or Claude Code", ""),
        Snapshot.Empty("Codex", "Waiting for a Codex reply", ""),
        now, new Dictionary<string, DateTimeOffset?>());

    /// <summary>Believable numbers for previews, the onboarding cards and README images.</summary>
    public static Reading Sample(DateTimeOffset now, double claude = 42, double codex = 18, double claudeWeek = 31, double codexWeek = 12,
        DateTimeOffset? claudeRunsOut = null)
    {
        var c = new Snapshot("Claude", null, new[]
        {
            new LimitWindow("5-hour", claude, now.AddHours(2).AddMinutes(14), ResetKind.Exact, false),
            new LimitWindow("Weekly", claudeWeek, now.AddDays(3).AddHours(5), ResetKind.Exact, false),
        }, now.AddMinutes(-1), TimeSpan.FromMinutes(60), null, "");
        var x = new Snapshot("Codex", "Pro", new[]
        {
            new LimitWindow("5-hour", codex, now.AddHours(3).AddMinutes(41), ResetKind.Exact, false),
            new LimitWindow("Weekly", codexWeek, now.AddDays(5).AddHours(2), ResetKind.Exact, false),
        }, now.AddMinutes(-3), null, "1,250 credits left", "");
        return new Reading(c, x, now, new Dictionary<string, DateTimeOffset?> { ["Claude"] = claudeRunsOut, ["Codex"] = null });
    }
}
