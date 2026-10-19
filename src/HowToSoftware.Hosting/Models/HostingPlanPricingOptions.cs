using System.Globalization;

namespace HowToSoftware.Hosting.Models;

/// <summary>
/// What each unit of a plan costs per month, bound from <c>HostingPlans:Rates</c>.
/// </summary>
/// <remarks>
/// <para>
/// The plan prices are not eight agreed numbers; they are one agreed rate card applied to the
/// resources each tier ships. Holding it this way means a rate change moves every tier at once
/// and cannot leave one card quietly inconsistent with the rest, and it means the relationship
/// between what a plan costs and what it contains is auditable rather than asserted.
/// </para>
/// <para>
/// Every rate is held as a string and parsed invariantly. Binding a decimal directly would let
/// the configuration provider use the host's culture, and a machine whose decimal separator is a
/// comma reads <c>0.90</c> as ninety. That is not a class of bug that may reach a price tag.
/// </para>
/// </remarks>
public sealed class PlanRateCard
{
    /// <summary>Monthly cost of 100% CPU allocation - the equivalent of one logical thread.</summary>
    public string CpuPer100Percent { get; set; } = string.Empty;

    /// <summary>Monthly cost of one GiB of memory.</summary>
    public string MemoryPerGb { get; set; } = string.Empty;

    /// <summary>Monthly cost of one storage block, whose size is <see cref="DiskBlockGb"/>.</summary>
    public string DiskPerBlock { get; set; } = string.Empty;

    /// <summary>Size of a storage block, in GiB.</summary>
    public int DiskBlockGb { get; set; } = 20;

    /// <summary>Whether every rate needed to price a plan has been configured.</summary>
    public bool IsConfigured =>
        Parse(CpuPer100Percent) is not null
        && Parse(MemoryPerGb) is not null
        && Parse(DiskPerBlock) is not null
        && DiskBlockGb > 0;

    /// <summary>
    /// Prices a plan from its resources.
    /// </summary>
    /// <param name="cpuPercent">Pterodactyl CPU limit as a percentage of one thread.</param>
    /// <param name="memoryGb">Memory in GiB.</param>
    /// <param name="diskGb">Storage in GiB.</param>
    /// <returns>
    /// The monthly price, or <see langword="null"/> when the rate card is not fully configured -
    /// in which case the plan renders as visibly unpriced rather than falling back to a number
    /// nobody agreed to.
    /// </returns>
    /// <remarks>
    /// Storage is prorated rather than billed in whole blocks. A 25 GiB tier is a block and a
    /// quarter, and rounding it up to two would charge the same for 25 GiB as for 40.
    /// </remarks>
    public decimal? Compute(int cpuPercent, int memoryGb, int diskGb)
    {
        if (Parse(CpuPer100Percent) is not { } cpuRate
            || Parse(MemoryPerGb) is not { } memoryRate
            || Parse(DiskPerBlock) is not { } diskRate
            || DiskBlockGb <= 0)
        {
            return null;
        }

        var total =
            (cpuRate * cpuPercent / 100m)
            + (memoryRate * memoryGb)
            + (diskRate * diskGb / DiskBlockGb);

        return total > 0m ? Math.Round(total, 2, MidpointRounding.AwayFromZero) : null;
    }

    private static decimal? Parse(string raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
        && value >= 0m
            ? value
            : null;
}

/// <summary>
/// The range the custom-build request form accepts, bound from <c>HostingPlans:CustomBuild</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>limits on a form</b>, not a statement about capacity, and they are configuration
/// so that changing what the site is willing to be asked for does not need a deployment.
/// </para>
/// <para>
/// The increments matter: with them set to whole steps, every line of a quote lands on an exact
/// two-decimal figure, so the breakdown a visitor reads adds up to the total above it precisely
/// rather than nearly.
/// </para>
/// </remarks>
public sealed class CustomBuildLimits
{
    /// <summary>Largest memory the form will submit, in GiB.</summary>
    public int MaxMemoryGb { get; set; } = 64;

    /// <summary>Memory increment, in GiB.</summary>
    public int MemoryStepGb { get; set; } = 2;

    /// <summary>
    /// Largest CPU allocation the form will submit, as a percentage of one thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 3600 is the whole processor: the i9-10980XE the infrastructure page names is 18 cores and
    /// 36 threads, and Pterodactyl counts 100% as one thread. A ceiling drawn from real hardware
    /// rather than a round number, and configuration so it moves when the hardware does.
    /// </para>
    /// <para>
    /// Note the unit. A request for "36 cores" is 3600, not 36000 - the latter would be 360
    /// threads, ten times what any node here has.
    /// </para>
    /// </remarks>
    public int MaxCpuPercent { get; set; } = 3600;

    /// <summary>CPU increment, in percent.</summary>
    public int CpuStepPercent { get; set; } = 100;

    /// <summary>Largest storage the form will submit, in GiB.</summary>
    public int MaxDiskGb { get; set; } = 500;

