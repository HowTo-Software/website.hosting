using System.Globalization;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Subscriptions;

public sealed record CustomerInvoice(string Number, long Amount, string Currency, string Status, DateTime Created, string? Url, string? Pdf);
public sealed record CustomerService(ManagedService Service, string? Status, DateTime? Renewal,
    SubscriptionPlanChange? Change, IReadOnlyList<ManagedPlan> Plans, string? PaymentUrl = null);
public sealed record CustomerRenewal(Guid ServiceId, string Plan, long Amount, string Currency, DateTime? Date);
public sealed record CustomerWorkspaceView(IReadOnlyList<CustomerService> Services, IReadOnlyList<CustomerInvoice> Invoices,
    IReadOnlyList<CustomerRenewal> Upcoming, IReadOnlyList<CustomerSupportRecord> Tickets, bool BillingUnavailable = false)
{
    public static CustomerWorkspaceView Empty => new([], [], [], []);
}
public interface ICustomerWorkspace
{
    Task<CustomerWorkspaceView> LoadAsync(string owner, string tab, CancellationToken ct);
    Task<bool> SupportAsync(string owner, string subject, string message, string locale, CancellationToken ct);
    Task DeliverPendingAsync(CancellationToken ct);
}

public sealed class CustomerWorkspace(ISubscriptionStore store, ISubscriptionGateway stripe,
    IDbContextFactory<CommerceDbContext> factory, ITrialMailTransport mail,
    IOptions<SiteOptions> site, TimeProvider clock, ILogger<CustomerWorkspace> logger) : ICustomerWorkspace
{
    public async Task<CustomerWorkspaceView> LoadAsync(string owner, string tab, CancellationToken ct)
    {
        var services = new List<CustomerService>();
        var invoices = new List<CustomerInvoice>();
        var upcoming = new List<CustomerRenewal>();
        var unavailable = false;
        foreach (var service in await store.ListOwnedAsync(owner, ct))
        {
            string? status = null;
            DateTime? renewal = null;
            string? paymentUrl = null;
            var change = await store.ActiveChangeAsync(service.Id, ct);
            try
            {
                if (stripe.IsConfigured)
                {
                    var subscription = await stripe.ReadSubscriptionAsync(service.SubscriptionId, ct);
                    if (subscription.CustomerId != service.CustomerId || subscription.Id != service.SubscriptionId)
                        throw new InvalidOperationException("Billing ownership mismatch.");
                    status = subscription.Status;
                    renewal = subscription.Items?.Data.SingleOrDefault()?.CurrentPeriodEnd;
                    if (tab == "plans" && change is { State: PlanChangeState.AwaitingPayment, StripeInvoiceId: { } invoiceId })
                    {
                        var payment = await stripe.ReadInvoiceAsync(invoiceId, ct);
                        if (payment.CustomerId == service.CustomerId && payment.Parent?.SubscriptionDetails?.SubscriptionId == service.SubscriptionId && payment.Status == "open")
                            paymentUrl = SafeStripeLink(payment.HostedInvoiceUrl);
                    }
                    if (tab is "overview" or "invoices")
                    {
                        foreach (var invoice in (await stripe.InvoicesAsync(service.CustomerId, service.SubscriptionId, ct))
                            .Where(invoice => invoice.CustomerId == service.CustomerId && invoice.Parent?.SubscriptionDetails?.SubscriptionId == service.SubscriptionId))
                        {
                            invoices.Add(new(invoice.Number ?? invoice.Id, invoice.Status == "paid" ? invoice.AmountPaid : invoice.AmountDue,
                                invoice.Currency, invoice.Status, invoice.Created, SafeStripeLink(invoice.HostedInvoiceUrl), SafeStripeLink(invoice.InvoicePdf)));
                        }
                        if (subscription.Status == "active" && !subscription.CancelAtPeriodEnd)
                        {
                            var preview = await stripe.UpcomingAsync(service.SubscriptionId, ct);
                            if (preview.CustomerId != service.CustomerId) throw new InvalidOperationException("Invoice ownership mismatch.");
                            upcoming.Add(new(service.Id, service.Plan.Name, preview.AmountDue, preview.Currency, renewal));
                        }
                    }
                }
                else unavailable = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                unavailable = true;
                logger.LogWarning("Customer billing data temporarily unavailable ({FailureClass}).", e.GetType().Name);
            }
            services.Add(new(service, status, renewal, change,
                tab == "plans" ? await store.ListPlansAsync(service.GameId, service.BillingPeriod, service.Currency, ct) : [], paymentUrl));
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var tickets = await db.CustomerSupport.AsNoTracking().Where(x => x.NormalizedEmail == owner)
            .OrderByDescending(x => x.CreatedAt).Take(25).ToListAsync(ct);
        return new(services, invoices.Distinct().OrderByDescending(x => x.Created).ToList(), upcoming, tickets, unavailable);
    }

    public async Task<bool> SupportAsync(string owner, string subject, string message, string locale, CancellationToken ct)
    {
        subject = subject.Trim(); message = message.Trim();
        if (subject.Length is < 3 or > 160 || message.Length is < 10 or > 4000
            || subject.Any(char.IsControl) || message.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        // Bound volume in addition to per-IP endpoint limits.
        if (await db.CustomerSupport.CountAsync(x => x.NormalizedEmail == owner && x.CreatedAt > now.AddHours(-1), ct) >= 5) return false;
        db.CustomerSupport.Add(new()
        {
            Id = Guid.NewGuid(),
            NormalizedEmail = owner,
            Subject = subject,
            Message = message,
            Locale = locale == "pt-BR" ? "pt-BR" : "en",
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return true; // A saved request survives relay outages and is delivered by the worker.
    }

    public async Task DeliverPendingAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var ticket in await db.CustomerSupport.Where(x => x.NotifiedAt == null).OrderBy(x => x.CreatedAt).Take(10).ToListAsync(ct))
        {
            await mail.DeliverAsync(new(site.Value.ContactEmail, $"HTS support request {ticket.Id:N}",
                $"Reference: {ticket.Id:N}\nCustomer: {ticket.NormalizedEmail}\nLanguage: {ticket.Locale}\nSubject: {ticket.Subject}\n\n{ticket.Message}"), ct);
            ticket.NotifiedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public static string? SafeStripeLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0 && url.IsDefaultPort
        && url.Host is "invoice.stripe.com" or "billing.stripe.com" or "pay.stripe.com" ? url.AbsoluteUri : null;
}

public sealed class UnavailableCustomerWorkspace : ICustomerWorkspace
{
    public Task<CustomerWorkspaceView> LoadAsync(string owner, string tab, CancellationToken ct) => Task.FromResult(CustomerWorkspaceView.Empty);
    public Task<bool> SupportAsync(string owner, string subject, string message, string locale, CancellationToken ct) => Task.FromResult(false);
    public Task DeliverPendingAsync(CancellationToken ct) => Task.CompletedTask;
}
