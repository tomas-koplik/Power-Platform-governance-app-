using System.Text.Json;

namespace Ppgsm.Core.Domain;

public sealed class SnapshotEvaluationService(
    IPublishedRuleCatalog catalog,
    IEvaluationEvidenceStore evidenceStore,
    IGovernanceStore governanceStore,
    RuleEvaluationRuntime runtime)
{
    public async ValueTask<bool> EvaluateAndPersistAsync(
        Guid customerId,
        Guid snapshotId,
        int snapshotSchemaVersion,
        IReadOnlyCollection<SnapshotSection> completedSections,
        IReadOnlyCollection<string>? requestedEnvironmentIds,
        CancellationToken cancellationToken)
    {
        var payloads = await evidenceStore.LoadEvaluationEvidenceAsync(customerId, snapshotId, cancellationToken);
        var sectionEvidence = BuildSections(completedSections, payloads);
        var discovered = DiscoverEnvironmentIds(sectionEvidence);
        var environmentCoverage = completedSections.FirstOrDefault(value => SectionKeys.Canonicalize(value.SectionKey) == SectionKeys.Environments)?.Coverage;
        await evidenceStore.SaveEnvironmentScopeAsync(new(snapshotId, customerId,
            JsonSerializer.Serialize(requestedEnvironmentIds ?? []), JsonSerializer.Serialize(discovered),
            environmentCoverage == SectionCoverage.Full, DateTimeOffset.UtcNow), cancellationToken);
        await evidenceStore.SaveNormalizedEvidenceAsync(customerId, snapshotId,
            CreateProjection(customerId, snapshotId, sectionEvidence), cancellationToken);
        var published = await catalog.GetCurrentAsync(cancellationToken);
        if (published is null) return false;
        var findings = runtime.Evaluate(new(customerId, snapshotId, snapshotSchemaVersion, published, sectionEvidence));
        await governanceStore.ReplaceFindingsAsync(customerId, snapshotId, findings, cancellationToken);
        return true;
    }

    private static NormalizedEvidenceProjection CreateProjection(
        Guid customerId,
        Guid snapshotId,
        IReadOnlyDictionary<string, EvaluationEvidenceSection> sections)
    {
        var settings = new List<TenantSettingsEvidence>();
        if (sections.TryGetValue(SectionKeys.TenantSettings, out var tenantSettings) &&
            tenantSettings.Data.ValueKind == JsonValueKind.Object && tenantSettings.EvidenceReferenceIds.FirstOrDefault() is var settingsEvidenceId &&
            settingsEvidenceId != Guid.Empty)
        {
            settings.Add(new(Guid.NewGuid(), customerId, snapshotId,
                ReadBoolean(tenantSettings.Data, "trialEnvironmentsDisabled"),
                ReadBoolean(tenantSettings.Data, "developerEnvironmentsRestricted"),
                ReadBoolean(tenantSettings.Data, "copilotDataMovementRestricted"),
                JsonDocument.Parse(tenantSettings.Data.GetRawText()), settingsEvidenceId));
        }

        var environments = ProjectItems(sections, SectionKeys.Environments, (item, evidenceId) =>
        {
            var id = ReadString(item, "id", "environmentId");
            return string.IsNullOrWhiteSpace(id) ? null : new EnvironmentEvidence(Guid.NewGuid(), customerId, snapshotId, id,
                ReadString(item, "displayName", "name") ?? id, ReadString(item, "environmentType", "type") ?? "Unknown",
                ReadString(item, "region", "location") ?? "Unknown", ReadBoolean(item, "isDefault") ?? false,
                ReadBoolean(item, "isManaged") ?? false, ReadString(item, "protectionLevel"),
                ReadBoolean(item, "hasDataverse") ?? false, ReadGuid(item, "securityGroupId"),
                JsonDocument.Parse(item.GetRawText()), evidenceId);
        });
        var dlpPolicies = ProjectItems(sections, SectionKeys.DlpPolicies, (item, evidenceId) =>
        {
            var id = ReadString(item, "id", "policyId");
            return string.IsNullOrWhiteSpace(id) ? null : new DlpPolicyEvidence(Guid.NewGuid(), customerId, snapshotId, id,
                ReadString(item, "displayName", "name") ?? id, JsonDocument.Parse(item.GetRawText()), evidenceId);
        });
        return new(settings, environments, dlpPolicies);
    }

    private static IReadOnlyCollection<T> ProjectItems<T>(IReadOnlyDictionary<string, EvaluationEvidenceSection> sections,
        string sectionKey, Func<JsonElement, Guid, T?> projector) where T : class
    {
        if (!sections.TryGetValue(sectionKey, out var section) || section.Data.ValueKind != JsonValueKind.Array ||
            section.EvidenceReferenceIds.FirstOrDefault() is not var evidenceId || evidenceId == Guid.Empty) return [];
        return section.Data.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
            .Select(value => projector(value, evidenceId)).Where(value => value is not null).Cast<T>().ToArray();
    }

    private static string? ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static bool? ReadBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static Guid? ReadGuid(JsonElement item, string name) =>
        Guid.TryParse(ReadString(item, name), out var value) ? value : null;

    private static IReadOnlyCollection<string> DiscoverEnvironmentIds(IReadOnlyDictionary<string, EvaluationEvidenceSection> sections) =>
        sections.TryGetValue(SectionKeys.Environments, out var environments) && environments.Data.ValueKind == JsonValueKind.Array
            ? environments.Data.EnumerateArray().Where(value => value.TryGetProperty("id", out _))
                .Select(value => value.GetProperty("id").GetString()).Where(value => value is not null).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray()
            : [];

    private static IReadOnlyDictionary<string, EvaluationEvidenceSection> BuildSections(
        IReadOnlyCollection<SnapshotSection> completedSections,
        IReadOnlyCollection<EvaluationEvidencePayload> payloads)
    {
        var documents = new List<JsonDocument>();
        try
        {
            return completedSections.ToDictionary(section => SectionKeys.Canonicalize(section.SectionKey), section =>
            {
                var key = SectionKeys.Canonicalize(section.SectionKey);
                var matching = payloads.Where(value => SectionKeys.Canonicalize(value.Reference.SectionKey) == key)
                    .OrderBy(value => value.Reference.PageNumber).ThenBy(value => value.Reference.CapturedAt).ToArray();
                var parsed = matching.Select(value => JsonDocument.Parse(value.Content)).ToArray();
                documents.AddRange(parsed);
                var normalized = Normalize(key, parsed.Select(value => value.RootElement));
                documents.Add(normalized);
                var confidence = matching.Length == 0 ? EvidenceConfidence.PocRequired : matching.Min(value => value.Reference.LifecycleConfidence);
                return new EvaluationEvidenceSection(key, section.Coverage, confidence, normalized.RootElement.Clone(),
                    matching.Select(value => value.Reference.RawEvidenceReferenceId).ToArray());
            }, StringComparer.Ordinal);
        }
        finally
        {
            foreach (var document in documents) document.Dispose();
        }
    }

    private static JsonDocument Normalize(string sectionKey, IEnumerable<JsonElement> roots)
    {
        var values = roots.SelectMany(root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray()
                    : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                        ? items.EnumerateArray()
                        : [root])
            .Select(value => value.Clone()).ToArray();
        if (sectionKey == SectionKeys.TenantSettings && values.Length == 1 && values[0].ValueKind == JsonValueKind.Object)
            return JsonDocument.Parse(values[0].GetRawText());
        return JsonDocument.Parse(JsonSerializer.Serialize(values));
    }
}