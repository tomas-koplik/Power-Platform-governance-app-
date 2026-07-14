using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ppgsm.Core.Domain;

public sealed record EvaluationEvidenceSection(
    string SectionKey,
    SectionCoverage Coverage,
    EvidenceConfidence Confidence,
    JsonElement Data,
    IReadOnlyCollection<Guid> EvidenceReferenceIds);

public sealed record RuleEvaluationContext(
    Guid CustomerId,
    Guid SnapshotId,
    int SnapshotSchemaVersion,
    PublishedRuleSet RuleSet,
    IReadOnlyDictionary<string, EvaluationEvidenceSection> Sections,
    IReadOnlySet<string>? PocValidatedRuleIds = null,
    IReadOnlySet<string>? ActiveExceptionRuleIds = null);

public sealed record EvaluatorResult(FindingStatus Status, decimal Ratio, string Scope, string Observed);

public interface IRuleEvaluator
{
    string Key { get; }
    int Version { get; }
    EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections);
}

public sealed class RuleEvaluatorRegistry
{
    private readonly IReadOnlyDictionary<string, IRuleEvaluator> evaluators;

    public RuleEvaluatorRegistry(IEnumerable<IRuleEvaluator>? evaluators = null)
    {
        var registered = (evaluators ?? BuiltIns()).ToArray();
        if (registered.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != registered.Length)
            throw new InvalidOperationException("Evaluator keys must be unique.");
        this.evaluators = registered.ToDictionary(value => value.Key, StringComparer.Ordinal);
    }

    public bool Supports(RuleEvaluator evaluator) =>
        evaluators.TryGetValue(evaluator.Key, out var implementation) && implementation.Version == evaluator.Version;

    public IRuleEvaluator Resolve(RuleEvaluator evaluator) => Supports(evaluator)
        ? evaluators[evaluator.Key]
        : throw new InvalidOperationException($"Evaluator '{evaluator.Key}' version {evaluator.Version} is not registered.");

    public IReadOnlyDictionary<string, int> Versions => evaluators.ToDictionary(value => value.Key, value => value.Value.Version, StringComparer.Ordinal);

    private static IEnumerable<IRuleEvaluator> BuiltIns() =>
    [
        new TenantSettingEqualsEvaluator(), new TenantSettingInEvaluator(), new DlpCoverageEvaluator(),
        new ProductionManagedEvaluator(), new EveryoneInDefaultEvaluator(), new OrphanResourceEvaluator()
    ];
}

public static class EvidenceCoverageAggregator
{
    public static SectionCoverage Aggregate(IEnumerable<SectionCoverage> required)
    {
        var values = required.ToArray();
        if (values.Length == 0 || values.All(value => value == SectionCoverage.Skipped)) return SectionCoverage.Skipped;
        if (values.All(value => value == SectionCoverage.Full)) return SectionCoverage.Full;
        if (values.Any(value => value is SectionCoverage.Full or SectionCoverage.Partial)) return SectionCoverage.Partial;
        return SectionCoverage.Failed;
    }
}

public sealed class RuleEvaluationRuntime(RuleEvaluatorRegistry registry)
{
    public IReadOnlyList<Finding> Evaluate(RuleEvaluationContext context)
    {
        return context.RuleSet.Rules.OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(rule => EvaluateRule(context, rule, Profile(context.RuleSet.DefaultProfile, rule.Id)))
            .ToArray();
    }

