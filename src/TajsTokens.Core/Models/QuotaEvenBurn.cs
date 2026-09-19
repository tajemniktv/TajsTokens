namespace TajsTokens.Core.Models;

/// <summary>A descriptive comparison at capture time, not a prediction of future consumption.</summary>
public sealed record QuotaEvenBurn(double UsedPercent, double ElapsedPercent)
{
    public double ExcessPercentagePoints => UsedPercent - ElapsedPercent;

    public static QuotaEvenBurn? FromSnapshot(QuotaSnapshot snapshot)
    {
        if (snapshot.UsedPercent is not double used || !double.IsFinite(used) || used < 0 || used > 100 ||
            snapshot.WindowMinutes is not int minutes || minutes <= 0 || snapshot.ResetsAtUtc is not { } reset)
            return null;

        // Infer the start from this observation's own reset and duration, never another lane/history.
        var remainingMinutes = (reset - snapshot.CapturedAtUtc).TotalMinutes;
        if (remainingMinutes <= 0 || remainingMinutes > minutes)
            return null;

        return new QuotaEvenBurn(used, 100d * (1d - remainingMinutes / minutes));
    }
}
