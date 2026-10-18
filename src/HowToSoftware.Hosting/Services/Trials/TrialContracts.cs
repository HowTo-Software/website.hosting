namespace HowToSoftware.Hosting.Services.Trials;

public enum TrialState { PendingVerification, Provisioning, Installing, Active, Expired, Deleted, Converted, Rejected, Deleting }
public enum TrialRequestOutcome { Sent, Unavailable, InvalidSelection }
public enum TrialConfirmationOutcome { Confirmed, InvalidOrExpired, Unavailable, AlreadyClaimed }
public enum TrialConversionOutcome { NotLinked, Converted, Retry, Unavailable }

public sealed record TrialSelectionRequest(string Email, string DisplayName, string GameSlug = "project-zomboid", string? ProfileId = null, string? Version = null, string Locale = "en");
public sealed record TrialRequestResult(TrialRequestOutcome Outcome);
public sealed record TrialConfirmationResult(TrialConfirmationOutcome Outcome, string? AccessToken = null);
public sealed record TrialUpgradeReservation(bool Reserved, Guid? TrialId = null);
public sealed record TrialConversionResult(TrialConversionOutcome Outcome, TrialPanelServer? Server = null);
public sealed record TrialAccountView(Guid Id, string Email, string DisplayName, string GameSlug, string? ProfileId, string? Version,
    TrialState State, int? ServerId, string? ServerIdentifier, DateTimeOffset? StartedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? DeleteAfter, bool CanUpgrade, Guid? UpgradeOrderId, TrialPanelServer? Server = null);
public sealed record TrialPanelUser(int Id, string Email);
public sealed record TrialPanelServer(int Id, string Identifier, string Uuid, bool Installed, bool Suspended,
    string? Name = null, TrialResourceLimits? Limits = null, string? Address = null, int? Port = null,
    int UserId = 0, string? ExternalId = null);
public sealed record TrialResourceLimits(int MemoryMb, int CpuPercent, int DiskMb, int Backups = 1, int Databases = 0, int Allocations = 1);
public sealed record TrialProvisioningRequest(Guid TrialId, string ExternalId, int UserId, TrialResourceLimits Limits,
    string GameSlug = "project-zomboid", string? ProfileId = null, string? Version = null, TrialGameProfile? Profile = null,
    string? DisplayName = null);

public interface ITrialService
{
    bool IsAvailable { get; }
    IReadOnlyList<TrialGameProfile> AvailableProfiles { get; }
    Task<TrialRequestResult> RequestAsync(TrialSelectionRequest request, CancellationToken cancellationToken = default);
    Task<TrialRequestResult> RecoverAccessAsync(string email, string locale, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrialRequestResult(TrialRequestOutcome.Unavailable));
    Task<TrialRequestResult> RequestAsync(string email, string displayName, CancellationToken cancellationToken = default) =>
        RequestAsync(new(email, displayName), cancellationToken);
    Task<TrialConfirmationResult> ConfirmAsync(string verificationToken, CancellationToken cancellationToken = default);
    Task<TrialAccountView?> GetByAccessTokenAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<TrialUpgradeReservation> ReserveUpgradeAsync(Guid trialId, string accessToken, Guid orderId, CancellationToken cancellationToken = default);
    Task ReleaseUpgradeAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<TrialConversionResult> ConvertPaidOrderAsync(Guid orderId, TrialResourceLimits limits, CancellationToken cancellationToken = default);
    Task ProcessDueAsync(CancellationToken cancellationToken = default);
}

public interface ITrialPanelGateway
{
    bool IsConfigured { get; }
    bool CanProvision(string gameSlug, string? profileId = null, string? version = null) => IsConfigured && gameSlug == "project-zomboid";
    Task<TrialPanelUser> ResolveUserAsync(string email, string displayName, CancellationToken cancellationToken);
    Task<TrialPanelServer?> FindServerAsync(string externalId, CancellationToken cancellationToken);
    Task<TrialPanelServer> CreateServerAsync(TrialProvisioningRequest request, CancellationToken cancellationToken);
    Task<TrialPanelServer?> InspectServerAsync(int serverId, CancellationToken cancellationToken);
    Task SuspendAsync(int serverId, CancellationToken cancellationToken);
    Task DeleteAsync(int serverId, CancellationToken cancellationToken);
    Task UpgradeAsync(int serverId, TrialResourceLimits limits, CancellationToken cancellationToken);
    Task ResumeAsync(int serverId, CancellationToken cancellationToken);
}

public interface ITrialEmailSender
{
    bool IsConfigured { get; }
    Task SendVerificationAsync(string email, string displayName, string verificationUrl, string locale, CancellationToken cancellationToken);
    Task SendReadyAsync(string email, string panelUrl, DateTimeOffset expiresAt, string locale, CancellationToken cancellationToken);
    Task SendExpiredAsync(string email, DateTimeOffset deleteAfter, string locale, CancellationToken cancellationToken);
    Task SendDeletedAsync(string email, string locale, CancellationToken cancellationToken);
}
