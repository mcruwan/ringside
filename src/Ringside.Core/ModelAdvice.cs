using System;
using System.Globalization;
using System.Linq;

namespace Ringside;

public enum ModelCall { UseNow, OnPace, Done }

/// <summary>
/// Whether to use a model with its own weekly allowance (for one, "Fable") right now.
///
/// Such a model may take a set share of the week, say half. What it uses counts twice: against
/// its own allowance and against the weekly limit for all models. So the allowance is lost in
/// two ways: the week ends with it unused, or other models fill the week first and leave it no
/// room. This says, from the two numbers and the clock, when using the model now avoids that.
/// </summary>
/// <param name="Short">Two or three words for the meter: "Use Fable now".</param>
/// <param name="Reason">One or two sentences for the tooltip.</param>
/// <param name="Usable">How much of the week can still go to the model, in percent of the week.</param>
public sealed record ModelAdvice(ModelCall Call, string Short, string Reason, double Usable)
{
    static readonly TimeSpan Week = TimeSpan.FromDays(7);
    /// <summary>How far behind the clock, in percent of its allowance, before it says so.</summary>
    public const double Grace = 5;

    public static ModelAdvice? For(Snapshot snap, LimitWindow model, DateTimeOffset now)
    {
        if (model.ShareOfWeek is not { } share || share <= 0 || model.Lapsed) return null;
        var weekly = snap.Windows.FirstOrDefault(w => w.Label == "Weekly" && !w.Lapsed);
        string name = model.Label;
        double used = Math.Clamp(model.Percent, 0, 100);
        double modelLeft = share * (100 - used) / 100;
        double weekLeft = weekly is null ? 100 : 100 - Math.Clamp(weekly.Percent, 0, 100);
        double usable = Math.Max(0, Math.Min(modelLeft, weekLeft));
        var reset = model.ResetsAt ?? weekly?.ResetsAt;
        string until = reset is { } r ? " until " + When(r) : "";
        string before = reset is { } b ? " before " + When(b) : "";

        if (used >= 100)
            return new(ModelCall.Done, $"{name} used up", $"{name} is used up for this week{until}. Use another model.", 0);
        if (weekLeft <= 0)
            return new(ModelCall.Done, "Week used up", $"The weekly limit is used up, so {name} is out too{until}.", 0);

        // The week has no more room than the model's allowance: all that is left can go to it,
        // and whatever another model takes now is taken from it.
        if (weekly is not null && weekLeft <= modelLeft)
            return new(ModelCall.UseNow, $"Use {name} now",
                $"Only {N(weekLeft)}% of the week is left and {name} could take all of it{before}. " +
                $"What another model uses now is lost to {name}.", usable);

        // Behind the clock: less of the allowance used than of the week gone.
        if (reset is { } at)
        {
            double gone = Math.Clamp(1 - (at - now) / Week, 0, 1) * 100;
            if (used < gone - Grace)
                return new(ModelCall.UseNow, $"Use {name} now",
                    $"{name} is behind the week: {N(used)}% of it used, {N(gone)}% of the week gone. " +
                    $"Up to {N(usable)}% of the week can still go to {name}{before}.", usable);
        }

        return new(ModelCall.OnPace, "On pace",
            $"{name} is keeping up with the week. Up to {N(usable)}% of the week can still go to it{before}.", usable);
    }

    static string N(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    static string When(DateTimeOffset t) => t.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
}
