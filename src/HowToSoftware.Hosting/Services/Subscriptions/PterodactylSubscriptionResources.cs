using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Requests;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Trials;

namespace HowToSoftware.Hosting.Services.Subscriptions;

/// <summary>Changes limits only. No create, reinstall, rename, credentials or allocation mutation.</summary>
public sealed class PterodactylSubscriptionResources(IPterodactylClient panel) : ISubscriptionResources
{
    public async Task<bool> CanUpgradeAsync(ManagedService service, TrialResourceLimits target, CancellationToken ct)
    {
        var server = await ReadOwnedAsync(service, ct);
        if (server.Limits is not { Memory: > 0, Disk: > 0 } before || target.MemoryMb < before.Memory
            || target.DiskMb < before.Disk || (before.Cpu == 0 && target.CpuPercent != 0)
            || (target.CpuPercent != 0 && target.CpuPercent < before.Cpu)
            || target.Backups < server.FeatureLimits?.Backups || target.Databases < server.FeatureLimits?.Databases)
            return false;
        return await FitsAsync(server, target, ct);
    }

    private async Task<bool> FitsAsync(PterodactylServer server, TrialResourceLimits target, CancellationToken ct)
    {
        var node = (await panel.GetNodesAsync(ct)).SingleOrDefault(x => x.Id == server.Node);
        if (node?.AllocatedResources is null || node.MaintenanceMode || server.Limits is not { } before) return false;
        static bool Fits(long used, long delta, long total, int overallocate) =>
            delta <= 0 || overallocate == -1 || (total > 0 && used + delta <= total * (1m + overallocate / 100m));
        return target.MemoryMb > 0 && target.DiskMb > 0 && target.CpuPercent >= 0
            && Fits(node.AllocatedResources.MemoryMib, Math.Max(0, target.MemoryMb - before.Memory), node.MemoryMib, node.MemoryOverallocate)
            && Fits(node.AllocatedResources.DiskMib, Math.Max(0, target.DiskMb - before.Disk), node.DiskMib, node.DiskOverallocate);
    }

    public async Task ApplyAsync(ManagedService service, SubscriptionPlanChange change, CancellationToken ct)
    {
        var before = await ReadOwnedAsync(service, ct);
        var limits = before.Limits ?? throw new InvalidOperationException("Server limits unavailable.");
        // Application API exposes allocated disk, NOT files actually used. Unknown usage is not
        // proof of safety: preserve files and require an operational check instead of shrinking.
        if (limits.Disk == 0 || change.DiskMb < limits.Disk) throw new UnsafeResourceReductionException();
        var target = PlanChangeRules.Limits(change);
        if (!await FitsAsync(before, target, ct)) throw new InvalidOperationException("Node capacity unavailable.");
        var after = await panel.UpdateServerBuildAsync(before.Id, new()
        {
            Allocation = before.Allocation,
            Limits = new(target.MemoryMb, limits.Swap, target.DiskMb, limits.Io, target.CpuPercent) { Threads = limits.Threads },
            OomDisabled = limits.OomDisabled,
            FeatureLimits = new(target.Databases, Math.Max(target.Allocations, before.Relationships?.Allocations?.Items.Count ?? 1), target.Backups)
        }, ct);
        if (after.Id != before.Id || after.Uuid != before.Uuid || after.User != before.User
            || after.Egg != before.Egg || after.Node != before.Node || after.Allocation != before.Allocation
            || after.Limits is null
            || after.Limits.Memory != change.MemoryMb || after.Limits.Cpu != change.CpuPercent || after.Limits.Disk != change.DiskMb)
            throw new InvalidOperationException("Panel did not confirm the expected build update.");
    }

    private async Task<PterodactylServer> ReadOwnedAsync(ManagedService s, CancellationToken ct)
    {
        if (!panel.IsConfigured || s.ServerId is not > 0 || string.IsNullOrEmpty(s.ServerUuid))
            throw new InvalidOperationException("Server identity unavailable.");
        var user = await panel.FindUserByEmailAsync(s.OwnerEmail, ct);
        var server = await panel.GetServerAsync(s.ServerId.Value, ct);
        if (user is null || user.RootAdmin || !string.Equals(user.Email.Trim(), s.OwnerEmail.Trim(), StringComparison.OrdinalIgnoreCase)
            || server is null || server.Id != s.ServerId || server.Uuid != s.ServerUuid || server.User != user.Id)
            throw new InvalidOperationException("Server ownership did not match.");
        return server;
    }
}
