using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ppgsm.Core.Domain;

namespace Ppgsm.Worker;

public sealed record JsonEvidencePackageOptions(long MaximumPackageBytes = 64 * 1024 * 1024);

public sealed record JsonEvidencePackageInput(
    ExportJob Job,
    Snapshot Snapshot,
    PublishedRuleSet RuleCatalog,
    IReadOnlyCollection<TenantSettingsEvidence> TenantSettings,
    IReadOnlyCollection<EnvironmentEvidence> Environments,
    IReadOnlyCollection<DlpPolicyEvidence> DlpPolicies,
    IReadOnlyCollection<RawEvidenceReference> RawEvidence,
    IReadOnlyDictionary<Guid, byte[]> PrivilegedRawBodies,
    IReadOnlyCollection<Finding> Findings,
    DateTimeOffset GeneratedAt);

public sealed record JsonEvidencePackageResult(string ContentHash, long ContentLength);

public sealed class JsonEvidencePackageBuilder(JsonEvidencePackageOptions options)
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "displayName", "mail", "email", "userPrincipalName", "upn", "phone", "mobilePhone",
        "givenName", "surname", "principalIdentityBasis", "tenantIdentityBasis"
    };

    public async ValueTask<JsonEvidencePackageResult> WriteAsync(
        JsonEvidencePackageInput input,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTenantScope(input);

        await using var package = new MemoryStream();
        await using (var limited = new SizeLimitedStream(package, options.MaximumPackageBytes, leaveOpen: true))
        {
            using var writer = new Utf8JsonWriter(limited, new JsonWriterOptions { Indented = false });
            WritePackage(writer, input);
            await writer.FlushAsync(cancellationToken);
        }

        package.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken)).ToLowerInvariant();
        package.Position = 0;

        var prefix = Encoding.UTF8.GetBytes($"{{\"contentHash\":\"sha256:{hash}\",\"evidencePackage\":");
        var suffix = "}"u8.ToArray();
        var contentLength = checked(prefix.LongLength + package.Length + suffix.LongLength);
        if (contentLength > options.MaximumPackageBytes)
            throw new InvalidOperationException($"JSON evidence package exceeds the {options.MaximumPackageBytes} byte limit.");

        await destination.WriteAsync(prefix, cancellationToken);
        await package.CopyToAsync(destination, cancellationToken);
        await destination.WriteAsync(suffix, cancellationToken);
        return new($"sha256:{hash}", contentLength);
    }

    private static void EnsureTenantScope(JsonEvidencePackageInput input)
    {
        var customerId = input.Job.CustomerId;
        var snapshotId = input.Job.SnapshotId;
        if (input.Snapshot.CustomerId != customerId || input.Snapshot.SnapshotId != snapshotId ||
            input.TenantSettings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            input.Environments.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            input.DlpPolicies.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            input.RawEvidence.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            input.PrivilegedRawBodies.Keys.Any(id => input.RawEvidence.All(value => value.RawEvidenceReferenceId != id)) ||
            input.Findings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId))
            throw new TenantAccessDeniedException();
    }

    private static void WritePackage(Utf8JsonWriter writer, JsonEvidencePackageInput input)
    {
        writer.WriteStartObject();
        writer.WriteNumber("packageSchemaVersion", 1);
        writer.WriteString("apiVersion", "v1");
        writer.WriteString("exportJobId", input.Job.ExportJobId);
        writer.WriteString("customerId", input.Job.CustomerId);
        writer.WriteString("snapshotId", input.Job.SnapshotId);
        writer.WriteString("generatedAt", input.GeneratedAt);
        writer.WritePropertyName("generationProvenance");
        writer.WriteStartObject();
        writer.WriteString("generator", "Ppgsm.Worker.JsonEvidencePackageBuilder");
        writer.WriteString("generatorVersion", typeof(JsonEvidencePackageBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
        writer.WriteBoolean("includesPrivilegedRawBodies", input.Job.IncludesPii);
        writer.WriteEndObject();
        WriteSnapshot(writer, input.Snapshot);
        WriteRuleCatalog(writer, input.RuleCatalog);
        WriteNormalizedEvidence(writer, input);
        WriteFindings(writer, input.Findings);
        WriteScore(writer, input);
        WriteRawEvidence(writer, input.RawEvidence, input.PrivilegedRawBodies, input.Job.IncludesPii);
        writer.WriteEndObject();
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, Snapshot snapshot)
    {
        writer.WritePropertyName("snapshot");
        writer.WriteStartObject();
        writer.WriteString("status", snapshot.Status.ToString());
        writer.WriteString("mode", snapshot.Mode.ToString());
        writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
        writer.WriteString("requestedAt", snapshot.RequestedAt);
        if (snapshot.StartedAt is { } startedAt) writer.WriteString("startedAt", startedAt);
        if (snapshot.CompletedAt is { } completedAt) writer.WriteString("completedAt", completedAt);
        writer.WritePropertyName("capturedIdentityBasis");
        writer.WriteStartObject();
        writer.WriteString("triggeredBy", "[REDACTED]");
        writer.WriteEndObject();
        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        foreach (var section in snapshot.Sections.OrderBy(value => value.SectionKey, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            var canonicalSectionKey = SectionKeys.Canonicalize(section.SectionKey);
            writer.WriteString("sectionKey", canonicalSectionKey);
            if (!string.Equals(canonicalSectionKey, section.SectionKey, StringComparison.Ordinal))
                writer.WriteString("originalSectionKey", section.SectionKey);
            writer.WriteString("coverage", section.Coverage.ToString());
            writer.WriteNumber("itemCount", section.ItemCount);
            if (section.Reason is not null) writer.WriteString("reason", section.Reason);
            writer.WriteString("recordedAt", section.RecordedAt);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRuleCatalog(Utf8JsonWriter writer, PublishedRuleSet catalog)
    {
        writer.WritePropertyName("ruleCatalog");
        writer.WriteStartObject();
        writer.WriteString("version", catalog.Version);
        writer.WriteString("publishedAt", catalog.PublishedAt);
        writer.WriteString("publicationAttestation", catalog.PublicationAttestation);
        writer.WriteString("contentDigest", catalog.ContentDigest);
        writer.WritePropertyName("evaluatorVersions");
        using (var versions = JsonDocument.Parse(catalog.EvaluatorVersionsJson)) WriteJson(writer, versions.RootElement, includePii: false);
        writer.WriteString("profileId", catalog.DefaultProfile.Id);
        writer.WriteNumber("profileVersion", catalog.DefaultProfile.Version);
        writer.WriteEndObject();
    }

    private static void WriteNormalizedEvidence(Utf8JsonWriter writer, JsonEvidencePackageInput input)
    {
        writer.WritePropertyName("normalizedEvidence");
        writer.WriteStartObject();
        writer.WritePropertyName("tenantSettings");
        writer.WriteStartArray();
        foreach (var item in input.TenantSettings.OrderBy(value => value.TenantSettingsEvidenceId))
        {
            writer.WriteStartObject();
            writer.WriteString("evidenceId", item.RawEvidenceReferenceId);
            WriteNullableBoolean(writer, "trialEnvironmentsDisabled", item.TrialEnvironmentsDisabled);
            WriteNullableBoolean(writer, "developerEnvironmentsRestricted", item.DeveloperEnvironmentsRestricted);
            WriteNullableBoolean(writer, "copilotDataMovementRestricted", item.CopilotDataMovementRestricted);
            writer.WritePropertyName("knownSettings");
            WriteJson(writer, item.KnownSettings.RootElement, input.Job.IncludesPii);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("environments");
        writer.WriteStartArray();
        foreach (var item in input.Environments.OrderBy(value => value.EnvironmentId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.EnvironmentId);
            writer.WriteString("displayName", input.Job.IncludesPii ? item.DisplayName : "[REDACTED]");
            writer.WriteString("type", item.EnvironmentType);
            writer.WriteString("region", item.Region);
            writer.WriteBoolean("isDefault", item.IsDefault);
            writer.WriteBoolean("isManaged", item.IsManaged);
            if (item.ProtectionLevel is not null) writer.WriteString("protectionLevel", item.ProtectionLevel);
            writer.WriteBoolean("hasDataverse", item.HasDataverse);
            writer.WriteString("evidenceId", item.RawEvidenceReferenceId);
            writer.WritePropertyName("properties");
            WriteJson(writer, item.Properties.RootElement, input.Job.IncludesPii);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("dlpPolicies");
        writer.WriteStartArray();
        foreach (var item in input.DlpPolicies.OrderBy(value => value.PolicyId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.PolicyId);
            writer.WriteString("displayName", input.Job.IncludesPii ? item.DisplayName : "[REDACTED]");
            writer.WriteString("evidenceId", item.RawEvidenceReferenceId);
            writer.WritePropertyName("properties");
            WriteJson(writer, item.Properties.RootElement, input.Job.IncludesPii);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFindings(Utf8JsonWriter writer, IReadOnlyCollection<Finding> findings)
    {
        writer.WritePropertyName("findings");
        writer.WriteStartArray();
        foreach (var finding in findings.OrderBy(value => value.RuleId, StringComparer.Ordinal).ThenBy(value => value.FindingId))
        {
            writer.WriteStartObject();
            writer.WriteString("findingId", finding.FindingId);
            writer.WriteString("ruleId", finding.RuleId);
            writer.WriteNumber("ruleVersion", finding.RuleVersion);
            writer.WriteString("catalogVersion", finding.CatalogVersion);
            writer.WriteString("publicationContentDigest", finding.PublicationContentDigest);
            writer.WritePropertyName("evaluatorVersions");
            using (var versions = JsonDocument.Parse(finding.EvaluatorVersionsJson)) WriteJson(writer, versions.RootElement, includePii: false);
            writer.WriteString("evaluatorKey", finding.EvaluatorKey);
            writer.WriteNumber("evaluatorVersion", finding.EvaluatorVersion);
            writer.WriteString("title", finding.Title);
            writer.WriteString("area", finding.Area);
            writer.WriteString("severity", finding.Severity.ToString());
            writer.WriteString("status", finding.Status.ToString());
            writer.WriteString("scope", finding.Scope);
            writer.WriteString("observed", finding.Observed);
            writer.WriteString("interpretation", finding.Interpretation);
            writer.WriteString("proposedAction", finding.ProposedAction);
            writer.WritePropertyName("evidenceReferences");
            using var references = JsonDocument.Parse(finding.EvidenceLinksJson);
            WriteJson(writer, references.RootElement, includePii: false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteScore(Utf8JsonWriter writer, JsonEvidencePackageInput input)
    {
        var eligible = input.Findings.Where(value => value.Severity != FindingSeverity.Informational &&
            value.Status is not FindingStatus.NotEvaluated and not FindingStatus.NotApplicable and not FindingStatus.Excepted).ToArray();
        if (eligible.Length == 0) return;
        var confidence = EvidenceCoverageAggregator.Aggregate(input.Snapshot.Sections.Select(value => value.Coverage));
        var score = GovernanceScoring.Calculate(input.Job.CustomerId, input.Job.SnapshotId, input.Findings, confidence);
        writer.WritePropertyName("score");
        JsonSerializer.Serialize(writer, score);
    }

    private static void WriteRawEvidence(
        Utf8JsonWriter writer,
        IReadOnlyCollection<RawEvidenceReference> evidence,
        IReadOnlyDictionary<Guid, byte[]> privilegedRawBodies,
        bool includePii)
    {
        writer.WritePropertyName("rawEvidence");
        writer.WriteStartArray();
        foreach (var reference in evidence.OrderBy(value => value.SectionKey, StringComparer.Ordinal)
                     .ThenBy(value => value.PageNumber).ThenBy(value => value.RawEvidenceReferenceId))
        {
            writer.WriteStartObject();
            writer.WriteString("evidenceId", reference.RawEvidenceReferenceId);
            var canonicalSectionKey = SectionKeys.Canonicalize(reference.SectionKey);
            writer.WriteString("sectionKey", canonicalSectionKey);
            if (!string.Equals(canonicalSectionKey, reference.SectionKey, StringComparison.Ordinal))
                writer.WriteString("originalSectionKey", reference.SectionKey);
            writer.WriteString("contentHash", reference.ContentHash);
            writer.WriteString("mediaType", reference.MediaType);
            writer.WriteString("apiVersion", reference.ApiVersion);
            writer.WriteString("capturedAt", reference.CapturedAt);
            writer.WriteString("lifecycleConfidence", reference.LifecycleConfidence.ToString());
            writer.WriteString("collectorId", reference.CollectorId);
            writer.WriteString("collectorVersion", reference.CollectorVersion);
            writer.WriteString("parserSchemaVersion", reference.ParserSchemaVersion);
            writer.WriteString("authMode", reference.AuthMode.ToString());
            writer.WriteString("tenantIdentityBasis", "[REDACTED]");
            writer.WriteString("principalIdentityBasis", "[REDACTED]");
            writer.WriteNumber("pageNumber", reference.PageNumber);
            writer.WriteNumber("attemptNumber", reference.AttemptNumber);
            writer.WriteString("completenessRationale", reference.CompletenessRationale);
            if (includePii && privilegedRawBodies.TryGetValue(reference.RawEvidenceReferenceId, out var content))
            {
                writer.WritePropertyName("body");
                using var body = JsonDocument.Parse(content);
                WriteJson(writer, body.RootElement, includePii: true);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonElement value, bool includePii, string? propertyName = null)
    {
        if (!includePii && propertyName is not null && SensitiveNames.Contains(propertyName))
        {
            writer.WriteStringValue("[REDACTED]");
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJson(writer, property.Value, includePii, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteJson(writer, item, includePii);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteNullableBoolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } present) writer.WriteBoolean(name, present);
        else writer.WriteNull(name);
    }

    private sealed class SizeLimitedStream(Stream inner, long maximumBytes, bool leaveOpen) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { Ensure(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Ensure(buffer.Length); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Ensure(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
        private void Ensure(int count)
        {
            if (inner.Length + count > maximumBytes)
                throw new InvalidOperationException($"JSON evidence package exceeds the {maximumBytes} byte limit.");
        }
        protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { if (!leaveOpen) await inner.DisposeAsync(); GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}