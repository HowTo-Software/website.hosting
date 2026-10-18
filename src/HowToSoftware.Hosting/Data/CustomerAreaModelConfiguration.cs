using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HowToSoftware.Hosting.Data;

internal static class CustomerAreaModelConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<CustomerAccessRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.ChallengeHash).HasMaxLength(64);
            e.Property(x => x.SessionHash).HasMaxLength(64);
            e.HasIndex(x => x.ChallengeHash).IsUnique();
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.HasIndex(x => x.SessionHash).IsUnique().HasFilter("[session_hash] IS NOT NULL");
            e.HasIndex(x => new { x.NormalizedEmail, x.CreatedAt });
        });
        model.Entity<CustomerSupportRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.Subject).HasMaxLength(160);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.Locale).HasMaxLength(16);
            e.HasIndex(x => new { x.NormalizedEmail, x.CreatedAt });
            e.HasIndex(x => x.NotifiedAt);
        });
        model.Entity<SubscriptionPlanChange>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.HostingServiceId).IsUnique().HasFilter("[active] = 1");
            e.HasIndex(x => new { x.State, x.Active });
            e.HasIndex(x => x.StripeSubscriptionId);
            e.Property(x => x.LeaseToken).IsConcurrencyToken();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            foreach (var field in new[] { "StripeSubscriptionId", "StripeCustomerId", "SubscriptionItemId", "SourcePriceId", "TargetPriceId", "BaselineInvoiceId", "StripeInvoiceId", "StripeScheduleId" })
                e.Property<string>(field).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.BillingPeriod).HasMaxLength(16);
            e.Property(x => x.FailureCode).HasMaxLength(80);
            e.HasOne<HostingServiceRecord>().WithMany().HasForeignKey(x => x.HostingServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CommerceHostingPlan>().WithMany().HasForeignKey(x => x.TargetPlanId).OnDelete(DeleteBehavior.Restrict);
        });
        // New tables use UTC ticks for ordered, precision-preserving comparisons on SQL Server
        // and the hermetic SQLite test provider. Existing columns and data are untouched.
        var ticks = new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        foreach (var type in new[] { typeof(CustomerAccessRecord), typeof(CustomerSupportRecord), typeof(SubscriptionPlanChange) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(ticks);
    }
}
