using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Api;

public static class PrincipalExtensions
{
    public static SubjectIdentity Subject(this ClaimsPrincipal principal)
    {
        var tenantClaim = principal.FindFirstValue("tid");
        var objectClaim = principal.FindFirstValue("oid");
        return Guid.TryParse(tenantClaim, out var tenantId) && Guid.TryParse(objectClaim, out var objectId)
            ? SubjectIdentity.Create(tenantId, objectId)
            : throw new TenantAccessDeniedException();
    }

    public static void SetAuthorizedTenant(this HttpContext context, TenantContext tenant) => context.Items[typeof(TenantContext)] = tenant;
}

public sealed class ApiAccessOptions
{
    public string Scope { get; set; } = "ppgsm.read";
    public string[] Audiences { get; set; } = [];
    public string[] AuthorizedClientIds { get; set; } = [];
}

public static class ApiAccessPolicy
{
    public static bool IsAuthorized(ClaimsPrincipal principal, ApiAccessOptions options)
    {
        if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(principal.FindFirstValue("oid"), out var objectId) || objectId == Guid.Empty) return false;
        var scopes = (principal.FindFirstValue("scp") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(options.Scope, StringComparer.Ordinal)) return false;
        var audiences = principal.FindAll("aud").Select(claim => claim.Value);
        if (options.Audiences.Length == 0 || !audiences.Any(value => options.Audiences.Contains(value, StringComparer.OrdinalIgnoreCase))) return false;
        var clientId = principal.FindFirstValue("azp") ?? principal.FindFirstValue("appid");
        return options.AuthorizedClientIds.Length > 0 && !string.IsNullOrWhiteSpace(clientId) &&
            options.AuthorizedClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase);
    }
}

public static class RawEvidenceProjection
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
        { "displayName", "mail", "email", "userPrincipalName", "upn", "phone", "mobilePhone", "givenName", "surname" };

    public static async ValueTask<object> CreateAsync(AuthorizedRawEvidence evidence, bool includeRaw, CancellationToken cancellationToken)
    {
        using (evidence.Content)
        {
            using var document = await JsonDocument.ParseAsync(evidence.Content, cancellationToken: cancellationToken);
            var content = includeRaw ? (object)document.RootElement.Clone() : Redact(document.RootElement);
            return new { evidence.Reference.RawEvidenceReferenceId, evidence.Reference.SectionKey, evidence.Reference.MediaType, Content = content };
        }
    }

    private static object? Redact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name,
            property => SensitiveNames.Contains(property.Name) ? (object?)"[REDACTED]" : Redact(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Redact).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

public sealed class DevelopmentSubjectAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevelopmentSubject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Request.Headers["X-Development-Tenant"].FirstOrDefault() ?? "11111111-1111-1111-1111-111111111111";
        var subject = Request.Headers["X-Development-Subject"].FirstOrDefault() ?? "22222222-2222-2222-2222-222222222222";
        if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(subject, out _))
        {
            return Task.FromResult(AuthenticateResult.Fail("Development tid and oid headers must be GUIDs."));
        }
        var identity = new ClaimsIdentity(
            [new Claim("tid", tenant), new Claim("oid", subject),
                new Claim("scp", Request.Headers["X-Development-Scope"].FirstOrDefault() ?? "ppgsm.read"),
                new Claim("aud", Request.Headers["X-Development-Audience"].FirstOrDefault() ?? "ppgsm-api"),
                new Claim("azp", Request.Headers["X-Development-Client"].FirstOrDefault() ?? "ppgsm-development-client"),
                new Claim(ClaimTypes.Name, subject)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        context.TraceIdentifier = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId;
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        await next(context);
    }
}

public interface IAuditService
{
    ValueTask RecordAsync(TenantContext tenant, HttpContext context, CancellationToken cancellationToken);
}

