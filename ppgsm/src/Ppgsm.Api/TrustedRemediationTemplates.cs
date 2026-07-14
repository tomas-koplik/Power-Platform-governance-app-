using System.Text.Json;
using System.Text.RegularExpressions;
using Ppgsm.Core.Domain;

namespace Ppgsm.Api;

public sealed record TrustedRemediationTemplate(
    string Id,
    int Version,
    IReadOnlySet<string> AllowedParameters,
    Func<IReadOnlyDictionary<string, JsonElement>, string, string> Render);

public interface ITrustedRemediationTemplateCatalog
{
    TrustedRemediationTemplate? Find(string templateId);
}

public sealed class BuiltInTrustedRemediationTemplateCatalog : ITrustedRemediationTemplateCatalog
{
    private static readonly IReadOnlyDictionary<string, TrustedRemediationTemplate> Templates =
        new Dictionary<string, TrustedRemediationTemplate>(StringComparer.Ordinal)
        {
            ["tenant-settings.restrict-production-creation.v1"] = new(
                "tenant-settings.restrict-production-creation.v1", 1, new HashSet<string>(["disabled"], StringComparer.Ordinal),
                (parameters, scope) => $"Set-TenantSettings -TenantId '{Scope(scope)}' -DisableEnvironmentCreationByNonAdminUsers ${Boolean(parameters, "disabled")}")
        };

    public TrustedRemediationTemplate? Find(string templateId) => Templates.GetValueOrDefault(templateId);

    private static string Boolean(IReadOnlyDictionary<string, JsonElement> parameters, string name) =>
        parameters[name].ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new ArgumentException($"Template parameter '{name}' must be boolean.")
        };

    private static string Scope(string scope) => Regex.IsMatch(scope, "^[0-9a-fA-F-]{36}$", RegexOptions.CultureInvariant)
        ? Guid.Parse(scope).ToString("D")
        : throw new ArgumentException("Target scope must be a tenant GUID.");
}

public sealed class TrustedRemediationProposalFactory(
    IPublishedRuleCatalog rules,
    ITrustedRemediationTemplateCatalog templates,
    IGovernanceStore governance,
    TimeProvider timeProvider)
{
    public async ValueTask<RemediationProposal> CreateAsync(
        Guid customerId,
        CreateRemediationProposalRequest request,
        string proposer,
        CancellationToken cancellationToken)
    {
        var published = await rules.GetCurrentAsync(cancellationToken) ?? throw new CapabilityUnavailableException(ApiCapability.Remediation);
        var finding = await governance.FindFindingAsync(customerId, request.SnapshotId, request.FindingId, cancellationToken)
            ?? throw new DomainConflictException("The finding does not belong to the supplied customer and snapshot.");
        var rule = published.Rules.SingleOrDefault(value => value.Id == finding.RuleId)
            ?? throw new DomainConflictException("The finding rule is not present in the published catalog.");
        if (rule.Remediation.Type != RemediationKind.Script || string.IsNullOrWhiteSpace(rule.Remediation.TemplateId))
            throw new CapabilityUnavailableException(ApiCapability.Remediation);
        var template = templates.Find(rule.Remediation.TemplateId) ?? throw new CapabilityUnavailableException(ApiCapability.Remediation);
        if (!string.Equals(request.TemplateId, template.Id, StringComparison.Ordinal))
            throw new DomainConflictException("The requested template is not the published rule template.");
        if (!await governance.EvidenceHashExistsAsync(customerId, request.SnapshotId, request.EvidenceHash, cancellationToken))
            throw new DomainConflictException("The evidence hash is not bound to the supplied snapshot.");
        if (!string.Equals(request.TargetScope, finding.Scope, StringComparison.Ordinal))
            throw new DomainConflictException("The remediation target scope does not match the finding scope.");

        var parameters = request.Parameters.EnumerateObject().ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        if (!parameters.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(template.AllowedParameters))
            throw new ArgumentException("Template parameters do not match the trusted allowlist.", nameof(request));
        var script = template.Render(parameters, request.TargetScope);
        var now = timeProvider.GetUtcNow();
        if (request.EvidenceCapturedAt > now || request.EvidenceValidUntil <= now || request.EvidenceValidUntil <= request.EvidenceCapturedAt)
            throw new StaleEvidenceException();
        return new(Guid.NewGuid(), customerId, finding.FindingId, finding.SnapshotId, script, proposer, now,
            request.EvidenceCapturedAt, request.EvidenceValidUntil, RemediationKind.Script,
            rule.Id, rule.Version, published.Version, template.Id, template.Version, request.EvidenceHash,
            JsonSerializer.Serialize(parameters), request.TargetScope, rule.Remediation.Verification, rule.Remediation.RollbackLimitations);
    }
}