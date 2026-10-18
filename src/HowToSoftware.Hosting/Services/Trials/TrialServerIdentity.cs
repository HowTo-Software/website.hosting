using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HowToSoftware.Hosting.Services.Trials;

/// <summary>Server-controlled display identity; never derives a name from an email or panel login.</summary>
public static class TrialServerIdentity
{
    public const int MaxDisplayNameLength = 80;

    public static string? NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256
            || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return null;
        string normalized;
        try { normalized = Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " "); }
        catch (ArgumentException) { return null; }
        var result = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > MaxDisplayNameLength) break;
            result.Append(rune.ToString());
        }
        return result.Length > 0 ? result.ToString().TrimEnd() : null;
    }

    public static string ServerName(string? displayName) => $"24 Teste {NormalizeDisplayName(displayName) ?? "Jogador"}";

    // Called only after the stable external-id lookup found no existing server. A recovered
    // server is reused without updating its environment or changing its existing credentials.
    public static void SetProjectZomboidPassword(string gameSlug, IDictionary<string, string> environment)
    {
        if (gameSlug == "project-zomboid")
            environment["ADMIN_PASSWORD"] = RandomNumberGenerator.GetString(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 32);
    }
}
