using System.Globalization;
using HowToSoftware.Hosting.Services.Trials;

namespace HowToSoftware.Hosting.Tests;

public sealed class TrialTimeTests
{
    [Theory]
    [InlineData("2026-03-08T06:59:00Z", "Mar 8, 2026 01:59 -05:00")]
    [InlineData("2026-03-08T07:00:00Z", "Mar 8, 2026 03:00 -04:00")]
    [InlineData("2026-11-01T05:59:00Z", "Nov 1, 2026 01:59 -04:00")]
    [InlineData("2026-11-01T06:00:00Z", "Nov 1, 2026 01:00 -05:00")]
    public void EasternTimeUsesTheCorrectDaylightSavingTransition(string utc, string expected) =>
        Assert.Equal(expected, TrialTime.Format(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture),
            "America/New_York", CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void BrasiliaKeepsItsOwnDateAndOffsetAtTheEasternTransition() =>
        Assert.Equal("08/03/2026 04:00 -03:00", TrialTime.Format(
            DateTimeOffset.Parse("2026-03-08T07:00:00Z", CultureInfo.InvariantCulture),
            "America/Sao_Paulo", CultureInfo.GetCultureInfo("pt-BR")));
}
