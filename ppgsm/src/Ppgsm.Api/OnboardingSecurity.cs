using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ppgsm.Collectors.Authentication;
using Microsoft.EntityFrameworkCore;
using Ppgsm.Core.Domain;
using Ppgsm.Data;

namespace Ppgsm.Api;

public sealed record OnboardingState(
    Guid CustomerId,
    Guid InitiatingTenantId,
    Guid InitiatingObjectId,
    Guid EntraTenantId,
    string Operation,
    string Nonce,
    DateTimeOffset ExpiresAt);

public interface IOnboardingStateReplayStore
{
    ValueTask<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public interface IOnboardingStateProtector
{
    string Protect(OnboardingState state);
    ValueTask<OnboardingState> ValidateAndConsumeAsync(string protectedState, CancellationToken cancellationToken);
}

public sealed class OnboardingStateOptions
{
    public const string SectionName = "Onboarding:State";
    public string SigningKey { get; set; } = string.Empty;
    public int LifetimeMinutes { get; set; } = 10;
}

public sealed class HmacOnboardingStateProtector(
    OnboardingStateOptions options,
    IOnboardingStateReplayStore replayStore,
    TimeProvider timeProvider) : IOnboardingStateProtector
{
    public string Protect(OnboardingState state)
    {
        ValidateBinding(state);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(state));
        var signature = Base64UrlEncode(Sign(payload));
        return $"{payload}.{signature}";
    }

    public async ValueTask<OnboardingState> ValidateAndConsumeAsync(string protectedState, CancellationToken cancellationToken)
    {
        var parts = protectedState.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Sign(parts[0]), Base64UrlDecode(parts[1])))
        {
            throw new OnboardingValidationException("Onboarding state signature is invalid.");
        }

        var state = JsonSerializer.Deserialize<OnboardingState>(Base64UrlDecode(parts[0]))
            ?? throw new OnboardingValidationException("Onboarding state payload is invalid.");
        ValidateBinding(state);
        if (state.ExpiresAt <= timeProvider.GetUtcNow()) throw new OnboardingValidationException("Onboarding state has expired.");
        if (!await replayStore.TryConsumeAsync(state.Nonce, state.ExpiresAt, cancellationToken))
        {
            throw new OnboardingValidationException("Onboarding state has already been consumed.");
        }
        return state;
    }

    private byte[] Sign(string payload)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            throw new InvalidOperationException("Onboarding state signing key must be injected by the deployment secret provider.");
        }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.SigningKey));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static void ValidateBinding(OnboardingState state)
    {
        if (state.CustomerId == Guid.Empty || state.EntraTenantId == Guid.Empty ||
            state.InitiatingTenantId == Guid.Empty || state.InitiatingObjectId == Guid.Empty || string.IsNullOrWhiteSpace(state.Operation) ||
            string.IsNullOrWhiteSpace(state.Nonce))
        {
            throw new OnboardingValidationException("Onboarding state is missing a required binding.");
        }
    }

    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public sealed class InMemoryOnboardingStateReplayStore(TimeProvider timeProvider) : IOnboardingStateReplayStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public ValueTask<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var expired in _consumed.Where(entry => entry.Value <= timeProvider.GetUtcNow()))
        {
            _consumed.TryRemove(expired.Key, out _);
        }
        return ValueTask.FromResult(_consumed.TryAdd(nonce, expiresAt));
    }
}

