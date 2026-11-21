using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ringside;

/// <summary>
/// Per-model limits (for one, "Fable"), which neither the Claude app's usage log nor Claude Code's
/// status line carries. Claude Code has them: its /usage command asks Anthropic and keeps the
/// answer in its own state file. So, when turned on, Ringside runs that command every ten minutes
/// (no chat, no tokens, nothing saved as a session) and reads the answer from that file.
/// Ringside itself still never sees a login: Claude Code does the asking with its own.
/// </summary>
public sealed class ClaudeUsageReport
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    /// <summary>An answer older than this is not shown.</summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromMinutes(60);
    static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(45);

    public sealed record Limit(string Label, double Percent, DateTimeOffset? ResetsAt);

    /// <summary>The row for extra usage: how much of the month's extra-usage budget is spent.</summary>
    public const string ExtraLabel = "Extra";

    /// <summary>One answer: the limits, when Claude Code got them, and the organization it is signed in to.</summary>
    public sealed record Answer(IReadOnlyList<Limit> Limits, DateTimeOffset AsOf, string Org)
    {
        public IEnumerable<LimitWindow> Windows(DateTimeOffset now)
        {
            foreach (var l in Limits)
            {
                bool lapsed = l.ResetsAt is { } r && now >= r;
                yield return new LimitWindow(l.Label, lapsed ? 0 : l.Percent, l.ResetsAt,
                    l.ResetsAt is null ? ResetKind.None : ResetKind.Exact, lapsed) { Aside = l.Label == ExtraLabel };
            }
        }
    }

    readonly string _appFolder;
    readonly string? _configDir;
    readonly object _gate = new();
    Answer? _latest;
    DateTimeOffset _asked = DateTimeOffset.MinValue;
    bool _running, _loaded, _off;

    /// <summary>For tests: never runs Claude Code; answers come from <see cref="Offer"/>.</summary>
    internal bool Quiet { get; set; }

    internal void Offer(Answer answer)
    {
        lock (_gate) _latest = answer;
    }

    /// <param name="appFolder">The Claude app's data folder by name, where its copy of Claude Code lives.</param>
    /// <param name="configDir">Claude Code's config folder for this account; null for the default one.</param>
    public ClaudeUsageReport(string appFolder, string? configDir)
    {
        _appFolder = appFolder;
        _configDir = string.IsNullOrWhiteSpace(configDir) ? null : configDir;
    }

    /// <summary>Claude Code's state file: ~/.claude.json, or .claude.json inside a named config folder.</summary>
    public string StateFile => Path.Combine(_configDir ?? Paths.Home, ".claude.json");

    /// <summary>
    /// The last answer, if it belongs to the account the meter shows. <paramref name="meterOrg"/> is
    /// the organization the Claude app's log is for ("" when there is no log to compare with).
    /// </summary>
    public Answer? Latest(DateTimeOffset now, string meterOrg)
    {
        Answer? a;
        lock (_gate) a = _latest;
        if (a is null || now - a.AsOf > GoodFor || a.AsOf - now > TimeSpan.FromMinutes(5)) return null;
        return SameAccount(a.Org, meterOrg) ? a : null;
    }

    // Claude Code in this folder may be signed in to a different account than the Claude app the
    // meter follows. Its numbers then belong on no meter here.
    static bool SameAccount(string loginOrg, string meterOrg) =>
        meterOrg.Length == 0 || string.Equals(loginOrg, meterOrg, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks again on the next <see cref="Poll"/>, unless it asked within the last minute.</summary>
    public void AskSoon(DateTimeOffset now)
    {
        lock (_gate)
            if (now - _asked > TimeSpan.FromMinutes(1)) _asked = DateTimeOffset.MinValue;
    }

    /// <summary>Starts a run in the background when one is due. Never waits for it.</summary>
    public void Poll(DateTimeOffset now, string meterOrg)
    {
        lock (_gate)
        {
            if (Quiet || _off || _running || Paths.Sandboxed) return;
            if (!_loaded) { _loaded = true; Load(); }
            if (now - _asked < Every) return;
            _asked = now;

            // Not signed in here, or signed in to another account: nothing to ask.
            string? org = LoginOrg(ReadState());
            if (org is null || !SameAccount(org, meterOrg)) return;
            _running = true;
        }
        Task.Run(Run);
    }

    void Run()
    {
        try
        {
            if (FindClaude() is not { } claude) return;
            if (Ask(claude)) Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or JsonException)
        {
            // No Claude Code to ask, or it would not start; the meter goes on without these limits.
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }

    void Load()
    {
        var answer = Parse(ReadState());
        lock (_gate)
            if (answer is not null) _latest = answer;
    }

    string? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            using var fs = SharedFile.Open(StateFile);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The organization Claude Code is signed in to, from its state file.</summary>
    public static string? LoginOrg(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("oauthAccount", out var acct) && acct.ValueKind == JsonValueKind.Object &&
                   acct.TryGetProperty("organizationUuid", out var org) && org.ValueKind == JsonValueKind.String &&
                   org.GetString() is { Length: > 0 } id ? id : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The usage answer Claude Code keeps in its state file ("cachedUsageUtilization"): the session
    /// limit as "5-hour", the all-models weekly limit as "Weekly", and each limit scoped to a model
    /// or a surface under that model's or surface's own name.
    /// </summary>
    public static Answer? Parse(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("oauthAccount", out var acct) || acct.ValueKind != JsonValueKind.Object) return null;
            if (Text(acct, "organizationUuid") is not { Length: > 0 } org) return null;
            if (!root.TryGetProperty("cachedUsageUtilization", out var cache) || cache.ValueKind != JsonValueKind.Object) return null;
            // An answer kept from an earlier sign-in in this folder is someone else's.
            if (Text(cache, "accountUuid") is { } cached && Text(acct, "accountUuid") is { } current && cached != current) return null;
            if (!cache.TryGetProperty("fetchedAtMs", out var at) || at.ValueKind != JsonValueKind.Number) return null;
            if (!cache.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Object) return null;

            var limits = new List<Limit>();
            if (u.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("percent", out var pct) || pct.ValueKind != JsonValueKind.Number) continue;
                    string? label = Text(e, "kind") switch
                    {
                        "session" => "5-hour",
                        "weekly_all" => "Weekly",
                        _ => ScopeName(e),
                    };
                    if (label is null || limits.Any(l => l.Label == label)) continue;
                    limits.Add(new Limit(label, pct.GetDouble(), Time(e, "resets_at")));
                }
            }
            else
            {
                // Before Claude Code listed limits one by one, it had only the two plan windows.
                foreach (var (key, label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Weekly") })
                    if (u.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object &&
                        w.TryGetProperty("utilization", out var pct) && pct.ValueKind == JsonValueKind.Number)
                        limits.Add(new Limit(label, pct.GetDouble(), Time(w, "resets_at")));
            }
            // Extra usage, where the account has it turned on: the share of its monthly budget spent.
            if (u.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object &&
                extra.TryGetProperty("is_enabled", out var on) && on.ValueKind == JsonValueKind.True &&
                extra.TryGetProperty("utilization", out var spent) && spent.ValueKind == JsonValueKind.Number &&
                limits.All(l => l.Label != ExtraLabel))
                limits.Add(new Limit(ExtraLabel, spent.GetDouble(), null));
            return limits.Count == 0 ? null
                : new Answer(limits, DateTimeOffset.FromUnixTimeMilliseconds((long)at.GetDouble()), org);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True for the two limits every source reports; the rest come only from this report.</summary>
    public static bool IsPlanWindow(string label) => label is "5-hour" or "Weekly";

    static string? ScopeName(JsonElement limit)
    {
        if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;
        foreach (var part in new[] { "model", "surface" })
            if (scope.TryGetProperty(part, out var p) && p.ValueKind == JsonValueKind.Object &&
                Text(p, "display_name") is { } name && name.Trim().Length > 0)
                return name.Trim();
        return null;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? Time(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    // ---- running Claude Code ---------------------------------------------------------------

    /// <summary>
    /// The Claude Code this account's Claude app carries (newest version first), then the usual
    /// places a terminal install lives. A meter started from the Dock has almost no PATH.
    /// </summary>
    string? FindClaude()
    {
        string exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        string appData = OperatingSystem.IsMacOS() ? Path.Combine(Paths.Home, "Library", "Application Support")
            : OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Paths.XdgConfig;
        string bundled = Path.Combine(appData, _appFolder, "claude-code");
        if (Directory.Exists(bundled))
        {
            foreach (var version in Directory.EnumerateDirectories(bundled).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                try
                {
                    var found = Directory.EnumerateFiles(version, exe, SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null) return found;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        var places = new List<string>
        {
            Path.Combine(Paths.Home, ".local", "bin", exe),
            Path.Combine(Paths.Home, ".claude", "local", exe),
        };
        if (!OperatingSystem.IsWindows()) { places.Add("/opt/homebrew/bin/claude"); places.Add("/usr/local/bin/claude"); }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Trim('"').Length > 0) places.Add(Path.Combine(dir.Trim('"'), exe));
        return places.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs "claude -p /usage" once. True when Claude Code answered it as its own command.</summary>
    bool Ask(string claude)
    {
        var psi = new ProcessStartInfo(claude)
        {
            WorkingDirectory = Directory.Exists(Paths.Data) ? Paths.Data : Paths.Home,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Nothing kept as a session, none of the user's MCP servers or hooks started for it.
        foreach (var arg in new[] { "-p", "/usage", "--output-format", "json", "--no-session-persistence",
                     "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}" })
            psi.ArgumentList.Add(arg);

        // Whatever Ringside was started from must not choose the account: only the folder does.
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        if (_configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = _configDir;

        using var p = Process.Start(psi);
        if (p is null) return false;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)RunLimit.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            return false;
        }
        bool answered = AnsweredLocally(output.Result);
        // A Claude Code that does not know the command would hand "/usage" to a model as a prompt.
        // Once is the most that may happen: stop asking until Ringside starts again.
        if (!answered && p.ExitCode == 0)
            lock (_gate) _off = true;
        return answered;
    }

    /// <summary>True when the run was Claude Code's own /usage command: no model turn, no cost.</summary>
    public static bool AnsweredLocally(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        int start = output.IndexOf('{'), end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && Text(r, "local_command") == "usage" &&
                   r.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number && turns.GetDouble() == 0;
        }
        catch (JsonException) { return false; }
    }
}
