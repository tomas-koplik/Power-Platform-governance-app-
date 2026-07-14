using System.Text.Json.Serialization;

namespace Ppgsm.Core.Domain;

public enum FindingSeverity { Critical, High, Medium, Low, Informational }
public enum FindingStatus { Pass, Fail, Partial, NotEvaluated, Excepted, NotApplicable }
public enum RemediationKind { Script, Manual, Informational }
public enum ExportJobStatus { Queued, Running, Completed, Failed }
public enum ExportFormat { Pdf, Xlsx, Json }
public enum RemediationProposalStatus { Proposed, Approved, Rejected }

public sealed record Finding(
    Guid FindingId,
    Guid CustomerId,
    Guid SnapshotId,
    string RuleId,
    string Title,
    string Area,
    FindingSeverity Severity,
    FindingStatus Status,
    string Scope,
    string Observed,
    string Interpretation,
    string ProposedAction,
    RemediationKind Remediation,
    string? OwnerUpn = null,
    decimal AreaWeight = 1m,
    decimal ApplicabilityWeight = 1m,
    decimal EvaluatorRatio = 0.5m,
    int RuleVersion = 1,
    string CatalogVersion = "unversioned",
    string EvaluatorKey = "legacy",
    int EvaluatorVersion = 1,
    string EvidenceLinksJson = "[]");

public sealed record GovernanceScore(
    Guid CustomerId,
    Guid SnapshotId,
    int Overall,
    string Tier,
    int Evaluated,
    int Total,
    SectionCoverage Confidence,
    IReadOnlyDictionary<string, int> Areas);

public static class GovernanceScoring
{
    /// <summary>
    /// Calculates a deterministic weighted score. Severity weights are Critical=10, High=6,
    /// Medium=3, Low=1, Informational=0. Rule weight is severity * area * applicability.
    /// Pass contributes 1, Fail 0, and Partial the evaluator ratio. Informational,
    /// NotEvaluated, NotApplicable, and Excepted findings are excluded from the denominator.
    /// Any included failed Critical finding caps the score at 59. Tiers are Excellent 90-100,
    /// Good 75-89, Needs Attention 60-74, and At Risk 0-59.
    /// </summary>
    public static GovernanceScore Calculate(Guid customerId, Guid snapshotId, IReadOnlyCollection<Finding> findings, SectionCoverage confidence)
    {
        if (findings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId))
        {
            throw new TenantAccessDeniedException();
        }

        var included = findings.Where(value =>
            value.Severity != FindingSeverity.Informational &&
            value.Status is not FindingStatus.NotEvaluated and not FindingStatus.NotApplicable and not FindingStatus.Excepted).ToArray();
        ValidateWeights(included);
        if (included.Length == 0)
        {
            return new(customerId, snapshotId, 0, "Not evaluated", 0, findings.Count, confidence, new Dictionary<string, int>());
        }
        var score = WeightedScore(included);
        if (included.Any(value => value.Severity == FindingSeverity.Critical && value.Status == FindingStatus.Fail)) score = Math.Min(score, 59);
        var areas = included.GroupBy(value => value.Area).ToDictionary(
            group => group.Key,
            group => WeightedScore(group));
        var tier = score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 60 ? "Needs Attention" : "At Risk";
        return new(customerId, snapshotId, score, tier, included.Length, findings.Count, confidence, areas);
    }

    private static int WeightedScore(IEnumerable<Finding> findings)
    {
        var weighted = findings.Select(value => (Weight: Weight(value), Ratio: Ratio(value))).ToArray();
        var denominator = weighted.Sum(value => value.Weight);
        return denominator == 0m ? 0 : (int)Math.Round(weighted.Sum(value => value.Weight * value.Ratio) * 100m / denominator, MidpointRounding.AwayFromZero);
    }

    private static decimal Weight(Finding finding) => SeverityWeight(finding.Severity) * finding.AreaWeight * finding.ApplicabilityWeight;

    private static decimal Ratio(Finding finding) => finding.Status switch
    {
        FindingStatus.Pass => 1m,
        FindingStatus.Partial => finding.EvaluatorRatio,
        _ => 0m
    };

    private static decimal SeverityWeight(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 10m,
        FindingSeverity.High => 6m,
        FindingSeverity.Medium => 3m,
        FindingSeverity.Low => 1m,
        FindingSeverity.Informational => 0m,
        _ => throw new ArgumentOutOfRangeException(nameof(severity))
    };

    private static void ValidateWeights(IEnumerable<Finding> findings)
    {
        if (findings.Any(value => value.AreaWeight < 0m || value.ApplicabilityWeight < 0m ||
                                  value.EvaluatorRatio is < 0m or > 1m))
        {
            throw new ArgumentOutOfRangeException(nameof(findings), "Scoring weights must be non-negative and evaluator ratios must be between zero and one.");
        }
    }
}

