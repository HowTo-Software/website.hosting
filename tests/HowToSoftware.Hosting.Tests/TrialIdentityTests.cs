using HowToSoftware.Hosting.Services.Trials;

namespace HowToSoftware.Hosting.Tests;

public sealed class TrialIdentityTests
{
    [Theory]
    [InlineData("Malaio", "24 Teste Malaio")]
    [InlineData("  Ana   Silva  ", "24 Teste Ana Silva")]
    [InlineData(null, "24 Teste Jogador")]
    [InlineData("\u0000bad", "24 Teste Jogador")]
    [InlineData("\u202Ebad", "24 Teste Jogador")]
    public void TrialNamesUseOnlyNormalizedDisplayNames(string? input, string expected) =>
        Assert.Equal(expected, TrialServerIdentity.ServerName(input));

    [Fact]
    public void NamesAreBoundedWithoutSplittingUnicodeCharacters()
    {
        var normalized = TrialServerIdentity.NormalizeDisplayName(new string('x', 79) + "😀");
        Assert.Equal(79, normalized!.Length);
        Assert.True(TrialServerIdentity.ServerName(new string('x', 200)).Length <= 89);
    }

    [Fact]
    public void EachZomboidCreationGetsAnIndependentEggCompatiblePassword()
    {
        var first = new Dictionary<string, string> { ["ADMIN_PASSWORD"] = "shared-override" };
        var second = new Dictionary<string, string>();
        TrialServerIdentity.SetProjectZomboidPassword("project-zomboid", first);
        TrialServerIdentity.SetProjectZomboidPassword("project-zomboid", second);
        Assert.Equal(32, first["ADMIN_PASSWORD"].Length);
        Assert.All(first["ADMIN_PASSWORD"], c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.NotEqual(first["ADMIN_PASSWORD"], second["ADMIN_PASSWORD"]);
    }

    [Fact]
    public void MinecraftEnvironmentIsNotGivenAZomboidPassword()
    {
        var environment = new Dictionary<string, string> { ["OTHER"] = "keep" };
        TrialServerIdentity.SetProjectZomboidPassword("minecraft", environment);
        Assert.Equal("keep", environment["OTHER"]);
        Assert.False(environment.ContainsKey("ADMIN_PASSWORD"));
    }
}
