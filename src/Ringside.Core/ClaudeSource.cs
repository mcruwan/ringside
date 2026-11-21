using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ringside;

/// <summary>
/// Claude usage from two local files, never from your login:
/// the Claude desktop app's own usage log (sampled every 15 minutes while the app is open),
/// and, when connected, the limits Claude Code hands to its status line after each reply.
/// The newer of the two wins. When turned on, Claude Code's own usage report adds the limits
/// neither file carries, such as the weekly one for a single model (see ClaudeUsageReport).
/// </summary>
public sealed class ClaudeSource
{
    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    internal const string DesktopText =
        "From the Claude app's own usage log, which it updates when it refreshes its usage card, sometimes hours apart. " +
        "The reset time is estimated from those samples.";
    internal const string StaleText = "Waiting for the Claude app to refresh its usage";
    internal const string FeedText =
        "From Claude Code's status line, which reports exact limits and reset times after each reply.";
    internal const string ReportText =
        "From Claude Code's own usage report (its /usage command), which Ringside asks for every 10 minutes.";

    readonly string? _logOverride;
    readonly string[]? _feedOverride;

    // Which account this source reads: its name on the meter, the Claude app's data folder and
    // the Claude Code feed file. The defaults are the one account Ringside has always read.
    string _provider = "Claude";
    string _appFolder = "Claude";
    string _feedFile = StatusLineBridge.DefaultFeedFile;
    string? _configDir;
    bool IsDefaultAccount => _appFolder == "Claude" && _feedFile == StatusLineBridge.DefaultFeedFile;

    /// <summary>
    /// Another Claude account on this computer: a second copy of the Claude app with its own data
    /// folder (for one, ~/Library/Application Support/Claude-Company) and Claude Code with its own
    /// config folder (CLAUDE_CONFIG_DIR, for one ~/.claude-company), shown as its own meter.
    /// </summary>
    public static ClaudeSource ForAccount(string provider, string appFolder, string configDir)
    {
        var s = new ClaudeSource();
        s._provider = provider;
        s._appFolder = appFolder;
        s._feedFile = StatusLineBridge.FeedFileFor(configDir, Paths.Home);
        s._configDir = s._feedFile == StatusLineBridge.DefaultFeedFile ? null : configDir;
        return s;
    }

    public string Provider => _provider;

    sealed record Sample(DateTimeOffset T, string Org, double? FiveHour, double? Weekly, double? Extra);
    sealed record Reading(IReadOnlyList<LimitWindow> Windows, DateTimeOffset AsOf);

    List<Sample> _samples = new();
    DateTime _logStamp;
    string? _logPath;
    DateTimeOffset _logLookup = DateTimeOffset.MinValue;

    JsonDocument? _feed;
    DateTime _feedStamp;
    string? _feedPath;
    string[] _feeds = Array.Empty<string>();
    DateTimeOffset _feedLookup = DateTimeOffset.MinValue;

    /// <param name="feeds">Where Claude Code's feed may be; by default Ringside's own folder and
    /// the Store app's copies of it.</param>
    public ClaudeSource(string? desktopLog = null, params string[] feeds)
    {
        _logOverride = desktopLog;
        _feedOverride = feeds.Length > 0 ? feeds : null;
    }

    public bool DesktopLogFound => ResolveLog(DateTimeOffset.Now) is not null;

    /// <summary>
    /// Also ask Claude Code for its usage report, for the limits the two files do not carry
    /// (a single model's weekly limit). Off unless the user turns it on.
    /// </summary>
    public bool AskClaudeCode { get; set; }

    /// <summary>
    /// Also show extra usage as a row ("Extra": the share of the month's extra-usage budget spent),
    /// for an account that has it. The Claude app's log records it; so does the usage report.
    /// </summary>
    public bool ShowExtraUsage { get; set; }

    /// <summary>Has the usage report asked for again at the next read (a manual refresh).</summary>
    public void AskSoon(DateTimeOffset now) => _usage?.AskSoon(now);

