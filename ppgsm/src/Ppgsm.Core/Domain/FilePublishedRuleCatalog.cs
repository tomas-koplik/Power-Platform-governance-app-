using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ppgsm.Core.Domain;

public sealed class FilePublishedRuleCatalog(
    string catalogPath,
    string profilePath,
    string trustedVersion,
    string trustedAttestation,
    RuleEvaluatorRegistry? evaluatorRegistry = null) : IPublishedRuleCatalog
{
    private static readonly Regex RuleIdPattern = new("^PPG-[A-Z]{2,3}-[0-9]{3}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly RuleEvaluatorRegistry registry = evaluatorRegistry ?? new();

    public async ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(catalogPath) || !File.Exists(profilePath) || string.IsNullOrWhiteSpace(trustedAttestation)) return null;
            await using var catalogStream = File.OpenRead(catalogPath);
            await using var profileStream = File.OpenRead(profilePath);
            var catalog = await JsonSerializer.DeserializeAsync<RuleCatalogDocument>(catalogStream, JsonOptions, cancellationToken);
            var profile = await JsonSerializer.DeserializeAsync<GovernanceProfile>(profileStream, JsonOptions, cancellationToken);
            return IsValid(catalog, profile)
                ? new(catalog!.CatalogVersion, catalog.PublishedAt, catalog.PublicationAttestation, catalog.Rules, profile!)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    private bool IsValid(RuleCatalogDocument? catalog, GovernanceProfile? profile)
    {
        if (catalog is null || profile is null || catalog.SchemaVersion != 1 || catalog.CatalogVersion != trustedVersion ||
            catalog.PublicationAttestation != trustedAttestation || catalog.Rules.Count == 0 || profile.Id != "default" || profile.Version < 1) return false;
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
}