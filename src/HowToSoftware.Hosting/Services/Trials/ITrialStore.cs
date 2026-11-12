using HowToSoftware.Hosting.Models.Commerce;

namespace HowToSoftware.Hosting.Services.Trials;

public sealed class TrialAccountAlreadyClaimedException : Exception
{
    public TrialAccountAlreadyClaimedException() : base("This panel account already has a trial entitlement.") { }
}

/// <summary>Every mutation is conditional; a process-local lock is not a lifecycle boundary.</summary>
public interface ITrialStore
{
    bool IsConfigured { get; }
    Task<ServerTrialRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken);
    Task<ServerTrialRecord?> FindByVerificationAsync(string hash, CancellationToken cancellationToken);
    Task<ServerTrialRecord?> FindByAccessAsync(string hash, CancellationToken cancellationToken);
    Task<ServerTrialRecord?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> AddPendingAsync(ServerTrialRecord record, CancellationToken cancellationToken);
    Task<bool> RefreshVerificationAsync(ServerTrialRecord record, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Withdraws an undelivered verification link, only if it is still the current one.</summary>
    Task ClearVerificationAsync(Guid id, string verificationHash, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(Guid id, string verificationHash, string accessHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<ServerTrialRecord?> ClaimAsync(Guid id, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid id, Guid token, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> SaveLeasedAsync(ServerTrialRecord record, Guid token, DateTimeOffset now, CancellationToken cancellationToken);
    Task ReleaseLeaseAsync(Guid id, Guid token, CancellationToken cancellationToken);
    Task<bool> ReserveUpgradeAsync(Guid id, string accessHash, Guid orderId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task ReleaseUpgradeAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> IsPaidOrderAsync(Guid orderId, CancellationToken cancellationToken);
    Task<Guid?> FindPaidUpgradeAsync(Guid trialId, CancellationToken cancellationToken);
    Task<bool> ClearUnpaidReservationAsync(Guid id, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> BeginDeleteAsync(Guid id, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);
}
