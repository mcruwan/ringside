using System;
using System.Globalization;
using System.Linq;

namespace Ringside;

public enum ModelCall
{
    /// <summary>More is left than will be used before the reset: use it freely.</summary>
    Plenty,
    /// <summary>The week has no more room than the model's allowance: everything left should go to it.</summary>
    Only,
    /// <summary>Keeping pace with the week: it lasts about until the reset.</summary>
    OnPace,
    /// <summary>Nothing left, of the model's allowance or of the week.</summary>
    Done,
}

/// <summary>
/// How much of a model with its own weekly allowance (for one, "Fable") can still be used, and
/// what to do about it.
///
/// Such a model may take a set share of the week, say half. Its own percent is measured against
/// that share, and what it uses counts against the weekly limit for all models too. The share
/// is a ceiling, not a reserve: it goes unused when the week ends first, and it shrinks when
/// other models fill the week and leave it no room. So the number worth showing is what can
/// still be reached, the smaller of what is left of the share and of the week.
/// </summary>
/// <param name="Short">A few words for the meter, read after the amount: "use Fable freely".</param>
/// <param name="Reason">One or two sentences for the tooltip.</param>
/// <param name="LeftToUse">What can still be used, in percent of the model's allowance.</param>
/// <param name="Lost">What is left of the allowance but out of reach, in percent of it: the week has no room for it.</param>
/// <param name="Usable">What can still be used, in percent of the week.</param>
public sealed record ModelAdvice(ModelCall Call, string Short, string Reason, double LeftToUse, double Lost, double Usable)
{
    static readonly TimeSpan Week = TimeSpan.FromDays(7);
    /// <summary>How far behind the clock, in percent of its allowance, before there is "plenty".</summary>
    public const double Grace = 5;

    /// <summary>The line under the model's row: "26% left · Fable only from here".</summary>
    public string Line => Call == ModelCall.Done ? Short : $"{N(LeftToUse)}% left · {Short}";

    public static ModelAdvice? For(Snapshot snap, LimitWindow model, DateTimeOffset now)
    {
        if (model.ShareOfWeek is not { } share || share <= 0 || model.Lapsed) return null;
        var weekly = snap.Windows.FirstOrDefault(w => w.Label == "Weekly" && !w.Lapsed);
        string name = model.Label;
        double used = Math.Clamp(model.Percent, 0, 100);
        double modelLeft = share * (100 - used) / 100;                 // in percent of the week
        double weekLeft = weekly is null ? 100 : 100 - Math.Clamp(weekly.Percent, 0, 100);
        double usable = Math.Max(0, Math.Min(modelLeft, weekLeft));
        double leftToUse = usable / share * 100;                       // in percent of the allowance
        double lost = Math.Max(0, (modelLeft - usable) / share * 100);
        var reset = model.ResetsAt ?? weekly?.ResetsAt;
        string until = reset is { } r ? " until " + When(r) : "";
        string before = reset is { } b ? " before " + When(b) : "";
        string gap = lost >= 1 ? $" {N(lost)}% of {name} is out of reach this week: other models have filled the week." : "";

        if (used >= 100)
            return new(ModelCall.Done, $"{name} used up{until}", $"{name} is used up for this week. Use another model{until}.", 0, 0, 0);
        if (weekLeft <= 0)
            return new(ModelCall.Done, $"Week used up{until}", $"The weekly limit is used up, so {name} is out too{until}.{gap}", 0, lost, 0);

        // The week has no more room than the model's allowance: all that is left can go to it,
        // and whatever another model takes now is taken from it.
        if (weekly is not null && weekLeft <= modelLeft)
            return new(ModelCall.Only, $"{name} only from here",
                $"Only {N(weekLeft)}% of the week is left, and that is all {name} can still use{before}. " +
                $"Anything another model uses now takes {name}'s place.{gap}", leftToUse, lost, usable);

        if (reset is { } at)
        {
            double gone = Math.Clamp(1 - (at - now) / Week, 0, 1) * 100;
            // Behind the clock: less of the allowance used than of the week gone.
            if (used < gone - Grace)
                return new(ModelCall.Plenty, $"use {name} freely",
                    $"{name} is behind the week: {N(used)}% used with {N(gone)}% of the week gone. " +
                    $"More is left than you are likely to use{before}, and what is left then is gone.", leftToUse, lost, usable);
            return new(ModelCall.OnPace, "on pace, for hard work",
                $"{name} is keeping pace with the week: {N(used)}% used with {N(gone)}% of the week gone. " +
                $"At this rate it lasts about until the reset, so spend it on the hard work.", leftToUse, lost, usable);
        }

        return new(ModelCall.OnPace, "on pace, for hard work",
            $"{N(leftToUse)}% of {name} can still be used this week.", leftToUse, lost, usable);
    }

    static string N(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    // To the nearest minute: a reset reported a second short of the hour reads as the hour.
    static string When(DateTimeOffset t) => t.AddSeconds(30).ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
}
