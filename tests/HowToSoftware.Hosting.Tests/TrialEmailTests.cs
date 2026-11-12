using System.Globalization;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services.Identity;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class TrialEmailTests
{
    private static SmtpOptions ConfiguredSmtp() => new()
    {
        Host = "smtp.example.test", FromAddress = "hosting@example.test", Username = "relay-user",
        Password = "unit-test-only", EnableSsl = true
    };

    private static SmtpTrialEmailSender Sender(RecordingTransport transport, SmtpOptions? smtp = null) => new(
        Options.Create(smtp ?? ConfiguredSmtp()),
        Options.Create(new SiteOptions { BaseUrl = "https://hosting.example.test", ContactEmail = "help@example.test" }),
        TestLocalizer.For<CheckoutText>(), transport);

    [Fact]
    public async Task MissingSmtpCannotPretendThatAVerificationEmailWasSent()
    {
        var transport = new RecordingTransport();
        var sender = Sender(transport, new SmtpOptions());
        Assert.False(sender.IsConfigured);
        await Assert.ThrowsAsync<TrialEmailDeliveryException>(() => sender.SendVerificationAsync(
            "player@example.test", "Player", "https://hosting.example.test/trial?verify=unit-token", "en", default));
        Assert.Empty(transport.Messages);
    }

    [Theory]
    [InlineData("en", "Confirm your HTS Hosting email", "Opening the link does not create a server.")]
    [InlineData("pt-BR", "Confirme seu email na HTS Hosting", "Só abrir o link não cria um servidor.")]
    public async Task VerificationMessagesExplainTheConfirmationBoundaryInTheRequestedLocale(
        string locale, string expectedSubject, string expectedBoundary)
    {
        var transport = new RecordingTransport();
        var link = "https://hosting.example.test/trial?verify=unit-test-token";
        await Sender(transport).SendVerificationAsync("player@example.test", "Player", link, locale, default);
        var message = Assert.Single(transport.Messages);
        Assert.Equal(expectedSubject, message.Subject);
        Assert.Contains(expectedBoundary, message.Body);
        Assert.Contains(link, message.Body);
        Assert.DoesNotContain("unit-test-token", message.Subject);
        Assert.Equal("player@example.test", message.Recipient);
        Assert.DoesNotContain("unit-test-only", message.Body);
    }

    [Fact]
    public async Task TheWorkerLocaleDoesNotOverwriteTheCallingExecutionContext()
    {
        using var culture = new CultureScope("en");
        var transport = new RecordingTransport();
        await Sender(transport).SendDeletedAsync("player@example.test", "pt-BR", default);
        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en", CultureInfo.CurrentCulture.Name);
        Assert.Contains("prazo de retenção", Assert.Single(transport.Messages).Subject);
    }

    [Theory]
    [InlineData("en", "same server and save", "3 days")]
    [InlineData("pt-BR", "mesmo servidor e save", "3 dias")]
    public async Task ReadyAndExpiryMessagesStateTheRetentionDeadlineAndSaveContinuity(
        string locale, string sameSave, string retention)
    {
        var transport = new RecordingTransport();
        var sender = Sender(transport);
        var expiry = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
        await sender.SendReadyAsync("player@example.test", "https://panel.example.test/auth/login", expiry, locale, default);
        await sender.SendExpiredAsync("player@example.test", expiry.AddDays(3), locale, default);
        Assert.Equal(2, transport.Messages.Count);
        Assert.Contains(retention, transport.Messages[0].Body);
        Assert.Contains(sameSave, transport.Messages[0].Body);
        Assert.Contains("2026-10-06 15:00 UTC", transport.Messages[0].Body);
        Assert.Contains("2026-10-09 15:00 UTC", transport.Messages[1].Body);
        Assert.Contains("https://hosting.example.test/trial", transport.Messages[1].Body);
    }

    [Theory]
    [InlineData("en", "Sign in with HowTo.Software SSO", "Set panel password")]
    [InlineData("pt-BR", "Entrar com HowTo.Software SSO", "Definir senha do painel")]
    public async Task WithPanelSsoTheReadyMessageSaysWhereToChooseThePassword(string locale, string signIn, string action)
    {
        var transport = new RecordingTransport();
        var sender = new SmtpTrialEmailSender(Options.Create(ConfiguredSmtp()),
            Options.Create(new SiteOptions { BaseUrl = "https://hosting.example.test", ContactEmail = "help@example.test" }),
            TestLocalizer.For<CheckoutText>(), transport,
            panelSignIn: Options.Create(new AuthentikOptions { BaseUrl = "https://auth.example.test", ApiToken = "unit-test-only" }));
        await sender.SendReadyAsync("player@example.test", "https://panel.example.test",
            new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero), locale, default);
        var body = Assert.Single(transport.Messages).Body;
        Assert.Contains(signIn, body);
        Assert.Contains(action, body);
        Assert.Contains("https://hosting.example.test/trial", body);
        Assert.DoesNotContain("setup email", body);
        Assert.DoesNotContain("unit-test-only", body);
    }

    [Fact]
    public void TlsIsRequiredForSmtpOutsideDevelopment()
    {
        var settings = ConfiguredSmtp();
        settings.EnableSsl = false;
        Assert.True(new SmtpOptionsValidator(new EnvironmentStub("Production")).Validate(null, settings).Failed);
        Assert.True(new SmtpOptionsValidator(new EnvironmentStub("Development")).Validate(null, settings).Succeeded);
    }

    [Fact]
    public void UnconfiguredSmtpIsValidAndCredentialPairErrorsDoNotEchoTheCredential()
    {
        var validator = new SmtpOptionsValidator(new EnvironmentStub("Production"));
        Assert.True(validator.Validate(null, new SmtpOptions()).Succeeded);
        var settings = ConfiguredSmtp();
        settings.Username = string.Empty;
        var result = validator.Validate(null, settings);
        Assert.True(result.Failed);
        Assert.DoesNotContain(settings.Password, string.Join(" ", result.Failures ?? []));
    }

    private sealed class RecordingTransport : ITrialMailTransport
    {
        public List<TrialMailMessage> Messages { get; } = [];
        public Task DeliverAsync(TrialMailMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class EnvironmentStub(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "HowToSoftware.Hosting";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
