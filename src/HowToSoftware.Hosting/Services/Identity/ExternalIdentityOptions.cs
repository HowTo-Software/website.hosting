namespace HowToSoftware.Hosting.Services.Identity;

public static class ExternalProviders
{
    public const string Directory = "active-directory";
    public const string Discord = "discord";
    public const string DiscordIssuer = "https://discord.com";
    public const string DiscordScheme = "HTS.Discord";
    public const string DirectoryScheme = "HTS.ActiveDirectory";
    public const string SinkScheme = "HTS.ExternalSink";
    public const string DiscordCallback = "/account/connections/callback/discord";
    public const string DirectoryCallback = "/account/connections/callback/active-directory";
    public static bool Known(string provider) => provider is Directory or Discord;
    public static string Scheme(string provider) => provider switch
    { Directory => DirectoryScheme, Discord => DiscordScheme, _ => throw new ArgumentException("Unsupported identity provider.") };
}

public sealed class ExternalIdentityOptions
{
    public const string SectionName = "ExternalIdentity";
    public DirectoryIdentityOptions ActiveDirectory { get; set; } = new();
    public DiscordIdentityOptions Discord { get; set; } = new();
    public bool LinkingEnabled(string provider) => provider switch
    { ExternalProviders.Directory => ActiveDirectory.Enabled, ExternalProviders.Discord => Discord.Enabled, _ => false };
    public bool LoginEnabled(string provider) => provider switch
    { ExternalProviders.Directory => ActiveDirectory.Enabled && ActiveDirectory.LoginEnabled,
      ExternalProviders.Discord => Discord.Enabled && Discord.LoginEnabled, _ => false };
    public override string ToString() => nameof(ExternalIdentityOptions);
}

public sealed class DirectoryIdentityOptions
{
    public bool Enabled { get; set; }
    public bool LoginEnabled { get; set; }
    // On-premises transport is an explicit operator choice; never silently assume Entra ID.
    public string Protocol { get; set; } = "Unspecified";
    public string Issuer { get; set; } = string.Empty;
    public override string ToString() => nameof(DirectoryIdentityOptions);
}
public sealed class DiscordIdentityOptions
{
    public bool Enabled { get; set; }
    public bool LoginEnabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public override string ToString() => nameof(DiscordIdentityOptions);
}

public sealed record ExternalConnection(string Provider, string DisplayName, DateTimeOffset LinkedAt);
public sealed record ExternalFlowStart(Guid Id, string BrowserSecret);
public sealed record ExternalFlowView(Guid Id, string Provider, string Purpose, string DisplayName);
public sealed record ExternalFlowCompletion(bool Success, string? SessionToken = null);

public interface IExternalIdentityService
{
    Task<IReadOnlyList<ExternalConnection>> ConnectionsAsync(string? ownerSession, CancellationToken ct);
    Task<ExternalFlowStart?> BeginAsync(string provider, string purpose, string? ownerSession, CancellationToken ct);
    Task<ExternalFlowView?> PendingAsync(Guid id, string? browserSecret, string? ownerSession, CancellationToken ct);
    Task<ExternalFlowCompletion> CompleteAsync(Guid id, string? browserSecret, string? ownerSession, CancellationToken ct);
    Task<bool> UnlinkAsync(string provider, string? ownerSession, CancellationToken ct);
}
