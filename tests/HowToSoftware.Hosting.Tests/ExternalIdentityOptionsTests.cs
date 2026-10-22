using HowToSoftware.Hosting.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class ExternalIdentityOptionsTests
{
    [Fact]
    public async Task DefaultsDoNotRegisterRemoteHandlersAndRequireNoCredentialsOrDatabase()
    {
        var config = new ConfigurationBuilder().Build();
        await using var services = new ServiceCollection().AddLogging().AddExternalIdentities(config, database: false).BuildServiceProvider();
        var settings = services.GetRequiredService<IOptions<ExternalIdentityOptions>>().Value;
        Assert.False(settings.ActiveDirectory.Enabled); Assert.False(settings.ActiveDirectory.LoginEnabled);
        Assert.False(settings.Discord.Enabled); Assert.False(settings.Discord.LoginEnabled);
        Assert.Equal("Unspecified", settings.ActiveDirectory.Protocol);
        Assert.Empty(await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync());
    }
    [Fact]
    public void OnPremisesAdapterCannotBeEnabledWhileItsTransportIsUnresolved()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalIdentity:ActiveDirectory:Enabled"] = "true",
            ["ExternalIdentity:ActiveDirectory:Protocol"] = "AdfsOidc",
            ["CustomerArea:Enabled"] = "true", ["Smtp:Host"] = "smtp.invalid",
            ["Smtp:FromAddress"] = "sender@example.test", ["Site:BaseUrl"] = "https://host.example.test"
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddExternalIdentities(config, database: true).BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ExternalIdentityOptions>>().Value);
        Assert.Contains(error.Failures, x => x.Contains("intentionally pending", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("http://host.example.test", true)]
    [InlineData("https://host.example.test/unexpected-base-path", true)]
    [InlineData("https://host.example.test", false)]
    public void FutureDiscordActivationRequiresTheCanonicalHttpsOrigin(string origin, bool invalid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalIdentity:Discord:Enabled"] = "true", ["CustomerArea:Enabled"] = "true",
            ["ExternalIdentity:Discord:ClientId"] = "123456789012345678",
            ["ExternalIdentity:Discord:ClientSecret"] = "FAKE_DISCORD_TEST_SECRET_NOT_REAL",
            ["Smtp:Host"] = "smtp.invalid", ["Smtp:FromAddress"] = "sender@example.test", ["Site:BaseUrl"] = origin
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddExternalIdentities(config, database: true).BuildServiceProvider();
        if (invalid) Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ExternalIdentityOptions>>().Value);
        else Assert.True(services.GetRequiredService<IOptions<ExternalIdentityOptions>>().Value.Discord.Enabled);
    }
    [Fact]
    public void OptionsNeverPrintClientSecrets()
    {
        var settings = new ExternalIdentityOptions { Discord = new() { ClientSecret = "DO_NOT_PRINT" } };
        Assert.DoesNotContain("DO_NOT_PRINT", settings.ToString());
        Assert.DoesNotContain("DO_NOT_PRINT", settings.Discord.ToString());
    }
}
