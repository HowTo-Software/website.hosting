using System.Text.Json.Serialization;

namespace HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;

/// <summary>
/// The panel's single-item envelope: <c>{"object":"server","attributes":{…}}</c>.
/// </summary>
/// <typeparam name="T">Attribute payload type.</typeparam>
/// <remarks>
/// Pterodactyl wraps everything, including error-free single reads. It is not JSON:API despite
/// looking a little like it, so nothing here tries to be generic beyond this envelope.
/// </remarks>
public sealed record PterodactylItem<T>
{
    /// <summary>Resource name, e.g. <c>server</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; init; } = string.Empty;

    /// <summary>The resource itself.</summary>
    [JsonPropertyName("attributes")]
    public T? Attributes { get; init; }
}

/// <summary>
/// The panel's list envelope: <c>{"object":"list","data":[…],"meta":{"pagination":{…}}}</c>.
/// </summary>
/// <typeparam name="T">Attribute payload type of each entry.</typeparam>
public sealed record PterodactylList<T>
{
    /// <summary>Always <c>list</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; init; } = string.Empty;

    /// <summary>The entries, each in its own single-item envelope.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<PterodactylItem<T>> Data { get; init; } = [];

    /// <summary>Pagination block.</summary>
    [JsonPropertyName("meta")]
    public PterodactylListMeta? Meta { get; init; }

    /// <summary>The unwrapped entries, which is what every caller actually wants.</summary>
    [JsonIgnore]
    public IReadOnlyList<T> Items =>
        [.. Data.Where(entry => entry.Attributes is not null).Select(entry => entry.Attributes!)];
}

/// <summary>Wrapper around the panel's pagination block.</summary>
public sealed record PterodactylListMeta
{
    /// <summary>Page counters.</summary>
    [JsonPropertyName("pagination")]
    public PterodactylPagination? Pagination { get; init; }
}

/// <summary>How many results there are, and where in them we are.</summary>
public sealed record PterodactylPagination
{
    /// <summary>Total matching records.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>Records on this page.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>Page size.</summary>
    [JsonPropertyName("per_page")]
    public int PerPage { get; init; }

    /// <summary>1-based current page.</summary>
    [JsonPropertyName("current_page")]
    public int CurrentPage { get; init; }

    /// <summary>Number of pages.</summary>
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; init; }
}

/// <summary>The panel's error envelope.</summary>
public sealed record PterodactylErrorEnvelope
{
    /// <summary>One entry per rejected field or per failure.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<PterodactylErrorEntry> Errors { get; init; } = [];
}

/// <summary>One entry in the panel's error envelope.</summary>
public sealed record PterodactylErrorEntry
{
    /// <summary>Panel exception name, e.g. <c>ValidationException</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>HTTP status, sent as a string.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Human-readable message.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    /// <summary>Extra context; carries <c>source_field</c> on validation failures.</summary>
    [JsonPropertyName("meta")]
    public PterodactylErrorMeta? Meta { get; init; }
}

/// <summary>Validation context attached to an error entry.</summary>
public sealed record PterodactylErrorMeta
{
    /// <summary>Dotted field path the error refers to, e.g. <c>limits.memory</c>.</summary>
    [JsonPropertyName("source_field")]
    public string? SourceField { get; init; }

    /// <summary>Validation rule that failed, e.g. <c>required</c>.</summary>
    [JsonPropertyName("rule")]
    public string? Rule { get; init; }
}

// ── Resources ─────────────────────────────────────────────────────────────

/// <summary>A node, as the Application API reports it.</summary>
public sealed record PterodactylNode
{
    /// <summary>Numeric node id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Location the node sits in.</summary>
    [JsonPropertyName("location_id")]
    public int LocationId { get; init; }

    /// <summary>Whether the node accepts automatic deployment.</summary>
    /// <remarks>
    /// A private node is skipped entirely by the panel's node selection, so a deployment that
    /// "cannot find a viable node" while a node looks healthy is usually this.
    /// </remarks>
    [JsonPropertyName("public")]
    public bool IsPublic { get; init; }

