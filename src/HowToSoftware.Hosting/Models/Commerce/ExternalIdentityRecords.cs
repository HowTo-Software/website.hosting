namespace HowToSoftware.Hosting.Models.Commerce;

/// <summary>A verified provider subject belongs to one existing HTS customer, never an email guess.</summary>
public sealed class CustomerExternalIdentity
{
    public Guid Id { get; set; }
    public Guid CustomerAccessId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>Short-lived, browser/session-bound intent. No passwords or OAuth tokens are persisted.</summary>
public sealed class ExternalIdentityFlow
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public Guid? CustomerAccessId { get; set; }
    public string? OwnerSessionHash { get; set; }
    public string BrowserSecretHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public string? DisplayName { get; set; }
}
