using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HowToSoftware.Hosting.Data;

internal static class ExternalIdentityModelConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<CustomerExternalIdentity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Issuer).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Subject).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasIndex(x => new { x.Provider, x.Issuer, x.Subject }).IsUnique();
            e.HasIndex(x => new { x.CustomerAccessId, x.Provider }).IsUnique();
            e.HasOne<CustomerAccessRecord>().WithMany().HasForeignKey(x => x.CustomerAccessId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ExternalIdentityFlow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Purpose).HasMaxLength(16);
            e.Property(x => x.OwnerSessionHash).HasMaxLength(64);
            e.Property(x => x.BrowserSecretHash).HasMaxLength(64);
            e.Property(x => x.Issuer).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Subject).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<CustomerAccessRecord>().WithMany().HasForeignKey(x => x.CustomerAccessId).OnDelete(DeleteBehavior.Restrict);
        });
        var ticks = new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        foreach (var type in new[] { typeof(CustomerExternalIdentity), typeof(ExternalIdentityFlow) })
            foreach (var property in model.Entity(type).Metadata.GetProperties()
                .Where(property => property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(ticks);
    }
}