public sealed class AuditService(IAuditSink sink, TimeProvider timeProvider) : IAuditService
{
    public ValueTask RecordAsync(TenantContext tenant, HttpContext context, CancellationToken cancellationToken) => sink.AppendAsync(
        new AuditEvent(0, tenant.CustomerId, tenant.Subject.TenantId, tenant.Subject.ObjectId,
            $"{context.Request.Method} {context.Request.Path}", "HttpResource", null, timeProvider.GetUtcNow(),
            context.Response.StatusCode >= 500 ? "Failure" : context.Response.StatusCode >= 400 ? "Denied" : "Success",
            context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString(), null, context.TraceIdentifier), cancellationToken);
}

public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuditService auditService)
    {
        try
        {
            await next(context);
        }
        finally
        {
            var tenant = context.Items.TryGetValue(typeof(TenantContext), out var value) && value is TenantContext authorized
                ? authorized
                : TryCreateDeniedTenant(context);
            if (tenant is not null)
            {
                await auditService.RecordAsync(tenant, context, CancellationToken.None);
            }
        }
    }

    private static TenantContext? TryCreateDeniedTenant(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.RouteValues["customerId"]?.ToString(), out var customerId)) return null;
        try
        {
            return new(customerId, context.User.Subject(), MembershipRole.Reader);
        }
        catch (TenantAccessDeniedException)
        {
            return null;
        }
    }
}

public sealed class LocalDevelopmentStore : ICustomerStore, ITenantMembershipStore, ITenantConnectionStore, ISnapshotStore, IRawEvidenceAuthorizationStore, IAuditSink, ISnapshotEvidenceSink, ICollectorCheckpointStore, ISectionProgressSink, IGovernanceStore, IEvaluationEvidenceStore, IEvidenceProjectionStore, IPocApprovalStore, IExportArtifactStore, IExportDownloadAuthorizer, ICustomerOffboardingStore, ICustomerQueueAdapter
{
    private readonly ConcurrentDictionary<Guid, Customer> _customers = new();
    private readonly ConcurrentDictionary<(Guid CustomerId, Guid TenantId, Guid ObjectId), TenantMembership> _memberships = new();
    private readonly ConcurrentDictionary<Guid, Snapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, TenantConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, TenantCapability> _capabilities = new();
    private readonly ConcurrentDictionary<Guid, CustomerLegalHold> _legalHolds = new();
    private readonly ConcurrentDictionary<Guid, CustomerDeletionRecord> _deletions = new();
    private readonly ConcurrentDictionary<Guid, LocalEvidence> _evidence = new();
    private readonly ConcurrentDictionary<(Guid CustomerId, Guid SnapshotId, string SectionKey), CollectorCheckpoint> _checkpoints = new();
    private readonly ConcurrentQueue<SectionProgress> _progress = new();
    private readonly ConcurrentQueue<AuditEvent> _audit = new();
    private readonly ConcurrentDictionary<Guid, Finding> _findings = new();
    private readonly ConcurrentDictionary<Guid, GovernanceException> _exceptions = new();
    private readonly ConcurrentDictionary<Guid, ExportJob> _exports = new();
    private readonly ConcurrentDictionary<Guid, TenantSettingsEvidence> _tenantSettings = new();
    private readonly ConcurrentDictionary<Guid, EnvironmentEvidence> _environments = new();
    private readonly ConcurrentDictionary<Guid, DlpPolicyEvidence> _dlpPolicies = new();
    private readonly ConcurrentDictionary<(Guid CustomerId, Guid ExportJobId), byte[]> _exportArtifacts = new();
    private readonly ConcurrentDictionary<Guid, RemediationProposal> _proposals = new();
    private readonly ConcurrentDictionary<Guid, PocApproval> _pocApprovals = new();