    ClaudeUsageReport? _usage;
    internal ClaudeUsageReport Usage => _usage ??= new ClaudeUsageReport(_appFolder, _configDir);

    public Snapshot Read(DateTimeOffset now)
    {
        RefreshLog(now);
        RefreshFeed(now);
        var desktop = FromLog(now);
        var report = FromReport(now);
        var feed = FromFeed(now);

        // Two sources give exact numbers; the newer one speaks for the 5-hour and weekly limits.
        string exactText = FeedText;
        if (report is not null && (feed is null || report.AsOf > feed.AsOf))
        {
            feed = new Reading(report.Windows.Where(w => ClaudeUsageReport.IsPlanWindow(w.Label)).ToList(), report.AsOf);
            exactText = ReportText;
            if (feed.Windows.Count == 0) feed = null;
        }
        // A limit for one model comes only from the usage report, whichever source leads.
        var scoped = report?.Windows.Where(w => !ClaudeUsageReport.IsPlanWindow(w.Label)).ToList() ?? new List<LimitWindow>();
        scoped.RemoveAll(w => w.Label == ClaudeUsageReport.ExtraLabel && !ShowExtraUsage);
        if (ShowExtraUsage && scoped.All(w => w.Label != ClaudeUsageReport.ExtraLabel) && LogExtra() is double spent)
            scoped.Add(new LimitWindow(ClaudeUsageReport.ExtraLabel, spent, null, ResetKind.None, false) { Aside = true });

        if (desktop is null && feed is null && scoped.Count == 0)
            return Snapshot.Empty(_provider, IsDefaultAccount
                ? "Waiting for the Claude app or Claude Code"
                : $"Waiting for {_provider}: its Claude app or Claude Code", DesktopText);

        if (desktop is null && feed is null)
            return new Snapshot(_provider, null, scoped, report?.AsOf ?? _samples[^1].T, TimeSpan.FromMinutes(60), null,
                report is null ? DesktopText : ReportText);

        if (feed is not null && (desktop is null || feed.AsOf >= desktop.AsOf))
            return new Snapshot(_provider, null, feed.Windows.Concat(scoped).ToList(), feed.AsOf, TimeSpan.FromMinutes(60), null, exactText);

        var windows = desktop!.Windows.ToList();
        if (feed is not null)
        {
            // An exact reset time from Claude Code beats our estimate when both describe the same window.
            var fiveHour = feed.Windows.FirstOrDefault(w => w.Label == "5-hour");
            int i = windows.FindIndex(w => w.Label == "5-hour");
            if (i >= 0 && fiveHour?.ResetsAt is { } exact && !fiveHour.Lapsed &&
                windows[i].ResetsAt is { } guess && windows[i].Kind != ResetKind.Exact &&
                Math.Abs((exact - guess).TotalMinutes) <= 35)
                windows[i] = windows[i] with { ResetsAt = exact, Kind = ResetKind.Exact };

            // The desktop log does not record every plan's weekly limit; Claude Code does.
            if (!windows.Any(w => w.Label == "Weekly") &&
                feed.Windows.FirstOrDefault(w => w.Label == "Weekly") is { Lapsed: false } weekly)
                windows.Add(weekly);
        }
        // The Claude app only writes its log while its own usage card refreshes, which can be a day
        // apart. Say so, rather than showing an old number as if it were current.
        string? note = now - desktop.AsOf > TimeSpan.FromMinutes(35) ? StaleText : null;
        windows.AddRange(scoped);
        return new Snapshot(_provider, null, windows, desktop.AsOf, TimeSpan.FromMinutes(35), note, DesktopText);
    }

    // ---- Claude Code's usage report (asked for, when turned on) --------------------------------

    Reading? FromReport(DateTimeOffset now)
    {
        if (!AskClaudeCode) return null;
        // The account the Claude app's log is for; the report counts only if Claude Code in this
        // account's folder is signed in to the same one.
        string org = _samples.Count > 0 ? _samples[^1].Org : "";
        Usage.Poll(now, org);
        return Usage.Latest(now, org) is { } answer ? new Reading(answer.Windows(now).ToList(), answer.AsOf) : null;
    }