public sealed class SqlOnboardingStateReplayStore(IPpgsmDbContextFactory contexts, TimeProvider timeProvider) : IOnboardingStateReplayStore
{
    private static readonly SubjectIdentity ServiceIdentity = SubjectIdentity.Create(
        Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"),
        Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff2"));

    public async ValueTask<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var db = contexts.Create(new(Guid.Empty, ServiceIdentity, MembershipRole.InternalAdmin, IsInternal: true));
        await db.OnboardingReplayNonces.Where(value => value.ExpiresAt <= timeProvider.GetUtcNow()).ExecuteDeleteAsync(cancellationToken);
        if (await db.OnboardingReplayNonces.AnyAsync(value => value.Nonce == nonce, cancellationToken)) return false;
        db.OnboardingReplayNonces.Add(new(nonce, expiresAt, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record ConsentCallback(
    string State,
    Guid? Tenant,
    bool AdminConsent,
    string? Error,
    string? ErrorDescription);

public sealed record AuthenticatedCallbackIdentity(Guid TenantId, Guid ObjectId)
{
    public static AuthenticatedCallbackIdentity From(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(principal.FindFirstValue("oid"), out var objectId) || objectId == Guid.Empty)
        {
            throw new OnboardingValidationException("Authenticated callback claims must contain valid tid and oid values.");
        }
        return new(tenantId, objectId);
    }
}

public sealed record ValidatedConsentCallback(
    OnboardingState State,
    Guid EntraTenantId,
    AuthenticatedCallbackIdentity AdminIdentity);

public enum CapabilityVerificationState { Verified, Unavailable, Unknown, PreviewOnly }

public sealed record EndpointCapabilityVerification(
    string Endpoint,
    string Identity,
    CapabilityVerificationState State,
    int? StatusCode,
    string? RequestId,
    string? RawResponseSha256,
    string Detail)
{
    public bool Available => State == CapabilityVerificationState.Verified;
}

public sealed record ConsentVerificationEvidence(
    Guid EntraTenantId,
    bool EnterpriseApplicationPresent,
    bool DelegatedScopeGranted,
    bool PowerPlatformRoleAssigned,
    string? EnterpriseApplicationObjectId,
    string? VerifiedAdminObjectId,
    IReadOnlyCollection<EndpointCapabilityVerification> Capabilities,
    string Detail)
{
    public bool Activatable => EntraTenantId != Guid.Empty && EnterpriseApplicationPresent && DelegatedScopeGranted &&
        PowerPlatformRoleAssigned && !string.IsNullOrWhiteSpace(VerifiedAdminObjectId) && Capabilities.Count > 0 &&
        Capabilities.All(value => value.State == CapabilityVerificationState.Verified);
}

public interface ITenantConsentVerifier
{
    ValueTask<ConsentVerificationEvidence> VerifyAsync(
        Guid entraTenantId,
        AuthenticatedCallbackIdentity adminIdentity,
        CancellationToken cancellationToken);
}

public sealed class UnavailableTenantConsentVerifier : ITenantConsentVerifier
{
    public ValueTask<ConsentVerificationEvidence> VerifyAsync(
        Guid entraTenantId,
        AuthenticatedCallbackIdentity adminIdentity,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ConsentVerificationEvidence(entraTenantId, false, false, false, null, adminIdentity.ObjectId.ToString("D"), [],
            "Live Entra and Power Platform verification is not configured; connection activation is denied."));
}

public sealed class TenantConsentVerificationOptions
{
    public const string SectionName = "Onboarding:Verification";
    public string ClientApplicationId { get; set; } = string.Empty;
    public string DelegatedResourceApplicationId { get; set; } = string.Empty;
    public string[] ExpectedDelegatedScopes { get; set; } = [];
    public List<ConsentCapabilityProbe> CapabilityProbes { get; set; } = [];

    public void Validate()
    {
        if (!Guid.TryParse(ClientApplicationId, out _) || !Guid.TryParse(DelegatedResourceApplicationId, out _) ||
            ExpectedDelegatedScopes.Length == 0 || ExpectedDelegatedScopes.Any(string.IsNullOrWhiteSpace) ||
            CapabilityProbes.Count == 0)
            throw new InvalidOperationException("Onboarding:Verification requires client/resource app IDs, expected scopes, and capability probes.");
        foreach (var probe in CapabilityProbes) probe.Validate();
    }
}

public sealed class ConsentCapabilityProbe
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ResourceScope { get; set; } = string.Empty;
    public bool Preview { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host) || !AllowedResources.Contains(ResourceScope))
            throw new InvalidOperationException($"Capability probe '{Name}' is not on the fixed read-only endpoint/resource allowlist.");
    }

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.powerplatform.com", "api.bap.microsoft.com", "api.powerapps.com", "api.flow.microsoft.com"
    };
    private static readonly HashSet<string> AllowedResources = new(StringComparer.Ordinal)
    {
        CollectorResources.PowerPlatform, CollectorResources.BusinessApplications, CollectorResources.Flow
    };
}