    private Finding EvaluateRule(RuleEvaluationContext context, RuleDefinition rule, RuleProfileEntry profile)
    {
        var canonicalSections = context.Sections.Values.ToDictionary(
            value => SectionKeys.Canonicalize(value.SectionKey), value => value, StringComparer.Ordinal);
        var links = rule.EvidenceRequirements
            .SelectMany(requirement => canonicalSections.TryGetValue(SectionKeys.Canonicalize(requirement.Section), out var section)
                ? section.EvidenceReferenceIds : [])
            .Distinct().Order().ToArray();
        var status = FindingStatus.NotEvaluated;
        var ratio = 0m;
        var scope = "Tenant";
        var observed = "Rule was not evaluated.";

        if (!registry.Supports(rule.Evaluator))
        {
            observed = $"Evaluator {rule.Evaluator.Key} version {rule.Evaluator.Version} is unavailable.";
        }
        else if (context.SnapshotSchemaVersion < rule.MinSnapshotSchema)
        {
            observed = $"Snapshot schema {context.SnapshotSchemaVersion} is below required schema {rule.MinSnapshotSchema}.";
        }
        else if (profile.Mode is "disabled" or "advisory")
        {
            status = FindingStatus.NotApplicable;
            observed = $"Rule profile mode is {profile.Mode}: {profile.Reason}";
        }
        else if (!IsApplicable(rule, canonicalSections, out var applicabilityReason))
        {
            status = FindingStatus.NotApplicable;
            observed = applicabilityReason;
        }
        else if (rule.PocGate.Status is "PocRequired" or "Blocked" && context.PocValidatedRuleIds?.Contains(rule.Id) != true)
        {
            observed = $"PoC gate remains {rule.PocGate.Status}: {rule.PocGate.Question}";
        }
        else
        {
            var gate = GateEvidence(rule, canonicalSections);
            if (gate is not null)
            {
                observed = gate;
            }
            else
            {
                var result = registry.Resolve(rule.Evaluator).Evaluate(rule.Evaluator.Params, canonicalSections);
                status = result.Status;
                ratio = result.Ratio;
                scope = result.Scope;
                observed = result.Observed;
            }
        }

        if (context.ActiveExceptionRuleIds?.Contains(rule.Id) == true && status is FindingStatus.Fail or FindingStatus.Partial)
            status = FindingStatus.Excepted;

        return new(
            DeterministicFindingId(context.CustomerId, context.SnapshotId, rule.Id, rule.Version, context.RuleSet.Version, rule.Evaluator.Version),
            context.CustomerId, context.SnapshotId, rule.Id, rule.Title, rule.Area, rule.Severity, status, scope, observed,
            rule.Rationale, rule.Recommendation, rule.Remediation.Type, AreaWeight: rule.AreaWeight,
            EvaluatorRatio: ratio, RuleVersion: rule.Version, CatalogVersion: context.RuleSet.Version,
            EvaluatorKey: rule.Evaluator.Key, EvaluatorVersion: rule.Evaluator.Version,
            EvidenceLinksJson: JsonSerializer.Serialize(links),
            PublicationContentDigest: context.RuleSet.ContentDigest,
            EvaluatorVersionsJson: context.RuleSet.EvaluatorVersionsJson);
    }

    private static bool IsApplicable(
        RuleDefinition rule,
        IReadOnlyDictionary<string, EvaluationEvidenceSection> sections,
        out string reason)
    {
        reason = "Rule is applicable.";
        if (rule.Applicability.Mode is "always" or "profile" or "customerProfile") return true;
        if (rule.Applicability.Mode == "resource-present")
        {
            var key = SectionKeys.Canonicalize(rule.Applicability.ProfileKey);
            var present = sections.TryGetValue(key, out var section) && section.Data.ValueKind switch
            {
                JsonValueKind.Array => section.Data.GetArrayLength() > 0,
                JsonValueKind.Object => section.Data.EnumerateObject().Any(),
                _ => false
            };
            reason = present ? reason : $"Applicability resource '{key}' is not present.";
            return present;
        }
        reason = $"Applicability mode '{rule.Applicability.Mode}' is unsupported.";
        return false;
    }

    private static string? GateEvidence(RuleDefinition rule, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        foreach (var requirement in rule.EvidenceRequirements)
        {
            var key = SectionKeys.Canonicalize(requirement.Section);
            if (!sections.TryGetValue(key, out var section)) return $"Required evidence section '{key}' was not collected.";
            if (!Enum.TryParse<SectionCoverage>(requirement.Coverage, out var requiredCoverage) ||
                requiredCoverage == SectionCoverage.Full && section.Coverage != SectionCoverage.Full)
                return $"Required evidence section '{key}' has {section.Coverage} coverage; Full is required.";
            if (!Enum.TryParse<EvidenceConfidence>(requirement.Confidence, out var confidence) || section.Confidence != confidence)
                return $"Required evidence section '{key}' has {section.Confidence} confidence; {requirement.Confidence} is required.";
            foreach (var path in requirement.Paths)
                if (!EvidenceJson.PathExists(sections, path)) return $"Required evidence path '{path}' is absent.";
        }
        return null;
    }

