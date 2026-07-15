using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ppgsm.Core.Domain;

public sealed class FilePublishedRuleCatalog(
    string catalogPath,
    string profilePath,
    string trustedVersion,
    string trustedManifestDigest,
    RuleEvaluatorRegistry? evaluatorRegistry = null) : IPublishedRuleCatalog
{
    private static readonly Regex RuleIdPattern = new("^PPG-[A-Z]{2,3}-[0-9]{3}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly RuleEvaluatorRegistry registry = evaluatorRegistry ?? new();
    private readonly IReadOnlySet<string> trustedDigests = trustedManifestDigest.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => TryParseDigest(value, out var digest) ? $"sha256:{digest}" : string.Empty)
        .Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);

    public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) =>
        LoadAsync(catalogPath, profilePath, expectedDigest: null, trustedVersion, cancellationToken);

    public async ValueTask<PublishedRuleSet?> GetByDigestAsync(string contentDigest, CancellationToken cancellationToken)
    {
        if (!trustedDigests.Contains(contentDigest)) return null;
        var current = await LoadAsync(catalogPath, profilePath, contentDigest, expectedVersion: null, cancellationToken);
        if (current is not null) return current;
        var root = Directory.GetParent(Path.GetDirectoryName(catalogPath) ?? string.Empty)?.FullName;
        if (root is null || !Directory.Exists(root)) return null;
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var retained = await LoadAsync(Path.Combine(directory, "catalog.yaml"), Path.Combine(directory, "default-profile.yaml"),
                contentDigest, expectedVersion: null, cancellationToken);
            if (retained is not null) return retained;
        }
        return null;
    }

    private async ValueTask<PublishedRuleSet?> LoadAsync(
        string candidateCatalogPath,
        string candidateProfilePath,
        string? expectedDigest,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = candidateCatalogPath + ".manifest.json";
            if (!File.Exists(candidateCatalogPath) || !File.Exists(candidateProfilePath) || !File.Exists(manifestPath)) return null;
            var catalogBytes = await File.ReadAllBytesAsync(candidateCatalogPath, cancellationToken);
            var profileBytes = await File.ReadAllBytesAsync(candidateProfilePath, cancellationToken);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var contentDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()}";
            if (!trustedDigests.Contains(contentDigest) || expectedDigest is not null && !string.Equals(contentDigest, expectedDigest, StringComparison.Ordinal)) return null;
            var manifest = JsonSerializer.Deserialize<RulePublicationManifest>(manifestBytes, JsonOptions);
            if (manifest is null || manifest.SchemaVersion != 1 || !HashMatches(catalogBytes, manifest.CatalogSha256) || !HashMatches(profileBytes, manifest.ProfileSha256)) return null;
            var catalog = JsonSerializer.Deserialize<RuleCatalogDocument>(catalogBytes, JsonOptions);
            var profile = JsonSerializer.Deserialize<GovernanceProfile>(profileBytes, JsonOptions);
            return IsValid(catalog, profile, expectedVersion)
                ? new(catalog!.CatalogVersion, catalog.PublishedAt, catalog.PublicationAttestation, catalog.Rules, profile!,
                    contentDigest, JsonSerializer.Serialize(catalog.Rules.OrderBy(value => value.Evaluator.Key, StringComparer.Ordinal)
                        .ToDictionary(value => value.Evaluator.Key, value => value.Evaluator.Version, StringComparer.Ordinal)))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    private bool IsValid(RuleCatalogDocument? catalog, GovernanceProfile? profile, string? expectedVersion)
    {
        if (catalog is null || profile is null || catalog.SchemaVersion != 1 || expectedVersion is not null && catalog.CatalogVersion != expectedVersion ||
            catalog.Rules.Count == 0 || profile.Id != "default" || profile.Version < 1) return false;
        if (catalog.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Rules.Count) return false;
        if (profile.Rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != profile.Rules.Count) return false;
        if (profile.Rules.Any(entry => !catalog.Rules.Any(rule => rule.Id == entry.RuleId) || entry.Mode is not ("enabled" or "disabled" or "advisory"))) return false;
        return catalog.Rules.All(IsValidRule) && catalog.Rules.All(rule => profile.Rules.Any(entry => entry.RuleId == rule.Id));
    }

    private bool IsValidRule(RuleDefinition rule) =>
        RuleIdPattern.IsMatch(rule.Id) && rule.Version > 0 && !string.IsNullOrWhiteSpace(rule.Title) &&
        !string.IsNullOrWhiteSpace(rule.Area) && rule.AreaWeight > 0 && rule.MinSnapshotSchema > 0 &&
        !string.IsNullOrWhiteSpace(rule.Applicability.Mode) && !string.IsNullOrWhiteSpace(rule.Applicability.ProfileKey) &&
        !string.IsNullOrWhiteSpace(rule.Evaluator.Key) && rule.Evaluator.Version > 0 && registry.Supports(rule.Evaluator) &&
        rule.Evaluator.Params.ValueKind == JsonValueKind.Object &&
        rule.EvidenceRequirements.Count > 0 && rule.EvidenceRequirements.All(evidence =>
            SectionKeys.Canonical.Contains(SectionKeys.Canonicalize(evidence.Section)) && evidence.Coverage is "Full" &&
            Enum.TryParse<EvidenceConfidence>(evidence.Confidence, out _) && evidence.Paths.Count > 0) &&
        !string.IsNullOrWhiteSpace(rule.Rationale) && !string.IsNullOrWhiteSpace(rule.Recommendation) &&
        !string.IsNullOrWhiteSpace(rule.Remediation.Guidance) && !string.IsNullOrWhiteSpace(rule.Remediation.BlastRadius) &&
        rule.Remediation.Prerequisites.Count > 0 && !string.IsNullOrWhiteSpace(rule.Remediation.Verification) &&
        !string.IsNullOrWhiteSpace(rule.Remediation.RollbackLimitations) &&
        Uri.TryCreate(rule.DocsUrl, UriKind.Absolute, out var docsUri) && docsUri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(rule.PocGate.Status) && !string.IsNullOrWhiteSpace(rule.PocGate.Question);

    private static bool TryParseDigest(string value, out string digest)
    {
        digest = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..].ToLowerInvariant() : string.Empty;
        return digest.Length == 64 && digest.All(Uri.IsHexDigit);
    }

    private static bool HashMatches(byte[] content, string expected)
    {
        if (!TryParseDigest(expected, out var digest)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(digest));
    }

    private sealed record RulePublicationManifest(int SchemaVersion, string CatalogSha256, string ProfileSha256);
}