public sealed record GraphConsentFacts(
    Guid TenantId,
    string? ClientServicePrincipalId,
    bool ClientServicePrincipalEnabled,
    IReadOnlyCollection<string> DelegatedScopes,
    bool AdminRoleAssigned,
    string Detail);

public interface IConsentGraphTransport
{
    ValueTask<GraphConsentFacts> ReadAsync(
        Guid tenantId,
        Guid clientApplicationId,
        Guid resourceApplicationId,
        Guid adminObjectId,
        CancellationToken cancellationToken);
}

public interface IConsentCapabilityProbeTransport
{
    ValueTask<EndpointCapabilityVerification> ProbeAsync(
        Guid tenantId,
        AuthenticatedCallbackIdentity adminIdentity,
        ConsentCapabilityProbe probe,
        CancellationToken cancellationToken);
}

public sealed class LiveTenantConsentVerifier(
    TenantConsentVerificationOptions options,
    IConsentGraphTransport graph,
    IConsentCapabilityProbeTransport probes) : ITenantConsentVerifier
{
    public async ValueTask<ConsentVerificationEvidence> VerifyAsync(
        Guid entraTenantId,
        AuthenticatedCallbackIdentity adminIdentity,
        CancellationToken cancellationToken)
    {
        if (entraTenantId == Guid.Empty || adminIdentity.TenantId != entraTenantId)
            throw new OnboardingValidationException("Authenticated callback administrator is not bound to the customer tenant.");

        GraphConsentFacts facts;
        try
        {
            facts = await graph.ReadAsync(
                entraTenantId, Guid.Parse(options.ClientApplicationId), Guid.Parse(options.DelegatedResourceApplicationId),
                adminIdentity.ObjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(entraTenantId, false, false, false, null, adminIdentity.ObjectId.ToString("D"), [],
                $"Graph verification was inconclusive: {exception.GetType().Name}. Connection activation is denied.");
        }
        if (facts.TenantId != entraTenantId)
            throw new OnboardingValidationException("Graph verification returned evidence for a different tenant.");

        var granted = facts.DelegatedScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopesValid = options.ExpectedDelegatedScopes.All(granted.Contains);
        var capabilities = new List<EndpointCapabilityVerification>();
        foreach (var probe in options.CapabilityProbes)
        {
            try
            {
                capabilities.Add(await probes.ProbeAsync(entraTenantId, adminIdentity, probe, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                capabilities.Add(new(probe.Endpoint, "app-only", CapabilityVerificationState.Unknown, null, null, null,
                    $"{probe.Name} was inconclusive: {exception.GetType().Name}."));
            }
        }
        var detail = $"Graph: {facts.Detail} Capability probes: {string.Join("; ", capabilities.Select(value => value.Detail))}";
        return new(entraTenantId, facts.ClientServicePrincipalEnabled, scopesValid, facts.AdminRoleAssigned,
            facts.ClientServicePrincipalId, adminIdentity.ObjectId.ToString("D"), capabilities, detail);
    }
}

public sealed class TenantConnectionService(ITenantConnectionStore store, ITenantMembershipStore memberships, ITenantConsentVerifier verifier, TimeProvider timeProvider)
{
    public async ValueTask<(TenantConnection Connection, ConsentVerificationEvidence Evidence)> VerifyConsentAsync(
        ValidatedConsentCallback callback,
        CancellationToken cancellationToken)
    {
        var current = await store.FindAsync(callback.State.CustomerId, cancellationToken);
        var connectionId = current?.ConnectionId ?? Guid.NewGuid();
        await store.SaveAsync(new(connectionId, callback.State.CustomerId, ConnectionMode.Delegated, null, null, null, false,
            null, callback.AdminIdentity.ObjectId.ToString("D"), timeProvider.GetUtcNow(), ConnectionStatus.Pending, null), cancellationToken);

        var evidence = await verifier.VerifyAsync(callback.EntraTenantId, callback.AdminIdentity, cancellationToken);
        if (evidence.EntraTenantId != callback.State.EntraTenantId)
            throw new OnboardingValidationException("Verification evidence tenant does not match the signed onboarding tenant.");
        if (!string.Equals(evidence.VerifiedAdminObjectId, callback.AdminIdentity.ObjectId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new OnboardingValidationException("Verified customer administrator does not match authenticated callback claims.");

        var status = evidence.Activatable ? ConnectionStatus.Active : ConnectionStatus.Degraded;
        var connection = await store.SaveAsync(new(connectionId, callback.State.CustomerId, ConnectionMode.Delegated, null,
            Guid.TryParse(evidence.EnterpriseApplicationObjectId, out var enterpriseAppId) ? enterpriseAppId : null,
            evidence.PowerPlatformRoleAssigned ? "verified" : null, false, null, evidence.VerifiedAdminObjectId,
            timeProvider.GetUtcNow(), status, timeProvider.GetUtcNow()), cancellationToken);
        var capabilities = evidence.Capabilities.Select(value => new TenantCapability(Guid.NewGuid(), callback.State.CustomerId,
            connectionId, value.Endpoint, value.Identity, value.Available,
            JsonSerializer.Serialize(new
            {
                state = value.State.ToString(),
                value.StatusCode,
                value.RequestId,
                value.RawResponseSha256,
                value.Detail
            }), timeProvider.GetUtcNow())).Append(new TenantCapability(
                Guid.NewGuid(), callback.State.CustomerId, connectionId,
                "ppac.role.PowerPlatformAdministrator",
                $"delegated:{callback.AdminIdentity.TenantId:D}/{callback.AdminIdentity.ObjectId:D}",
                evidence.PowerPlatformRoleAssigned,
                JsonSerializer.Serialize(new
                {
                    source = "Microsoft Graph directory role verification",
                    entraTenantId = callback.AdminIdentity.TenantId,
                    principalObjectId = callback.AdminIdentity.ObjectId,
                    role = "Power Platform Administrator",
                    ppgsmMembership = "separate application authorization record"
                }), timeProvider.GetUtcNow())).ToArray();
        await store.ReplaceCapabilitiesAsync(callback.State.CustomerId, connectionId, capabilities, cancellationToken);
        if (status == ConnectionStatus.Active)
        {
            await memberships.GrantAsync(callback.State.CustomerId,
                SubjectIdentity.Create(callback.AdminIdentity.TenantId, callback.AdminIdentity.ObjectId), MembershipRole.CustomerAdmin, cancellationToken);
        }
        return (connection, evidence);
    }
}

public interface IConsentCallbackValidator
{
    ValueTask<ValidatedConsentCallback> ValidateAsync(
        ConsentCallback callback,
        AuthenticatedCallbackIdentity adminIdentity,
        CancellationToken cancellationToken);
}

public sealed class ConsentCallbackValidator(IOnboardingStateProtector states) : IConsentCallbackValidator
{
    public async ValueTask<ValidatedConsentCallback> ValidateAsync(
        ConsentCallback callback,
        AuthenticatedCallbackIdentity adminIdentity,
        CancellationToken cancellationToken)
    {
        var state = await states.ValidateAndConsumeAsync(callback.State, cancellationToken);
        if (!string.IsNullOrWhiteSpace(callback.Error) || !callback.AdminConsent)
        {
            throw new OnboardingValidationException($"Consent was not granted: {callback.Error ?? callback.ErrorDescription ?? "admin_consent=false"}.", state);
        }
        if (callback.Tenant is null || callback.Tenant.Value != state.EntraTenantId)
        {
            throw new OnboardingValidationException("Consent callback tenant does not match the signed onboarding tenant.", state);
        }
        if (adminIdentity.TenantId != state.EntraTenantId)
            throw new OnboardingValidationException("Authenticated callback administrator tenant does not match the signed onboarding tenant.", state);
        return new(state, callback.Tenant.Value, adminIdentity);
    }
}

public sealed class OnboardingValidationException(string message, OnboardingState? state = null) : InvalidOperationException(message)
{
    public OnboardingState? State { get; } = state;
}