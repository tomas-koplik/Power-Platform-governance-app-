using System.Text.Json;

namespace Ppgsm.Core.Domain;

public sealed record RuleEvidenceRequirement(string Section, string Coverage, string Confidence, IReadOnlyList<string> Paths);
public sealed record RuleApplicability(string Mode, string ProfileKey);
public sealed record RuleEvaluator(string Key, int Version, JsonElement Params);
public sealed record RuleExceptionPolicy(bool Allowed, int? MaximumDays, bool RequiresApprover, string Review);
public sealed record RuleRemediation(RemediationKind Type, string Guidance, string BlastRadius, IReadOnlyList<string> Prerequisites, string Verification, string RollbackLimitations, string? TemplateId = null);
public sealed record RulePocGate(string Status, string Question);

public sealed record RuleDefinition(
    string Id,
    int Version,
    string Title,
    string Area,
    FindingSeverity Severity,
    decimal AreaWeight,
    RuleApplicability Applicability,
    RuleEvaluator Evaluator,
    int MinSnapshotSchema,
    IReadOnlyList<RuleEvidenceRequirement> EvidenceRequirements,
    string Rationale,
    string Recommendation,
    RuleExceptionPolicy ExceptionPolicy,
    RuleRemediation Remediation,
    string DocsUrl,
    RulePocGate PocGate);

public sealed record RuleProfileEntry(string RuleId, string Mode, string Reason);
public sealed record GovernanceProfile(string Id, int Version, IReadOnlyList<RuleProfileEntry> Rules);
public sealed record RuleCatalogDocument(int SchemaVersion, string CatalogVersion, DateTimeOffset PublishedAt, string PublicationAttestation, IReadOnlyList<RuleDefinition> Rules);

public sealed record PublishedRuleSet(
    string Version,
    DateTimeOffset PublishedAt,
    string PublicationAttestation,
    IReadOnlyList<RuleDefinition> Rules,
    GovernanceProfile DefaultProfile,
    string ContentDigest = "legacy",
    string EvaluatorVersionsJson = "{}");

public interface IPublishedRuleCatalog
{
    ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken);
    ValueTask<PublishedRuleSet?> GetByDigestAsync(string contentDigest, CancellationToken cancellationToken);
}