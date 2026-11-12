using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Trials;

public sealed record TrialMailMessage(string Recipient, string Subject, string Body);

/// <summary>A transport boundary so automated checks never need SMTP or a real recipient.</summary>
public interface ITrialMailTransport
{
    Task DeliverAsync(TrialMailMessage message, CancellationToken cancellationToken);
}

/// <summary>Plain-text transactional messages; user content is never inserted into HTML.</summary>
public sealed class SmtpTrialEmailSender(
    IOptions<SmtpOptions> options,
    IOptions<SiteOptions> site,
    IStringLocalizer<CheckoutText> text,
    ITrialMailTransport transport,
    IOptions<TrialOptions>? trials = null,
    IOptions<AuthentikOptions>? panelSignIn = null) : ITrialEmailSender
{
    public bool IsConfigured => options.Value.IsConfigured;

    public Task SendVerificationAsync(string email, string displayName, string verificationUrl,
        string locale, CancellationToken cancellationToken) =>
        SendAsync(email, locale, "Trial.Mail.VerifySubject", "Trial.Mail.VerifyBody", cancellationToken,
            displayName, verificationUrl, site.Value.ContactEmail, Settings.DurationHours);

    // With SSO the panel has no password of its own, so the message explains where to choose it.
    public Task SendReadyAsync(string email, string panelUrl, DateTimeOffset expiresAt,
        string locale, CancellationToken cancellationToken) =>
        SendAsync(email, locale, "Trial.Mail.ReadySubject",
            panelSignIn?.Value.IsConfigured == true ? "Trial.Mail.ReadyBodySso" : "Trial.Mail.ReadyBody", cancellationToken,
            panelUrl, FormatUtc(expiresAt), TrialPageUrl, site.Value.ContactEmail,
            Settings.DurationHours, Settings.RetentionHours / 24m);

    public Task SendExpiredAsync(string email, DateTimeOffset deleteAfter, string locale, CancellationToken cancellationToken) =>
        SendAsync(email, locale, "Trial.Mail.ExpiredSubject", "Trial.Mail.ExpiredBody", cancellationToken,
            FormatUtc(deleteAfter), TrialPageUrl, site.Value.ContactEmail);

    public Task SendDeletedAsync(string email, string locale, CancellationToken cancellationToken) =>
        SendAsync(email, locale, "Trial.Mail.DeletedSubject", "Trial.Mail.DeletedBody", cancellationToken,
            TrialPageUrl, site.Value.ContactEmail, Settings.RetentionHours / 24m);

    private string TrialPageUrl => $"{site.Value.BaseUrl.TrimEnd('/')}/trial";
    private TrialOptions Settings => trials?.Value ?? new TrialOptions();

    private static string FormatUtc(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private async Task SendAsync(string email, string locale, string subjectKey, string bodyKey,
        CancellationToken cancellationToken, params object[] arguments)
    {
        if (!IsConfigured)
        {
            throw new TrialEmailDeliveryException();
        }

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(SupportedCultures.ResolveOrDefault(locale));

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            var message = new TrialMailMessage(email, text[subjectKey].Value, text[bodyKey, arguments].Value);
            await transport.DeliverAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}

/// <summary>Never exposes relay credentials, recipients or verification tokens in an error.</summary>
public sealed class TrialEmailDeliveryException : Exception
{
    public TrialEmailDeliveryException() : base("Trial email delivery is temporarily unavailable.") { }
}

public sealed class SmtpTrialMailTransport(IOptions<SmtpOptions> options) : ITrialMailTransport
{
    public async Task DeliverAsync(TrialMailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured) throw new TrialEmailDeliveryException();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.DeliveryTimeoutSeconds));

        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(settings.FromAddress, settings.FromName, Encoding.UTF8),
                Subject = message.Subject,
                Body = message.Body,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false
            };
            mail.To.Add(new MailAddress(message.Recipient));

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = settings.DeliveryTimeoutSeconds * 1000
            };

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            }

            await client.SendMailAsync(mail, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TrialEmailDeliveryException();
        }
        catch (Exception exception) when (exception is SmtpException or FormatException)
        {
            // The raw SMTP response may repeat a recipient or a relay detail. It stays private.
            throw new TrialEmailDeliveryException();
        }
    }
}

public static class TrialEmailRegistration
{
    public static IServiceCollection AddTrialEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
        services.AddSingleton<ITrialMailTransport, SmtpTrialMailTransport>();
        services.AddScoped<ITrialEmailSender, SmtpTrialEmailSender>();
        return services;
    }
}