    private static RuleProfileEntry Profile(GovernanceProfile profile, string ruleId) =>
        profile.Rules.Single(value => value.RuleId == ruleId);

    private static Guid DeterministicFindingId(Guid customerId, Guid snapshotId, string ruleId, int ruleVersion, string catalogVersion, int evaluatorVersion)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{customerId:D}|{snapshotId:D}|{ruleId}|{ruleVersion}|{catalogVersion}|{evaluatorVersion}"));
        return new Guid(hash[..16]);
    }
}

internal static class EvidenceJson
{
    public static bool PathExists(IReadOnlyDictionary<string, EvaluationEvidenceSection> sections, string path)
    {
        var parts = path.Replace("[*]", "", StringComparison.Ordinal).Split('.');
        if (!sections.TryGetValue(SectionKeys.Canonicalize(parts[0]), out var section)) return false;
        return Exists(section.Data, parts.AsSpan(1));
    }

    public static JsonElement? Value(IReadOnlyDictionary<string, EvaluationEvidenceSection> sections, string path)
    {
        var parts = path.Split('.');
        if (!sections.TryGetValue(SectionKeys.Canonicalize(parts[0]), out var section)) return null;
        var current = section.Data;
        foreach (var part in parts.Skip(1))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current;
    }

    public static IEnumerable<JsonElement> Items(IReadOnlyDictionary<string, EvaluationEvidenceSection> sections, string section) =>
        sections.TryGetValue(section, out var value) && value.Data.ValueKind == JsonValueKind.Array ? value.Data.EnumerateArray() : [];

    private static bool Exists(JsonElement current, ReadOnlySpan<string> parts)
    {
        if (parts.Length == 0) return current.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (current.ValueKind == JsonValueKind.Array) return current.EnumerateArray().Any(item => Exists(item, parts));
        return current.ValueKind == JsonValueKind.Object && current.TryGetProperty(parts[0], out var child) && Exists(child, parts[1..]);
    }
}

internal sealed class TenantSettingEqualsEvaluator : IRuleEvaluator
{
    public string Key => "tenant.settingEquals";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var path = parameters.GetProperty("path").GetString()!;
        var expected = parameters.GetProperty("expected");
        var actual = EvidenceJson.Value(sections, path)!.Value;
        var pass = JsonElement.DeepEquals(actual, expected);
        return new(pass ? FindingStatus.Pass : FindingStatus.Fail, pass ? 1m : 0m, "Tenant", $"{path} is {actual.GetRawText()}; expected {expected.GetRawText()}.");
    }
}

internal sealed class TenantSettingInEvaluator : IRuleEvaluator
{
    public string Key => "tenant.settingIn";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var configured = parameters.TryGetProperty("result", out var result) && Enum.TryParse<FindingStatus>(result.GetString(), out var forced) ? forced : (FindingStatus?)null;
        if (configured is not null) return new(configured.Value, 0m, "Tenant", "Observed AI setting requires customer review.");
        var actual = EvidenceJson.Value(sections, parameters.GetProperty("path").GetString()!)!.Value;
        var pass = parameters.GetProperty("acceptedValues").EnumerateArray().Any(value => JsonElement.DeepEquals(value, actual));
        return new(pass ? FindingStatus.Pass : FindingStatus.Fail, pass ? 1m : 0m, "Tenant", $"Observed value is {actual.GetRawText()}.");
    }
}