    /// <summary>Storage increment, in GiB.</summary>
    public int DiskStepGb { get; set; } = 10;
}

/// <summary>
/// Commercial values for the plan catalogue, bound from the <c>HostingPlans</c> configuration
/// section.
/// </summary>
/// <remarks>
/// <para>
/// Prices are configuration rather than code on purpose. The resource allocations are a
/// technical decision that belongs in the catalogue; what to charge for them is a commercial one
/// that will change without the plans changing, and it should not need a deployment of new
/// binaries to move.
/// </para>
/// <para>
/// A plan with no rate card and no override renders as visibly unpriced. There is deliberately
/// no fallback number: a default price is a price somebody will read as real.
/// </para>
/// </remarks>
public sealed class HostingPlanPricingOptions
{
    /// <summary>Server-owned regional prices; the catalogue remains denominated in USD.</summary>
    public RegionalPricingOptions RegionalPricing { get; set; } = new();
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HostingPlans";

    /// <summary>Currency symbol used for display.</summary>
    public string CurrencySymbol { get; set; } = "$";

    /// <summary>
    /// ISO 4217 currency code, lower case, as the payment provider wants it. Must describe the
    /// same currency as <see cref="CurrencySymbol"/>; nothing converts between them.
    /// </summary>
    public string CurrencyCode { get; set; } = "usd";

    /// <summary>
    /// Whether a computed price is snapped to the nearest <c>x.99</c>.
    /// </summary>
    /// <remarks>
    /// A commercial choice, so it is a switch rather than an assumption baked into the
    /// arithmetic. It applies to computed prices only - a figure typed into
    /// <see cref="Prices"/> is a decision somebody already made and is used exactly as written.
    /// </remarks>
    public bool CharmPricing { get; set; } = true;

    /// <summary>
    /// Snaps a price to the nearest <c>x.99</c>.
    /// </summary>
    /// <param name="value">The computed price.</param>
    /// <returns>
    /// The same whole unit with 99 cents when the price was at or above the half unit, and the
    /// unit below it otherwise - so 14.70 becomes 14.99 and 17.10 becomes 16.99.
    /// </returns>
    /// <remarks>
    /// Rounding down across the whole unit is the point: it is what makes 17.10 read as sixteen
    /// rather than seventeen. Prices never fall below 0.99, because rounding a small figure down
    /// a whole unit would otherwise produce a negative one.
    /// </remarks>
    public static decimal ToCharmPrice(decimal value)
    {
        var unit = Math.Floor(value);
        var cents = value - unit;

        var snapped = cents >= 0.5m ? unit + 0.99m : unit - 0.01m;

        return Math.Max(snapped, 0.99m);
    }

    /// <summary>Per-unit monthly rates the plan prices are derived from.</summary>
    public PlanRateCard Rates { get; set; } = new();

    /// <summary>What the custom-build request form will accept.</summary>
    public CustomBuildLimits CustomBuild { get; set; } = new();

    /// <summary>
    /// Optional fixed monthly price per plan slug, e.g. <c>"zomboid-4gb": "6.99"</c>. An entry
    /// here overrides the rate card for that tier alone, which is how a promotional or
    /// hand-negotiated price is set without disturbing the rest of the ladder.
    /// </summary>
    public Dictionary<string, string> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the configured price override for a plan slug.
    /// </summary>
    /// <param name="slug">Plan slug to look up.</param>
    /// <returns>The override, or <see langword="null"/> when unset or unparseable.</returns>
    /// <remarks>
    /// Held as a string and parsed invariantly, for the same reason the rate card is: a
    /// configuration provider binding a decimal would use the host's culture, and read
    /// <c>9.99</c> as 999 wherever the decimal separator is a comma.
    /// </remarks>
    public decimal? GetPrice(string slug)
    {
        if (!Prices.TryGetValue(slug, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0m
            ? price
            : null;
    }

    /// <summary>
    /// Settles on the monthly price for a plan: the configured override if there is one, and the
    /// rate card applied to the plan's own resources otherwise.
    /// </summary>
    /// <param name="slug">Plan slug, used to look for an override.</param>
    /// <param name="cpuPercent">Pterodactyl CPU limit as a percentage of one thread.</param>
    /// <param name="memoryGb">Memory in GiB.</param>
    /// <param name="diskGb">Storage in GiB.</param>
    /// <returns>The monthly price, or <see langword="null"/> when the plan is unpriced.</returns>
    public decimal? Resolve(string slug, int cpuPercent, int memoryGb, int diskGb)
    {
        if (GetPrice(slug) is { } configured)
        {
            return configured;
        }

        return Rates.Compute(cpuPercent, memoryGb, diskGb) is { } computed
            ? CharmPricing ? ToCharmPrice(computed) : computed
            : null;
    }
}

/// <summary>Commercial conversion policy, never supplied by the browser.</summary>
public sealed class RegionalPricingOptions
{
    /// <summary>Invariant BRL charged for one catalogue USD.</summary>
    public string BrlPerUsd { get; set; } = "6.50";
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
