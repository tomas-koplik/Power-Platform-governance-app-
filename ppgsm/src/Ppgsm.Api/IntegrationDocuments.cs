using Ppgsm.Core.Domain;

namespace Ppgsm.Api;

public sealed class ConsentDocumentService(
    IConfiguration configuration,
    ICustomerStore customers,
    ITenantConnectionStore connections)
{
    public async ValueTask<ConsentDocumentResponse?> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindCustomerAsync(customerId, cancellationToken);
        var connection = await connections.FindAsync(customerId, cancellationToken);
        if (customer is null || connection is null) return null;
        var scopes = configuration.GetSection("Onboarding:Verification:ExpectedDelegatedScopes").Get<string[]>() ?? [];
        var categories = configuration.GetSection("GovernanceDocument:DataCategories").Get<string[]>() ?? [];
        var retention = configuration["GovernanceDocument:Retention"];
        var revocation = configuration["GovernanceDocument:Revocation"];
        if (scopes.Length == 0 || categories.Length == 0 || string.IsNullOrWhiteSpace(retention) || string.IsNullOrWhiteSpace(revocation))
            throw new CapabilityUnavailableException(ApiCapability.Onboarding);
        return new(customerId, customer.EntraTenantId, connection.Status, scopes, categories, retention,
            customer.Region, revocation, await connections.ListCapabilitiesAsync(customerId, cancellationToken), connection.LastValidatedAt);
    }
}

public sealed class RemediationEligibilityService(
    IPublishedRuleCatalog rules,
    ITrustedRemediationTemplateCatalog templates,
    IGovernanceStore governance,
    TimeProvider timeProvider)
{
    public async ValueTask<RemediationEligibilityResponse> GetAsync(
        Guid customerId,
        Guid snapshotId,
        Guid findingId,
        string evidenceHash,
        DateTimeOffset evidenceCapturedAt,
        DateTimeOffset evidenceValidUntil,
        CancellationToken cancellationToken)
    {
        var finding = await governance.FindFindingAsync(customerId, snapshotId, findingId, cancellationToken);
        var published = await rules.GetCurrentAsync(cancellationToken);
        var rule = finding is null || published is null ? null : published.Rules.SingleOrDefault(value => value.Id == finding.RuleId);
        var template = rule?.Remediation.TemplateId is null ? null : templates.Find(rule.Remediation.TemplateId);
        var hashBound = await governance.EvidenceHashExistsAsync(customerId, snapshotId, evidenceHash, cancellationToken);
        var reason = finding is null ? "Finding is not bound to this tenant and snapshot."
            : published is null ? "No trusted published rule catalog is available."
            : rule is null ? "Finding rule is not in the trusted published catalog."
            : rule.Remediation.Type != RemediationKind.Script ? "Published remediation is manual or informational."
            : template is null ? "Published remediation template is unavailable."
            : !hashBound ? "Evidence hash is not bound to this snapshot."
            : evidenceCapturedAt > timeProvider.GetUtcNow() || evidenceValidUntil <= timeProvider.GetUtcNow() || evidenceValidUntil <= evidenceCapturedAt
                ? "Evidence validity window is invalid or expired."
                : null;
        return new(reason is null, reason, findingId, snapshotId, rule?.Id, rule?.Version, published?.Version,
            template?.Id, template?.Version, template?.AllowedParameters.Order().ToArray() ?? [], evidenceCapturedAt,
            evidenceValidUntil, finding?.Scope ?? string.Empty, rule?.Remediation.Verification ?? string.Empty,
            rule?.Remediation.RollbackLimitations ?? string.Empty);
    }
}