public sealed record GovernanceException(
    Guid ExceptionId,
    Guid CustomerId,
    Guid FindingId,
    string Reason,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsActive(DateTimeOffset now) => now < ExpiresAt;
}

public sealed record SnapshotComparison(
    Guid CustomerId,
    Guid BaselineSnapshotId,
    Guid CurrentSnapshotId,
    int AddedFindings,
    int ResolvedFindings,
    int ChangedFindings);

public static class SnapshotComparisonGuard
{
    public static void EnsureSameTenant(Snapshot baseline, Snapshot current)
    {
        if (baseline.CustomerId != current.CustomerId) throw new TenantAccessDeniedException();
    }
}

public sealed record ExportJob(
    Guid ExportJobId,
    Guid CustomerId,
    ExportFormat Format,
    ExportJobStatus Status,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore]
    string RequestedBy,
    string? DownloadUrl = null,
    string? FailureReason = null,
    Guid SnapshotId = default,
    bool IncludesPii = false,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DownloadExpiresAt = null,
    string? ArtifactContentHash = null,
    long? ArtifactContentLength = null,
    string? ArtifactMediaType = null,
    string? ArtifactStorageETag = null);

public sealed class RemediationProposal
{
    public RemediationProposal(
        Guid proposalId,
        Guid customerId,
        Guid findingId,
        Guid snapshotId,
        string script,
        string proposedBy,
        DateTimeOffset proposedAt,
        DateTimeOffset evidenceCapturedAt,
        DateTimeOffset evidenceValidUntil,
        RemediationKind kind,
        string ruleId = "legacy",
        int ruleVersion = 0,
        string catalogVersion = "legacy",
        string templateId = "legacy",
        int templateVersion = 0,
        string evidenceHash = "legacy",
        string parametersJson = "{}",
        string targetScope = "legacy",
        string verification = "legacy",
        string rollback = "legacy")
    {
        if (kind != RemediationKind.Script) throw new DomainConflictException("Only review-first scripts can be proposed for remediation.");
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("A generated script is required.", nameof(script));
        ProposalId = proposalId;
        CustomerId = customerId;
        FindingId = findingId;
        SnapshotId = snapshotId;
        Script = script;
        ProposedBy = proposedBy;
        ProposedAt = proposedAt;
        EvidenceCapturedAt = evidenceCapturedAt;
        EvidenceValidUntil = evidenceValidUntil;
        Kind = kind;
        RuleId = ruleId;
        RuleVersion = ruleVersion;
        CatalogVersion = catalogVersion;
        TemplateId = templateId;
        TemplateVersion = templateVersion;
        EvidenceHash = evidenceHash;
        ParametersJson = parametersJson;
        TargetScope = targetScope;
        Verification = verification;
        Rollback = rollback;
        Status = RemediationProposalStatus.Proposed;
    }

    public Guid ProposalId { get; }
    public Guid CustomerId { get; }
    public Guid FindingId { get; }
    public Guid SnapshotId { get; }
    public string Script { get; }
    public string ProposedBy { get; }
    public DateTimeOffset ProposedAt { get; }
    public DateTimeOffset EvidenceCapturedAt { get; }
    public DateTimeOffset EvidenceValidUntil { get; }
    public RemediationKind Kind { get; }
    public string RuleId { get; }
    public int RuleVersion { get; }
    public string CatalogVersion { get; }
    public string TemplateId { get; }
    public int TemplateVersion { get; }
    public string EvidenceHash { get; }
    public string ParametersJson { get; }
    public string TargetScope { get; }
    public string Verification { get; }
    public string Rollback { get; }
    public RemediationProposalStatus Status { get; private set; }
    public string? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReviewReason { get; private set; }

    public void Approve(string approver, DateTimeOffset now, Guid latestSnapshotId)
    {
        if (Status != RemediationProposalStatus.Proposed) throw new DomainConflictException("Only proposed remediation can be approved.");
        if (string.Equals(ProposedBy, approver, StringComparison.OrdinalIgnoreCase)) throw new DomainConflictException("The proposer cannot approve their own remediation.");
        if (now >= EvidenceValidUntil || latestSnapshotId != SnapshotId) throw new StaleEvidenceException();
        Status = RemediationProposalStatus.Approved;
        ReviewedBy = approver;
        ReviewedAt = now;
    }

    public void Reject(string approver, string reason, DateTimeOffset now)
    {
        if (Status != RemediationProposalStatus.Proposed) throw new DomainConflictException("Only proposed remediation can be rejected.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        Status = RemediationProposalStatus.Rejected;
        ReviewedBy = approver;
        ReviewedAt = now;
        ReviewReason = reason;
    }
}

public sealed class StaleEvidenceException : InvalidOperationException
{
    public StaleEvidenceException() : base("The remediation evidence is stale or has been superseded by a newer snapshot.") { }
}