    public ValueTask<Customer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _customers.TryGetValue(customerId, out var customer);
        return ValueTask.FromResult(customer);
    }

    public ValueTask<Customer> CreateCustomerAsync(string name, Guid entraTenantId, string region, SubjectIdentity creator, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Customer name is required.", nameof(name));
        if (entraTenantId == Guid.Empty) throw new ArgumentException("Entra tenant ID is required.", nameof(entraTenantId));
        if (_customers.Values.Any(value => value.EntraTenantId == entraTenantId)) throw new DomainConflictException("Entra tenant is already registered.");

        var customer = new Customer(Guid.NewGuid(), name.Trim(), entraTenantId, region.Trim(), CustomerStatus.Pending, DateTimeOffset.UtcNow);
        _customers[customer.CustomerId] = customer;
        var membership = new TenantMembership(Guid.NewGuid(), customer.CustomerId, creator.TenantId, creator.ObjectId, MembershipRole.Consultant, DateTimeOffset.UtcNow);
        _memberships[(customer.CustomerId, creator.TenantId, creator.ObjectId)] = membership;
        return ValueTask.FromResult(customer);
    }

    public ValueTask<TenantMembership?> FindAsync(SubjectIdentity subject, Guid customerId, CancellationToken cancellationToken)
    {
        _memberships.TryGetValue((customerId, subject.TenantId, subject.ObjectId), out var membership);
        return ValueTask.FromResult(membership);
    }

    public ValueTask<IReadOnlyList<TenantMembership>> ListForSubjectAsync(SubjectIdentity subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<TenantMembership>>(_memberships.Values
            .Where(value => value.SubjectTenantId == subject.TenantId && value.SubjectObjectId == subject.ObjectId)
            .OrderBy(value => value.CustomerId)
            .ToArray());
    }

    public ValueTask<TenantMembership> GrantAsync(Guid customerId, SubjectIdentity subject, MembershipRole role, CancellationToken cancellationToken)
    {
        var membership = new TenantMembership(Guid.NewGuid(), customerId, subject.TenantId, subject.ObjectId, role, DateTimeOffset.UtcNow);
        _memberships[(customerId, subject.TenantId, subject.ObjectId)] = membership;
        return ValueTask.FromResult(membership);
    }

    public ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken)
    {
        _connections.TryGetValue(customerId, out var connection);
        return ValueTask.FromResult(connection);
    }

    public ValueTask<TenantConnection> SaveAsync(TenantConnection connection, CancellationToken cancellationToken)
    {
        _connections[connection.CustomerId] = connection;
        return ValueTask.FromResult(connection);
    }

    public ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<TenantCapability>>(_capabilities.Values.Where(value => value.CustomerId == customerId).OrderBy(value => value.Endpoint).ToArray());

    public ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken)
    {
        foreach (var item in _capabilities.Where(value => value.Value.CustomerId == customerId && value.Value.ConnectionId == connectionId).ToArray())
            _capabilities.TryRemove(item.Key, out _);
        foreach (var capability in capabilities) _capabilities[capability.TenantCapabilityId] = capability;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Snapshot?> FindByIdAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return ValueTask.FromResult(snapshot?.CustomerId == customerId ? snapshot : null);
    }

    public ValueTask<Snapshot?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_snapshots.Values.SingleOrDefault(value => value.CustomerId == customerId && value.IdempotencyKey == idempotencyKey));

    public ValueTask<IReadOnlyList<Snapshot>> ListAsync(Guid customerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<Snapshot>>(_snapshots.Values.Where(value => value.CustomerId == customerId).OrderByDescending(value => value.RequestedAt).ToArray());

    public ValueTask AddAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        if (_snapshots.Values.Any(value => value.CustomerId == snapshot.CustomerId && value.IdempotencyKey == snapshot.IdempotencyKey))
        {
            throw new DomainConflictException("A snapshot with this idempotency key already exists.");
        }
        if (!_snapshots.TryAdd(snapshot.SnapshotId, snapshot)) throw new DomainConflictException("Snapshot already exists.");
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveAsync(Snapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<AuthorizedRawEvidence?> OpenAuthorizedAsync(Guid customerId, Guid snapshotId, Guid evidenceId, CancellationToken cancellationToken)
    {
        _evidence.TryGetValue(evidenceId, out var evidence);
        AuthorizedRawEvidence? result = evidence?.Reference.CustomerId == customerId && evidence.Reference.SnapshotId == snapshotId
            ? new(evidence.Reference, new MemoryStream(evidence.Content, writable: false))
            : null;
        return ValueTask.FromResult(result);
    }

    public async ValueTask WriteRawAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (!_evidence.TryAdd(evidence.RawEvidenceReferenceId, new(evidence, buffer.ToArray())))
        {
            throw new DomainConflictException("Raw evidence already exists.");
        }
    }

    public ValueTask<IReadOnlyCollection<EvaluationEvidencePayload>> LoadEvaluationEvidenceAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<EvaluationEvidencePayload>>(_evidence.Values
            .Where(value => value.Reference.CustomerId == customerId && value.Reference.SnapshotId == snapshotId)
            .Select(value => new EvaluationEvidencePayload(value.Reference, value.Content)).ToArray());

    public ValueTask SaveEnvironmentScopeAsync(SnapshotEnvironmentScope scope, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask SaveNormalizedEvidenceAsync(Guid customerId, Guid snapshotId, NormalizedEvidenceProjection projection, CancellationToken cancellationToken)
    {
        foreach (var item in _tenantSettings.Where(value => value.Value.CustomerId == customerId && value.Value.SnapshotId == snapshotId).ToArray()) _tenantSettings.TryRemove(item.Key, out _);
        foreach (var item in _environments.Where(value => value.Value.CustomerId == customerId && value.Value.SnapshotId == snapshotId).ToArray()) _environments.TryRemove(item.Key, out _);
        foreach (var item in _dlpPolicies.Where(value => value.Value.CustomerId == customerId && value.Value.SnapshotId == snapshotId).ToArray()) _dlpPolicies.TryRemove(item.Key, out _);
        foreach (var item in projection.TenantSettings) _tenantSettings[item.TenantSettingsEvidenceId] = item;
        foreach (var item in projection.Environments) _environments[item.EnvironmentEvidenceId] = item;
        foreach (var item in projection.DlpPolicies) _dlpPolicies[item.DlpPolicyEvidenceId] = item;
        return ValueTask.CompletedTask;
    }

    public ValueTask<EvidenceMetadataPage> ListEvidenceMetadataAsync(Guid customerId, Guid snapshotId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = _evidence.Values.Select(value => value.Reference).Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId)
            .OrderBy(value => value.SectionKey).ThenBy(value => value.PageNumber).ThenBy(value => value.CapturedAt).ToArray();
        return ValueTask.FromResult(new EvidenceMetadataPage(matches.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, matches.Length));
    }

    public ValueTask<IReadOnlyCollection<TenantSettingsEvidence>> ListTenantSettingsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<TenantSettingsEvidence>>(_tenantSettings.Values.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ToArray());

    public ValueTask<IReadOnlyCollection<EnvironmentEvidence>> ListEnvironmentsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<EnvironmentEvidence>>(_environments.Values.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).OrderBy(value => value.DisplayName).ToArray());

    public ValueTask<IReadOnlyCollection<DlpPolicyEvidence>> ListDlpPoliciesAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<DlpPolicyEvidence>>(_dlpPolicies.Values.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).OrderBy(value => value.DisplayName).ToArray());

    public ValueTask<CollectorCheckpoint?> ReadAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryGetValue((customerId, snapshotId, sectionKey), out var checkpoint);
        return ValueTask.FromResult(checkpoint);
    }

    public ValueTask WriteAsync(CollectorCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints[(checkpoint.CustomerId, checkpoint.SnapshotId, checkpoint.SectionKey)] = checkpoint;
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryRemove((customerId, snapshotId, sectionKey), out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(SectionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _progress.Enqueue(progress);
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _audit.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Finding>> ListFindingsAsync(Guid customerId, Guid snapshotId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeExceptions = _exceptions.Values.Where(value => value.CustomerId == customerId && value.IsActive(now)).Select(value => value.FindingId).ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<Finding>>(_findings.Values
            .Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId)
            .Select(value => activeExceptions.Contains(value.FindingId) ? value with { Status = FindingStatus.Excepted } : value)
            .ToArray());
    }

    public ValueTask<Finding?> FindFindingAsync(Guid customerId, Guid snapshotId, Guid findingId, CancellationToken cancellationToken)
    {
        _findings.TryGetValue(findingId, out var finding);
        return ValueTask.FromResult(finding?.CustomerId == customerId && finding.SnapshotId == snapshotId ? finding : null);
    }

    public ValueTask<RawEvidenceReference?> FindEvidenceByHashAsync(Guid customerId, Guid snapshotId, string evidenceHash, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_evidence.Values.Select(value => value.Reference).SingleOrDefault(value => value.CustomerId == customerId &&
            value.SnapshotId == snapshotId && string.Equals(value.ContentHash, evidenceHash, StringComparison.Ordinal)));

    public ValueTask ReplaceFindingsAsync(Guid customerId, Guid snapshotId, IReadOnlyCollection<Finding> findings, CancellationToken cancellationToken)
    {
        if (findings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId)) throw new TenantAccessDeniedException();
        foreach (var existing in _findings.Where(value => value.Value.CustomerId == customerId && value.Value.SnapshotId == snapshotId).ToArray())
            _findings.TryRemove(existing.Key, out _);
        foreach (var finding in findings) _findings[finding.FindingId] = finding;
        return ValueTask.CompletedTask;
    }

    public ValueTask<PocApproval> AddPocApprovalAsync(PocApproval approval, CancellationToken cancellationToken)
    {
        if (approval.ExpiresAt <= approval.ApprovedAt) throw new ArgumentException("PoC approval expiry must be after approval.", nameof(approval));
        if (!_evidence.TryGetValue(approval.EvidenceReferenceId, out var evidence) || evidence.Reference.CustomerId != approval.CustomerId ||
            !string.Equals(evidence.Reference.PrincipalIdentityBasis, approval.Identity, StringComparison.Ordinal) ||
            !string.Equals(evidence.Reference.ApiVersion, approval.ApiVersion, StringComparison.Ordinal)) throw new TenantAccessDeniedException();
        if (!_pocApprovals.TryAdd(approval.PocApprovalId, approval)) throw new DomainConflictException("PoC approval already exists.");
        return ValueTask.FromResult(approval);
    }

    public ValueTask<IReadOnlySet<string>> GetApprovedRuleIdsAsync(Guid customerId, string identity, string apiVersion,
        DateTimeOffset now, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlySet<string>>(
            _pocApprovals.Values.Where(value => value.Matches(customerId, value.RuleId, identity, apiVersion, now))
                .Select(value => value.RuleId).ToHashSet(StringComparer.Ordinal));

    public ValueTask<GovernanceException> AddExceptionAsync(GovernanceException exception, CancellationToken cancellationToken)
    {
        if (exception.ExpiresAt <= exception.ApprovedAt) throw new ArgumentException("Exception expiry must be after approval.", nameof(exception));
        if (!_findings.TryGetValue(exception.FindingId, out var finding) || finding.CustomerId != exception.CustomerId) throw new TenantAccessDeniedException();
        if (!_exceptions.TryAdd(exception.ExceptionId, exception)) throw new DomainConflictException("Exception already exists.");
        return ValueTask.FromResult(exception);
    }

    public ValueTask<ExportJob> AddExportAsync(ExportJob job, CancellationToken cancellationToken)
    {
        if (!_exports.TryAdd(job.ExportJobId, job)) throw new DomainConflictException("Export job already exists.");
        return ValueTask.FromResult(job);
    }

    public ValueTask<ExportJob?> FindExportAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken)
    {
        _exports.TryGetValue(exportJobId, out var job);
        return ValueTask.FromResult(job?.CustomerId == customerId ? job : null);
    }

    public ValueTask<IReadOnlyCollection<Guid>> ListQueuedExportsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<Guid>>(_exports.Values.Where(value => value.Status == ExportJobStatus.Queued).OrderBy(value => value.CreatedAt).Select(value => value.ExportJobId).ToArray());

    public ValueTask<ExportJob?> ClaimExportAsync(Guid exportJobId, CancellationToken cancellationToken)
    {
        if (!_exports.TryGetValue(exportJobId, out var job) || job.Status != ExportJobStatus.Queued) return ValueTask.FromResult<ExportJob?>(null);
        var running = job with { Status = ExportJobStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
        return ValueTask.FromResult<ExportJob?>(_exports.TryUpdate(exportJobId, running, job) ? running : null);
    }

    public ValueTask CompleteExportAsync(Guid exportJobId, string artifactPath, DateTimeOffset downloadExpiresAt,
        ExportArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        if (!_exports.TryGetValue(exportJobId, out var job) || job.Status != ExportJobStatus.Running) throw new DomainConflictException("Export job is not running.");
        _exports[exportJobId] = job with
        {
            Status = ExportJobStatus.Completed, DownloadUrl = artifactPath, DownloadExpiresAt = downloadExpiresAt,
            ArtifactContentHash = artifact.ContentHash, ArtifactContentLength = artifact.ContentLength,
            ArtifactMediaType = artifact.MediaType, ArtifactStorageETag = artifact.StorageETag, UpdatedAt = DateTimeOffset.UtcNow
        };
        return ValueTask.CompletedTask;
    }

    public ValueTask FailExportAsync(Guid exportJobId, string reason, CancellationToken cancellationToken)
    {
        if (_exports.TryGetValue(exportJobId, out var job)) _exports[exportJobId] = job with
        {
            Status = ExportJobStatus.Failed, FailureReason = reason, DownloadUrl = null, DownloadExpiresAt = null,
            ArtifactContentHash = null, ArtifactContentLength = null, ArtifactMediaType = null, ArtifactStorageETag = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask<ExportArtifactDescriptor> WriteAsync(Guid customerId, Guid exportJobId, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (!_exportArtifacts.TryAdd((customerId, exportJobId), bytes))
            throw new DomainConflictException("Export artifact already exists.");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        return new($"sha256:{hash}", bytes.LongLength, "application/json", $"local-{hash}");
    }

    public ValueTask<Stream?> OpenReadAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream?>(_exportArtifacts.TryGetValue((customerId, exportJobId), out var content) ? new MemoryStream(content, writable: false) : null);

    public ValueTask DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        foreach (var item in _exportArtifacts.Keys.Where(value => value.CustomerId == customerId).ToArray()) _exportArtifacts.TryRemove(item, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<Uri?> CreateAuthorizedDownloadAsync(ExportJob job, TimeSpan lifetime, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Uri?>(null);

    public ValueTask<RemediationProposal> AddProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken)
    {
        if (!_findings.TryGetValue(proposal.FindingId, out var finding) || finding.CustomerId != proposal.CustomerId || finding.SnapshotId != proposal.SnapshotId)
        {
            throw new TenantAccessDeniedException();
        }
        if (!_proposals.TryAdd(proposal.ProposalId, proposal)) throw new DomainConflictException("Remediation proposal already exists.");
        return ValueTask.FromResult(proposal);
    }

    public ValueTask<RemediationProposal?> FindProposalAsync(Guid customerId, Guid proposalId, CancellationToken cancellationToken)
    {
        _proposals.TryGetValue(proposalId, out var proposal);
        return ValueTask.FromResult(proposal?.CustomerId == customerId ? proposal : null);
    }

    public ValueTask<IReadOnlyList<RemediationProposal>> ListProposalsAsync(Guid customerId, RemediationProposalStatus? status, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RemediationProposal>>(_proposals.Values
            .Where(value => value.CustomerId == customerId && (status is null || value.Status == status))
            .OrderByDescending(value => value.ProposedAt).ThenBy(value => value.ProposalId).ToArray());

    public ValueTask SaveProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<CustomerLegalHold?> GetLegalHoldAsync(Guid customerId, CancellationToken cancellationToken)
    {
        _legalHolds.TryGetValue(customerId, out var hold);
        return ValueTask.FromResult(hold);
    }

    public ValueTask<CustomerDeletionRecord?> GetDeletionAsync(Guid customerId, CancellationToken cancellationToken)
    {
        _deletions.TryGetValue(customerId, out var deletion);
        return ValueTask.FromResult(deletion);
    }

    public ValueTask<CustomerDeletionRecord?> GetDeletionJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_deletions.Values.SingleOrDefault(value => value.JobId == jobId));

    public ValueTask SaveDeletionAsync(CustomerDeletionRecord deletion, CancellationToken cancellationToken)
    {
        _deletions[deletion.CustomerId] = deletion;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<Guid>> ListExecutableDeletionJobsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<Guid>>(_deletions.Values
            .Where(value => value.Status == DeletionStatus.Approved ||
                (value.Status == DeletionStatus.PendingRetentionExpiry && value.RetentionExpiresAt <= now))
            .Select(value => value.JobId).ToArray());

    public ValueTask<IReadOnlyDictionary<string, long>> CountTenantDataAsync(Guid customerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>
        {
            ["Customers"] = _customers.ContainsKey(customerId) ? 1 : 0,
            ["TenantMemberships"] = _memberships.Count(value => value.Key.CustomerId == customerId),
            ["TenantConnections"] = _connections.ContainsKey(customerId) ? 1 : 0,
            ["Snapshots"] = _snapshots.Count(value => value.Value.CustomerId == customerId),
            ["RawEvidenceReferences"] = _evidence.Count(value => value.Value.Reference.CustomerId == customerId)
        });

    public ValueTask<PhysicalDeletionResult> DeleteTenantDataAsync(Guid customerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PhysicalDeletionResult(false, null, new Dictionary<string, long>(),
            "Development store has no physical deletion adapter."));

    public ValueTask CancelCustomerJobsAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class UnavailableExternalConsentRevocationAdapter : IExternalConsentRevocationAdapter
{
    public ValueTask<ExternalConsentRevocationResult> RevokeAsync(Guid customerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ExternalConsentRevocationResult(ExternalConsentRevocationStatus.PendingManualAction, null, [],
            "External tenant consent revocation is not configured."));
}

public static class ProductionServiceGuard
{
    private static readonly HashSet<Type> DurableStoreContracts =
    [
        typeof(ICustomerStore), typeof(ITenantMembershipStore), typeof(ISnapshotStore),
        typeof(IRawEvidenceAuthorizationStore), typeof(ISnapshotEvidenceSink), typeof(ICollectorCheckpointStore),
        typeof(ISectionProgressSink), typeof(IAuditSink), typeof(IGovernanceStore), typeof(ISnapshotJobStore),
        typeof(ICustomerOffboardingStore), typeof(IOnboardingStateReplayStore)
        , typeof(ITenantConnectionStore)
    ];

    public static void RejectLocalStores(IServiceCollection services)
    {
        var forbidden = services.Where(descriptor => DurableStoreContracts.Contains(descriptor.ServiceType)).Where(descriptor =>
        {
            var name = descriptor.ImplementationType?.Name ?? descriptor.ImplementationInstance?.GetType().Name;
            return name?.Contains("Local", StringComparison.OrdinalIgnoreCase) == true ||
                   name?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        }).Select(descriptor => descriptor.ServiceType.FullName).ToArray();

        if (forbidden.Length > 0)
        {
            throw new InvalidOperationException($"Production cannot resolve local or in-memory services: {string.Join(", ", forbidden)}.");
        }
    }

    public static void ValidateResolved(IServiceProvider provider)
    {
        var forbidden = DurableStoreContracts
            .SelectMany(type => provider.GetServices(type))
            .Where(instance => instance.GetType().Name.Contains("Local", StringComparison.OrdinalIgnoreCase) ||
                               instance.GetType().Name.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
            .Select(instance => instance.GetType().FullName)
            .ToArray();
        if (forbidden.Length > 0)
        {
            throw new InvalidOperationException($"Production resolved local or in-memory stores: {string.Join(", ", forbidden)}.");
        }
    }
}