    /// <summary>Whether the node is in maintenance mode.</summary>
    [JsonPropertyName("maintenance_mode")]
    public bool MaintenanceMode { get; init; }

    /// <summary>Total memory the node offers, in MiB.</summary>
    [JsonPropertyName("memory")]
    public long MemoryMib { get; init; }

    /// <summary>Percentage the node may be over-allocated on memory.</summary>
    [JsonPropertyName("memory_overallocate")]
    public int MemoryOverallocate { get; init; }

    /// <summary>Total disk the node offers, in MiB.</summary>
    [JsonPropertyName("disk")]
    public long DiskMib { get; init; }

    /// <summary>Percentage the node may be over-allocated on disk.</summary>
    [JsonPropertyName("disk_overallocate")]
    public int DiskOverallocate { get; init; }

    /// <summary>Wings host name.</summary>
    [JsonPropertyName("fqdn")]
    public string Fqdn { get; init; } = string.Empty;

    /// <summary>What is already committed on this node.</summary>
    [JsonPropertyName("allocated_resources")]
    public PterodactylAllocatedResources? AllocatedResources { get; init; }
}

/// <summary>Resources already committed on a node.</summary>
public sealed record PterodactylAllocatedResources
{
    /// <summary>Committed memory, in MiB.</summary>
    [JsonPropertyName("memory")]
    public long MemoryMib { get; init; }

    /// <summary>Committed disk, in MiB.</summary>
    [JsonPropertyName("disk")]
    public long DiskMib { get; init; }
}

/// <summary>A location, as the Application API reports it.</summary>
public sealed record PterodactylLocation
{
    /// <summary>Numeric location id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Short code, e.g. <c>us-central</c>.</summary>
    [JsonPropertyName("short")]
    public string Short { get; init; } = string.Empty;

    /// <summary>Longer description.</summary>
    [JsonPropertyName("long")]
    public string Long { get; init; } = string.Empty;
}

/// <summary>An egg, as the Application API reports it.</summary>
/// <remarks>
/// The egg is what supplies <c>docker_image</c> and <c>startup</c>. Both are <b>required</b> on
/// server creation, so they are read from here rather than guessed - which is also why the
/// provisioner needs the nest id: the egg endpoint is nested under it.
/// </remarks>
public sealed record PterodactylEgg
{
    /// <summary>Numeric egg id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Nest the egg belongs to.</summary>
    [JsonPropertyName("nest")]
    public int Nest { get; init; }

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The egg's default container image.</summary>
    [JsonPropertyName("docker_image")]
    public string DockerImage { get; init; } = string.Empty;

    /// <summary>The egg's default startup command.</summary>
    [JsonPropertyName("startup")]
    public string Startup { get; init; } = string.Empty;

    /// <summary>Variables, when requested with <c>?include=variables</c>.</summary>
    [JsonPropertyName("relationships")]
    public PterodactylEggRelationships? Relationships { get; init; }

    /// <summary>The egg's variables, or an empty list when they were not included.</summary>
    [JsonIgnore]
    public IReadOnlyList<PterodactylEggVariable> Variables =>
        Relationships?.Variables?.Items ?? [];
}

/// <summary>Included relationships on an egg.</summary>
public sealed record PterodactylEggRelationships
{
    /// <summary>The egg's variables.</summary>
    [JsonPropertyName("variables")]
    public PterodactylList<PterodactylEggVariable>? Variables { get; init; }
}

/// <summary>One environment variable an egg declares.</summary>
public sealed record PterodactylEggVariable
{
    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The key used in the creation request's <c>environment</c> map.</summary>
    [JsonPropertyName("env_variable")]
    public string EnvVariable { get; init; } = string.Empty;

    /// <summary>Value used when the caller supplies none.</summary>
    [JsonPropertyName("default_value")]
    public string? DefaultValue { get; init; }

