using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Requests;
using HowToSoftware.Hosting.Models;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Provisioning;

/// <summary>
/// The provisioning pipeline: order in, running server out.
/// </summary>
/// <remarks>
/// <para>
/// The sequence is deliberately boring - resolve the plan, read the egg, find or create the
/// panel user, check whether this request already produced a server, then create one. Every step
/// that could create something checks first whether it already did.
/// </para>
/// <para>
/// Node placement is Pterodactyl's job, not ours. The request carries a <c>deploy</c> block
/// naming the location, and the panel picks a public node with memory and disk headroom and a
/// free allocation on it. Writing a load balancer here would duplicate logic the panel already
/// has, using data we would have to poll for.
/// </para>
/// </remarks>
public sealed class ProvisioningService : IProvisioningService
{
    private readonly IPterodactylClient _panel;
    private readonly IPlanCatalogService _plans;
    private readonly IGameTemplateCatalog _templates;
    private readonly IOptionsMonitor<PterodactylOptions> _options;
    private readonly ILogger<ProvisioningService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="panel">Panel API client.</param>
    /// <param name="plans">Plan catalogue - the source of the resource limits.</param>
    /// <param name="templates">Game templates.</param>
    /// <param name="options">Panel deployment settings.</param>
    /// <param name="logger">Structured log sink. Never receives a key or a password.</param>
    public ProvisioningService(
        IPterodactylClient panel,
        IPlanCatalogService plans,
        IGameTemplateCatalog templates,
        IOptionsMonitor<PterodactylOptions> options,
        ILogger<ProvisioningService> logger)
    {
        _panel = panel;
        _plans = plans;
        _templates = templates;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PanelSnapshot> InspectPanelAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        if (!options.IsConfigured)
        {
            return new PanelSnapshot(
                IsConfigured: false,
                IsReachable: false,
                PterodactylFailure.NotConfigured,
                "Set Pterodactyl:BaseUrl and Pterodactyl:ApiKey before running a connectivity test.",
                [], [], null, null, [], options.DescribeKey());
        }

        try
        {
            // The node list is the connectivity test: it proves the panel answers, the key
            // authenticates, and the response parses.
            var nodes = await _panel.GetNodesAsync(cancellationToken).ConfigureAwait(false);
            var locations = await _panel.GetLocationsAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Panel connectivity verified: {NodeCount} nodes, {LocationCount} locations",
                nodes.Count,
                locations.Count);

            // The egg is read separately, because a panel that answers but has the wrong egg id
            // configured is a different problem from a panel that is down.
            string? eggName = null;
            string? eggImage = null;
            IReadOnlyList<string> requiredVariables = [];

            try
            {
                var egg = await _panel
                    .GetEggAsync(options.NestId, options.EggId, cancellationToken)
                    .ConfigureAwait(false);

                eggName = egg.Name;
                eggImage = egg.DockerImage;
                requiredVariables = [.. egg.Variables.Where(v => v.IsRequired).Select(v => v.EnvVariable)];
            }
            catch (PterodactylApiException exception)
            {
                _logger.LogWarning(
                    "Panel reachable but the configured egg could not be read: {Failure}",
                    exception.Failure);
            }

            return new PanelSnapshot(
                IsConfigured: true,
                IsReachable: true,
                PterodactylFailure.None,
                string.Empty,
                [.. nodes.Select(Summarise)],
                [.. locations.Select(location =>
                    new PanelLocationSummary(location.Id, location.Short, location.Long))],
                eggName,
                eggImage,
                requiredVariables,
                options.DescribeKey());
        }
        catch (PterodactylApiException exception)
        {
            _logger.LogWarning("Panel connectivity test failed: {Failure}", exception.Failure);

            return new PanelSnapshot(
                IsConfigured: true,
                IsReachable: false,
                exception.Failure,
                exception.Message,
                [], [], null, null, [], options.DescribeKey());
        }
    }

    /// <inheritdoc />
    public async Task<ProvisioningResult> ProvisionAsync(
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation(
            "Provisioning started for request {RequestId} (plan {PlanSlug}, test {IsTest})",
            request.RequestId,
            request.PlanSlug,
            request.IsTest);

        if (_plans.FindBySlug(request.PlanSlug) is not { } plan)
        {
            return Fail(request, PterodactylFailure.ValidationFailed,
                $"No plan carries the slug '{request.PlanSlug}'.");
        }

        if (_templates.Find(plan.GameTemplateId) is not { } template)
        {
            return Fail(request, PterodactylFailure.ValidationFailed,
                $"Plan '{plan.Slug}' names game template '{plan.GameTemplateId}', which is not registered.");
        }

        _logger.LogInformation(
            "Plan selected: {PlanSlug} - {MemoryMib} MiB, {CpuPercent}% CPU, {DiskMib} MiB disk",
            plan.Slug,
            plan.MemoryMib,
            plan.CpuPercent,
            plan.DiskMib);

        if (template.EggId <= 0 || template.LocationId <= 0)
        {
            return Fail(request, PterodactylFailure.NotConfigured,
                "Pterodactyl:EggId, Pterodactyl:NestId and Pterodactyl:LocationId must all be set "
                    + "before a server can be created.");
        }

        var serverExternalId = request.IsTest
            ? PterodactylNaming.TestServerExternalId(request.RequestId)
            : PterodactylNaming.ServerExternalId(request.RequestId);

        try
        {
            // Idempotence first. A retried webhook, a double-clicked button and a re-run test all
            // land here, and none of them should produce a second server.
            var existing = await _panel
                .FindServerByExternalIdAsync(serverExternalId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                _logger.LogInformation(
                    "Request {RequestId} already provisioned server {ServerId}; reusing it",
                    request.RequestId,
                    existing.Id);

                return Describe(ProvisioningOutcome.AlreadyProvisioned, request, plan, existing,
                    existing.User, userWasReused: true,
                    "A server already exists for this request; it was returned unchanged.");
            }

            var egg = await _panel
                .GetEggAsync(template.NestId, template.EggId, cancellationToken)
                .ConfigureAwait(false);

            var (user, wasReused) = await ResolveUserAsync(request, cancellationToken).ConfigureAwait(false);

            var payload = BuildCreateRequest(request, plan, template, egg, user.Id, serverExternalId);

            _logger.LogInformation(
                "Creating server for request {RequestId}: user {PanelUserId}, egg {EggId}, location {LocationId}",
                request.RequestId,
                user.Id,
                template.EggId,
                template.LocationId);

            var created = await _panel.CreateServerAsync(payload, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Server {ServerId} created for request {RequestId} on node {NodeId}, allocation {AllocationId}",
                created.Id,
                request.RequestId,
                created.Node,
                created.Allocation);

            return Describe(ProvisioningOutcome.Created, request, plan, created, user.Id, wasReused,
                "Server created. The panel is installing it now.");
        }
        catch (PterodactylApiException exception) when (exception.Failure is PterodactylFailure.AlreadyExists)
        {
            // Something already holds this external id, which means a concurrent attempt at the
            // same request won the race. Re-read rather than reporting a failure the caller
            // would retry into a third attempt.
            _logger.LogInformation(
                "Request {RequestId} lost a creation race; re-reading the existing server",
                request.RequestId);

            var raced = await _panel
                .FindServerByExternalIdAsync(serverExternalId, cancellationToken)
                .ConfigureAwait(false);

            return raced is not null
                ? Describe(ProvisioningOutcome.AlreadyProvisioned, request, plan, raced, raced.User,
                    userWasReused: true,
                    "A concurrent request had already provisioned this server; it was returned unchanged.")
                : Fail(request, exception.Failure, PublicFailureMessage(exception.Failure));
        }
        catch (PterodactylApiException exception)
        {
            _logger.LogError(
                "Provisioning failed for request {RequestId} with {Failure}.",
                request.RequestId,
                exception.Failure);

            return Fail(request, exception.Failure, PublicFailureMessage(exception.Failure));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static string PublicFailureMessage(PterodactylFailure failure) => failure switch
    {
        PterodactylFailure.NotConfigured => "Server provisioning is not configured.",
        PterodactylFailure.NoCapacity => "No server capacity is currently available.",
        PterodactylFailure.RateLimited => "The server panel is temporarily rate-limiting requests.",
        PterodactylFailure.Timeout or PterodactylFailure.Unreachable =>
            "The server panel is temporarily unavailable.",
        _ => "The server panel could not complete the provisioning request."
    };

    /// <inheritdoc />
    public async Task<ProvisioningResult> DeleteTestServerAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        // The id can only ever be turned into a test external id. There is no path from a form
        // field to an arbitrary panel server.
        var externalId = PterodactylNaming.TestServerExternalId(requestId);

        try
        {
            var server = await _panel
                .FindServerByExternalIdAsync(externalId, cancellationToken)
                .ConfigureAwait(false);

            if (server is null)
            {
                return ProvisioningResult.Failed(requestId, null, PterodactylFailure.NotFound,
                    "No test server exists for that request id.");
            }

            // Checked again against what the panel actually returned, rather than trusting the id
            // we constructed. If this ever fails, something is very wrong and nothing is deleted.
            if (!PterodactylNaming.IsTestServer(server.ExternalId))
            {
                _logger.LogError(
                    "Refusing to delete server {ServerId}: external id {ExternalId} is not a lab server",
                    server.Id,
                    server.ExternalId);

                return ProvisioningResult.Failed(requestId, null, PterodactylFailure.Forbidden,
                    "That server was not created by the provisioning lab and will not be deleted here.");
            }

            await _panel.DeleteServerAsync(server.Id, force: false, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Test server {ServerId} deleted for request {RequestId}",
                server.Id,
                requestId);

            return new ProvisioningResult(
                ProvisioningOutcome.Created, requestId, server.Id, server.Uuid, server.Identifier,
                server.ExternalId, server.Node, server.Allocation, server.User, true, "deleted",
                null, PterodactylFailure.None, "Test server deleted.");
        }
        catch (PterodactylApiException exception)
        {
            _logger.LogError(
                "Deleting test server for request {RequestId} failed with {Failure}",
                requestId,
                exception.Failure);

            return ProvisioningResult.Failed(requestId, null, exception.Failure, exception.Message);
        }
    }

    // ── Steps ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the customer's panel account, or creates one.
    /// </summary>
    /// <remarks>
    /// HTS external ids take precedence. A verified billing email may also resolve an existing
    /// customer account created directly in the panel; the development lab keeps its own ids.
    /// </remarks>
    private async Task<(PterodactylUser User, bool WasReused)> ResolveUserAsync(
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var externalId = request.IsTest
            ? PterodactylNaming.TestCustomerExternalId(request.CustomerId)
            : PterodactylNaming.CustomerExternalId(request.CustomerId);

        var existing = await _panel
            .FindUserByExternalIdAsync(externalId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null && !request.IsTest)
        {
            existing = await _panel.FindUserByEmailAsync(request.CustomerEmail, cancellationToken)
                .ConfigureAwait(false);
        }

        if (existing is not null)
        {
            EnsureCustomerAccount(existing);
            // The customer id is derived from the email address, so it is not logged; the
            // request id correlates the entry instead.
            _logger.LogInformation(
                "Reusing panel user {PanelUserId} for request {RequestId}",
                existing.Id,
                request.RequestId);

            return (existing, true);
        }

        var localPart = request.CustomerEmail.Split('@', 2)[0];

        PterodactylUser created;
        try
        {
            created = await _panel.CreateUserAsync(
            new CreateUserRequest
            {
                ExternalId = externalId,
                Email = request.CustomerEmail,
                Username = PterodactylNaming.BuildUsername(localPart),
                FirstName = request.FirstName,
                LastName = request.LastName,
                // Generated, used once, and dropped. It is never logged, stored or shown - the
                // customer reaches the account through a password reset instead.
                Password = PterodactylNaming.GeneratePassword(),
                RootAdmin = false
            },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PterodactylApiException exception)
            when (!request.IsTest && (exception.StatusCode == 422
                || exception.Failure is PterodactylFailure.AlreadyExists))
        {
            // A concurrent signup can win email/external-id uniqueness after our lookup.
            // Recover by identity rather than masking an unrelated validation failure.
            var raced = await _panel.FindUserByExternalIdAsync(externalId, cancellationToken)
                .ConfigureAwait(false)
                ?? await _panel.FindUserByEmailAsync(request.CustomerEmail, cancellationToken)
                    .ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }
            EnsureCustomerAccount(raced);
            return (raced, true);
        }
        EnsureCustomerAccount(created);

        _logger.LogInformation(
            "Created panel user {PanelUserId} for request {RequestId}",
            created.Id,
            request.RequestId);

        return (created, false);
    }

    private static void EnsureCustomerAccount(PterodactylUser user)
    {
        if (user.Id <= 0 || user.RootAdmin)
        {
            throw new PterodactylApiException(PterodactylFailure.Forbidden,
                "An administrative panel account cannot own a provisioned customer server.");
        }
    }

    /// <summary>
    /// Assembles the creation request.
    /// </summary>
    /// <remarks>
    /// <c>docker_image</c> and <c>startup</c> are required by the panel, so they come from the
    /// egg unless the template overrides them. Environment starts from the egg's own defaults so
    /// that every variable the egg marks required is present, then the template's values are laid
    /// over the top.
    /// </remarks>
    private static CreateServerRequest BuildCreateRequest(
        ProvisioningRequest request,
        HostingPlan plan,
        GameTemplate template,
        PterodactylEgg egg,
        int panelUserId,
        string serverExternalId)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in egg.Variables)
        {
            environment[variable.EnvVariable] = variable.DefaultValue ?? string.Empty;
        }

        foreach (var (key, value) in template.Environment)
        {
            environment[key] = value;
        }

        HowToSoftware.Hosting.Services.Trials.TrialServerIdentity.SetProjectZomboidPassword(template.Id, environment);
        var limits = plan.ToResourceLimits();
        var features = plan.ToFeatureLimits();

        return new CreateServerRequest
        {
            ExternalId = serverExternalId,
            Name = request.ServerName,
            User = panelUserId,
            Egg = template.EggId,
            DockerImage = template.DockerImage ?? egg.DockerImage,
            Startup = template.StartupCommand ?? egg.Startup,
            Environment = environment,
            Limits = new ServerLimitsPayload(
                Memory: limits.MemoryMib,
                // Swap disabled. A game server that starts swapping has already failed; letting
                // it thrash the node's disk makes it everyone else's problem too.
                Swap: 0,
                Disk: limits.DiskMib,
                // The panel's own default block-IO weight. Values outside 10-1000 are rejected.
                Io: 500,
                Cpu: limits.CpuPercent),
            FeatureLimits = new ServerFeatureLimitsPayload(
                Databases: features.Databases,
                Allocations: features.Allocations,
                Backups: features.Backups),
            Deploy = new ServerDeploymentPayload(
                Locations: [template.LocationId],
                // A dedicated IP per server would exhaust the address pool immediately; servers
                // share an IP and are distinguished by port.
                DedicatedIp: false,
                PortRange: template.PortRange),
            StartOnCompletion = request.StartOnCompletion,
            Description = request.IsTest
                ? "Created by the HowToSoftware provisioning lab."
                : null
        };
    }

    // ── Shaping ───────────────────────────────────────────────────────────

    private static PanelNodeSummary Summarise(PterodactylNode node) => new(
        node.Id,
        node.Name,
        node.LocationId,
        node.IsPublic,
        node.MaintenanceMode,
        node.MemoryMib,
        node.AllocatedResources?.MemoryMib ?? 0,
        node.DiskMib,
        node.AllocatedResources?.DiskMib ?? 0);

    private static ProvisioningResult Describe(
        ProvisioningOutcome outcome,
        ProvisioningRequest request,
        HostingPlan plan,
        PterodactylServer server,
        int panelUserId,
        bool userWasReused,
        string message) =>
        new(outcome, request.RequestId, server.Id, server.Uuid, server.Identifier, server.ExternalId,
            server.Node, server.Allocation, panelUserId, userWasReused, server.Status, plan.Slug,
            PterodactylFailure.None, message);

    private ProvisioningResult Fail(
        ProvisioningRequest request,
        PterodactylFailure failure,
        string message)
    {
        _logger.LogWarning(
            "Provisioning rejected for request {RequestId}: {Failure}",
            request.RequestId,
            failure);

        return ProvisioningResult.Failed(request.RequestId, request.PlanSlug, failure, message);
    }
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
