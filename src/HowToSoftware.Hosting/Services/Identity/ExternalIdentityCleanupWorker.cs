using HowToSoftware.Hosting.Data;
using Microsoft.EntityFrameworkCore;

namespace HowToSoftware.Hosting.Services.Identity;

/// <summary>Short-lived authorization proofs are not a permanent customer activity archive.</summary>
public sealed class ExternalIdentityCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ExternalIdentityCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CommerceDbContext>>();
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var cutoff = clock.GetUtcNow().AddHours(-1);
                await db.ExternalIdentityFlows.Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("External identity cleanup will retry ({FailureClass}).", e.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