    // ---- the Claude desktop app's usage log ------------------------------------------

    IEnumerable<string> LogCandidates()
    {
        const string LogName = "plan-usage-history.json";
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Paths.Home, "Library", "Application Support", _appFolder, LogName);
            yield break;
        }
        if (!OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Paths.XdgConfig, _appFolder, LogName);
            yield break;
        }

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, _appFolder, LogName);

        // Store (MSIX) installs keep their roaming data inside the package folder.
        if (!IsDefaultAccount) yield break;
        foreach (var r in Paths.ClaudeStoreRoaming())
            yield return Path.Combine(r, "Claude", LogName);
    }

    string? ResolveLog(DateTimeOffset now)
    {
        if (_logOverride is not null) return File.Exists(_logOverride) ? _logOverride : null;
        if (now - _logLookup < TimeSpan.FromMinutes(5)) return _logPath;
        _logLookup = now;
        _logPath = LogCandidates().Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return _logPath;
    }

    void RefreshLog(DateTimeOffset now)
    {
        try
        {
            var path = ResolveLog(now);
            if (path is null) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _logStamp) return;
            using var fs = SharedFile.Open(path);
            _samples = ParseLog(fs);
            _logStamp = stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Caught mid-write; the next poll reads it.
        }
    }

    static List<Sample> ParseLog(Stream s)
    {
        using var doc = JsonDocument.Parse(s);
        var list = new List<Sample>();
        if (!doc.RootElement.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) continue;
            long ms = t.TryGetInt64(out var l) ? l : (long)t.GetDouble();
            string org = e.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
            double? fh = null, sd = null, xu = null;
            if (e.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("fh", out var f) && f.ValueKind == JsonValueKind.Number) fh = f.GetDouble();
                if (u.TryGetProperty("sd", out var d) && d.ValueKind == JsonValueKind.Number) sd = d.GetDouble();
                if (u.TryGetProperty("xu", out var x) && x.ValueKind == JsonValueKind.Number) xu = x.GetDouble();
            }
            list.Add(new Sample(DateTimeOffset.FromUnixTimeMilliseconds(ms), org, fh, sd, xu));
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    Reading? FromLog(DateTimeOffset now)
    {
        if (_samples.Count == 0) return null;
        // The newest sample belongs to the account the app is signed in to now.
        string org = _samples[^1].Org;
        var mine = _samples.Where(x => x.Org == org).ToList();
        var latest = mine[^1];
        var windows = new List<LimitWindow>();
        var fh = mine.Where(x => x.FiveHour.HasValue).ToList();
        // A plan with no 5-hour limit logs none. Samples left from when the account still had one
        // must not keep an empty 5-hour row on the meter: the newest sample has to carry the limit,
        // or follow one that did by less than a window (a single reading that left it out).
        if (fh.Count > 0 && (latest.FiveHour.HasValue || latest.T - fh[^1].T < FiveHours))
            windows.Add(FiveHour(fh, now));
        if (latest.Weekly is double w) windows.Add(new LimitWindow("Weekly", w, null, ResetKind.None, false));
        return windows.Count == 0 ? null : new Reading(windows, latest.T);
    }

    // Extra usage as the app last logged it for this account. One reading that leaves it out does
    // not take the row away; a day of readings without it does.
    double? LogExtra()
    {
        if (_samples.Count == 0) return null;
        var latest = _samples[^1];
        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            var x = _samples[i];
            if (x.Org != latest.Org) continue;
            if (latest.T - x.T > TimeSpan.FromHours(24)) break;
            if (x.Extra is double v) return v;
        }
        return null;
    }

    // Usage only rises inside one 5-hour window, so the current window began just after
    // the last sample that was zero or higher than the one that followed it.
    static LimitWindow FiveHour(List<Sample> s, DateTimeOffset now)
    {
        int n = s.Count - 1;
        double v = s[n].FiveHour!.Value;
        if (v <= 0) return new LimitWindow("5-hour", 0, null, ResetKind.None, false);

        int k = n;
        while (k > 0)
        {
            double prev = s[k - 1].FiveHour!.Value;
            if (prev <= 0 || prev > s[k].FiveHour!.Value || s[n].T - s[k - 1].T >= FiveHours) break;
            k--;
        }

        var upper = s[k].T + FiveHours;
        if (now >= upper) return new LimitWindow("5-hour", 0, upper, ResetKind.Exact, true);
        if (k > 0)
        {
            var lower = s[k - 1].T + FiveHours;
            // Past the earliest moment the window could have reset, the old number may belong to
            // a window that is gone (the app logs 100% at the limit, then nothing until it next
            // refreshes), so it must not pass for the current one.
            if (now >= lower) return new LimitWindow("5-hour", 0, lower, ResetKind.Estimate, true);
            if (upper - lower <= TimeSpan.FromMinutes(31))
                return new LimitWindow("5-hour", v, lower + (upper - lower) / 2, ResetKind.Estimate, false);
        }
        return new LimitWindow("5-hour", v, upper, ResetKind.UpperBound, false);
    }

    // ---- Claude Code's status line (written by the Ringside status line bridge) ----------------

    // Claude Code run inside the Claude app from the Store saves the feed into the app's own copy
    // of Ringside's folder whenever that copy exists (see Paths.ClaudeStoreRoaming), and Ringside
    // started from Windows never sees it there. So look in every copy; the one written last is current.
    IEnumerable<string> FeedCandidates()
    {
        yield return Path.Combine(Paths.Data, _feedFile);
        if (Paths.Sandboxed) yield break;
        foreach (var r in Paths.ClaudeStoreRoaming())
            yield return Path.Combine(r, "Ringside", _feedFile);
    }

    string? ResolveFeed(DateTimeOffset now)
    {
        if (_feedOverride is null && now - _feedLookup >= TimeSpan.FromMinutes(5))
        {
            _feedLookup = now;
            _feeds = FeedCandidates().ToArray();
        }
        return (_feedOverride ?? _feeds).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    void RefreshFeed(DateTimeOffset now)
    {
        try
        {
            var path = ResolveFeed(now);
            if (path is null) { _feed?.Dispose(); _feed = null; return; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (path == _feedPath && stamp == _feedStamp && _feed is not null) return;
            using var fs = SharedFile.Open(path);
            var doc = JsonDocument.Parse(fs);
            _feed?.Dispose();
            _feed = doc;
            _feedPath = path;
            _feedStamp = stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>
    /// With a second Claude account on, the original feed file takes only readings that name it
    /// ("feed"). A plugin or bridge from before the split, still loaded in a Claude Code process
    /// that started before it, writes every account's limits to that one file without the tag.
    /// </summary>
    public bool RequireTaggedFeed { get; set; }

    Reading? FromFeed(DateTimeOffset now)
    {
        if (_feed is null) return null;
        var root = _feed.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        bool tagged = root.TryGetProperty("feed", out var tag) && tag.ValueKind == JsonValueKind.String;
        if (tagged && tag.GetString() != _feedFile) return null;
        if (!tagged && RequireTaggedFeed) return null;
        if (!root.TryGetProperty("updated", out var up) || up.ValueKind != JsonValueKind.Number) return null;
        var asOf = DateTimeOffset.FromUnixTimeSeconds((long)up.GetDouble());

        var windows = new List<LimitWindow>();
        foreach (var (key, label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Weekly") })
        {
            if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("used_percentage", out var pct) || pct.ValueKind != JsonValueKind.Number) continue;
            DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds((long)r.GetDouble())
                : null;
            bool lapsed = resets is { } x && now >= x;
            windows.Add(new LimitWindow(label, lapsed ? 0 : pct.GetDouble(), resets,
                resets is null ? ResetKind.None : ResetKind.Exact, lapsed));
        }
        return windows.Count == 0 ? null : new Reading(windows, asOf);
    }
}
