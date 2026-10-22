using System.Globalization;

namespace HowToSoftware.Hosting.Services.Trials;

public static class TrialTime
{
    public static readonly string[] UnitedStatesZones =
        ["America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles"];

    public static string Format(DateTimeOffset utc, string zone, CultureInfo culture)
    {
        if (zone != "America/Sao_Paulo" && !UnitedStatesZones.Contains(zone, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(zone));
        var converted = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(zone));
        return converted.ToString(culture.Name == "pt-BR" ? "dd/MM/yyyy HH:mm zzz" : "MMM d, yyyy HH:mm zzz", culture);
    }
}