    /// <summary>Laravel validation rules, e.g. <c>required|string|max:20</c>.</summary>
    [JsonPropertyName("rules")]
    public string Rules { get; init; } = string.Empty;

    /// <summary>Whether the panel rejects a creation request that omits this variable.</summary>
    [JsonIgnore]
    public bool IsRequired =>
        Rules.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("required", StringComparer.OrdinalIgnoreCase);
}

/// <summary>A user, as the Application API reports it.</summary>
public sealed record PterodactylUser
{
    /// <summary>Numeric user id, which is what server creation references.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Our own correlation id.</summary>
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }

    /// <summary>Panel UUID.</summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>Panel username, lower-cased by the panel on save.</summary>
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>Email address.</summary>
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    /// <summary>Whether the account is a panel administrator.</summary>
    [JsonPropertyName("root_admin")]
    public bool RootAdmin { get; init; }
}

/// <summary>A server, as the Application API reports it.</summary>
public sealed record PterodactylServer
{
    /// <summary>Numeric id - the one every other endpoint takes.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Our own correlation id.</summary>
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }

    /// <summary>Panel UUID.</summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>Short identifier, the first eight characters of the UUID.</summary>
    [JsonPropertyName("identifier")]
    public string Identifier { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Lifecycle status; <c>installing</c> immediately after creation.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Node the panel placed the server on.</summary>
    [JsonPropertyName("node")]
    public int Node { get; init; }

    /// <summary>Allocation the panel bound to it.</summary>
    [JsonPropertyName("allocation")]
    public int Allocation { get; init; }

    /// <summary>Owning user.</summary>
    [JsonPropertyName("user")]
    public int User { get; init; }

    /// <summary>Egg it was built from.</summary>
    [JsonPropertyName("egg")]
    public int Egg { get; init; }

    /// <summary>Container limits the panel recorded.</summary>
    [JsonPropertyName("limits")]
    public PterodactylServerLimits? Limits { get; init; }

    [JsonPropertyName("feature_limits")]
    public PterodactylServerFeatureLimits? FeatureLimits { get; init; }

    [JsonPropertyName("container")]
    public PterodactylServerContainer? Container { get; init; }

    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    [JsonPropertyName("relationships")]
    public PterodactylServerRelationships? Relationships { get; init; }
}

/// <summary>Limits as reported back on a server.</summary>
public sealed record PterodactylServerLimits
{
    /// <summary>Memory ceiling, in MiB.</summary>
    [JsonPropertyName("memory")]
    public long Memory { get; init; }

    /// <summary>Disk ceiling, in MiB.</summary>
    [JsonPropertyName("disk")]
    public long Disk { get; init; }

    /// <summary>CPU ceiling, as a percentage.</summary>
    [JsonPropertyName("cpu")]
    public int Cpu { get; init; }

    [JsonPropertyName("swap")]
    public int Swap { get; init; }

    [JsonPropertyName("io")]
    public int Io { get; init; } = 500;

    [JsonPropertyName("threads")]
    public string? Threads { get; init; }

    [JsonPropertyName("oom_disabled")]
    public bool OomDisabled { get; init; }
}

/// <summary>Nullable caps preserve panel accounts whose feature limit is unlimited.</summary>
public sealed record PterodactylServerFeatureLimits
{
    [JsonPropertyName("databases")]
    public int? Databases { get; init; }

    [JsonPropertyName("allocations")]
    public int? Allocations { get; init; }

    [JsonPropertyName("backups")]
    public int? Backups { get; init; }
}

public sealed record PterodactylServerContainer
{
    [JsonPropertyName("installed")]
    public int Installed { get; init; }
}

public sealed record PterodactylServerRelationships
{
    [JsonPropertyName("allocations")]
    public PterodactylList<PterodactylAllocation>? Allocations { get; init; }
}

public sealed record PterodactylAllocation
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }
    [JsonPropertyName("port")]
    public int Port { get; init; }
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
