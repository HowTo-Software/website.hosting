namespace HowToSoftware.Hosting.Models.Commerce;

public sealed class CustomerAccessRecord
{
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } = string.Empty;
    public string ChallengeHash { get; set; } = string.Empty;
    public string? SessionHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
}

public sealed class CustomerSupportRecord
{
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public string Locale { get; set; } = "en";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
}

public enum PlanChangeState { Quoted, Submitting, AwaitingPayment, Scheduled, Applying, Applied, Failed, Cancelled, Operational }
public enum PlanChangeDirection { Upgrade, Downgrade }

/// <summary>One durable change for the existing subscription and server, never a new order.</summary>
public sealed class SubscriptionPlanChange
{
    public Guid Id { get; set; }
    public Guid HostingServiceId { get; set; }
    public Guid TargetPlanId { get; set; }
    public string StripeSubscriptionId { get; set; } = string.Empty;
    public string StripeCustomerId { get; set; } = string.Empty;
    public string SubscriptionItemId { get; set; } = string.Empty;
    public string SourcePriceId { get; set; } = string.Empty;
    public string TargetPriceId { get; set; } = string.Empty;
    public string? BaselineInvoiceId { get; set; }
    public string? StripeInvoiceId { get; set; }
    public string? StripeScheduleId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string BillingPeriod { get; set; } = string.Empty;
    public long DueNowMinor { get; set; }
    public long NextInvoiceMinor { get; set; }
    public int MemoryMb { get; set; }
    public int CpuPercent { get; set; }
    public int DiskMb { get; set; }
    public int BackupLimit { get; set; }
    public int DatabaseLimit { get; set; }
    public int AllocationLimit { get; set; }
    public PlanChangeDirection Direction { get; set; }
    public PlanChangeState State { get; set; }
    public bool Active { get; set; } = true;
    public bool CancellationRequested { get; set; }
    public DateTimeOffset ProrationAt { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset QuoteExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? FailureCode { get; set; }
}