internal sealed class DlpCoverageEvaluator : IRuleEvaluator
{
    public string Key => "dlp.coverage";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var environments = EvidenceJson.Items(sections, SectionKeys.Environments).Select(value => value.GetProperty("id").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (environments.Count == 0) return new(FindingStatus.NotApplicable, 0m, "Tenant", "No environments were discovered.");
        var covered = EvidenceJson.Items(sections, SectionKeys.DlpPolicies)
            .SelectMany(policy => policy.TryGetProperty("environments", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray() : [])
            .Select(value => value.ValueKind == JsonValueKind.Object ? value.GetProperty("id").GetString() : value.GetString())
            .Where(value => value is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ratio = environments.Count(value => covered.Contains(value)) / (decimal)environments.Count;
        var minimum = parameters.GetProperty("minimumCoverage").GetDecimal();
        var status = ratio >= minimum ? FindingStatus.Pass : ratio > 0m ? FindingStatus.Partial : FindingStatus.Fail;
        return new(status, ratio, "Tenant", $"{covered.Count(value => environments.Contains(value))} of {environments.Count} environments are covered by an observed DLP policy ({ratio:P0}).");
    }
}

internal sealed class ProductionManagedEvaluator : IRuleEvaluator
{
    public string Key => "env.allProductionManaged";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var production = EvidenceJson.Items(sections, SectionKeys.Environments)
            .Where(value => string.Equals(value.GetProperty("environmentType").GetString(), "Production", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (production.Length == 0) return new(FindingStatus.NotApplicable, 0m, "Tenant", "No production environments were discovered.");
        var managed = production.Count(value => value.GetProperty("governanceConfiguration").GetProperty("protectionLevel").GetString() == parameters.GetProperty("requiredState").GetString());
        var ratio = managed / (decimal)production.Length;
        return new(ratio == 1m ? FindingStatus.Pass : ratio > 0m ? FindingStatus.Partial : FindingStatus.Fail, ratio, "Production environments", $"{managed} of {production.Length} production environments have the required managed state.");
    }
}

internal sealed class EveryoneInDefaultEvaluator : IRuleEvaluator
{
    public string Key => "sharing.everyoneInDefault";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var defaults = EvidenceJson.Items(sections, SectionKeys.Environments).Where(value => value.GetProperty("isDefault").GetBoolean()).Select(value => value.GetProperty("id").GetString()).ToHashSet();
        var broadTypes = parameters.GetProperty("principalTypes").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = new[] { SectionKeys.Apps, SectionKeys.Flows }.SelectMany(section => EvidenceJson.Items(sections, section))
            .Where(value => defaults.Contains(value.GetProperty("environmentId").GetString()))
            .SelectMany(value => value.GetProperty("roleAssignments").EnumerateArray())
            .Count(value => broadTypes.Contains(value.GetProperty("principalType").GetString()));
        var maximum = parameters.GetProperty("maximumAssignments").GetInt32();
        return new(assignments <= maximum ? FindingStatus.Pass : FindingStatus.Fail, assignments <= maximum ? 1m : 0m, "Default environment", $"Observed {assignments} broad role assignments in the default environment.");
    }
}

internal sealed class OrphanResourceEvaluator : IRuleEvaluator
{
    public string Key => "resources.orphanCount";
    public int Version => 1;
    public EvaluatorResult Evaluate(JsonElement parameters, IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var owners = EvidenceJson.Items(sections, SectionKeys.OwnerDirectory).ToDictionary(value => value.GetProperty("objectId").GetString()!, value => value.GetProperty("status").GetString()!, StringComparer.OrdinalIgnoreCase);
        var ownerIds = new[] { SectionKeys.Apps, SectionKeys.Flows }.SelectMany(section => EvidenceJson.Items(sections, section)).Select(value => value.GetProperty("ownerObjectId").GetString()!).ToArray();
        var unknown = parameters.GetProperty("unknownStates").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ownerIds.Any(id => !owners.TryGetValue(id, out var state) || unknown.Contains(state))) return new(FindingStatus.NotEvaluated, 0m, "Resources", "At least one owner lookup is unresolved.");
        var orphanStates = parameters.GetProperty("orphanStates").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = ownerIds.Count(id => orphanStates.Contains(owners[id]));
        var maximum = parameters.GetProperty("maximumOrphans").GetInt32();
        return new(count <= maximum ? FindingStatus.Pass : FindingStatus.Fail, count <= maximum ? 1m : 0m, "Resources", $"Observed {count} resources with disabled or missing owners.");
    }
}