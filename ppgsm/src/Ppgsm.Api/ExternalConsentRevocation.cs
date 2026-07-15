using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Core.Domain;

namespace Ppgsm.Api;

public enum EnterpriseApplicationOffboardingPolicy { Preserve, Disable, Remove }

public sealed record OffboardingCapability(bool Enabled)
{
    public void Require()
    {
        if (!Enabled) throw new CapabilityUnavailableException(ApiCapability.Offboarding);
    }
}

public sealed class ExternalConsentRevocationOptions
{
    public const string SectionName = "Offboarding:ExternalConsentRevocation";
    public bool Enabled { get; set; }
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/";
    public string ClientApplicationId { get; set; } = string.Empty;
    public EnterpriseApplicationOffboardingPolicy EnterpriseApplicationPolicy { get; set; } = EnterpriseApplicationOffboardingPolicy.Preserve;
    public string? PowerPlatformRbacEndpoint { get; set; }
    public string PowerPlatformRbacResourceScope { get; set; } = CollectorResources.PowerPlatform;

    public void Validate()
    {
        if (!Enabled) return;
        if (!Guid.TryParse(ClientApplicationId, out var clientId) || clientId == Guid.Empty)
            throw new InvalidOperationException($"{SectionName}:ClientApplicationId must be a non-empty GUID when offboarding is enabled.");
        if (!Uri.TryCreate(GraphBaseUrl, UriKind.Absolute, out var graphBaseUri) || graphBaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(graphBaseUri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            graphBaseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(graphBaseUri.Query) || !string.IsNullOrEmpty(graphBaseUri.Fragment))
            throw new InvalidOperationException($"{SectionName}:GraphBaseUrl must be the HTTPS Microsoft Graph root https://graph.microsoft.com/.");
        if (!Enum.IsDefined(EnterpriseApplicationPolicy))
            throw new InvalidOperationException($"{SectionName}:EnterpriseApplicationPolicy must be Preserve, Disable, or Remove.");
        if (PowerPlatformRbacEndpoint is null) return;
        if (!Uri.TryCreate(PowerPlatformRbacEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !AllowedPowerPlatformHosts.Contains(endpoint.Host) ||
            !PowerPlatformRbacEndpoint.Contains("{assignmentId}", StringComparison.Ordinal) ||
            !AllowedPowerPlatformResources.Contains(PowerPlatformRbacResourceScope))
            throw new InvalidOperationException($"{SectionName}:PowerPlatformRbacEndpoint is not an allowlisted HTTPS assignment endpoint template/resource pair.");
    }

    private static readonly HashSet<string> AllowedPowerPlatformHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.powerplatform.com", "api.bap.microsoft.com"
    };

    private static readonly HashSet<string> AllowedPowerPlatformResources = new(StringComparer.Ordinal)
    {
        CollectorResources.PowerPlatform, "https://api.bap.microsoft.com/.default"
    };
}

public sealed record ExternalRevocationResponse(
    HttpStatusCode StatusCode,
    byte[] Body,
    string? RequestId,
    string ResponseSha256);

public interface IExternalRevocationTransport
{
    ValueTask<ExternalRevocationResponse> SendAsync(
        Guid tenantId,
        string resourceScope,
        HttpMethod method,
        Uri endpoint,
        string? jsonBody,
        CancellationToken cancellationToken);
}

public sealed class ExternalConsentRevocationAdapter(
    ExternalConsentRevocationOptions options,
    ICustomerStore customers,
    ITenantConnectionStore connections,
    IExternalRevocationTransport transport) : IExternalConsentRevocationAdapter
{
    private readonly Uri graphRoot = new(options.GraphBaseUrl, UriKind.Absolute);

    public async ValueTask<ExternalConsentRevocationResult> RevokeAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var evidence = new List<ExternalConsentRevocationEvidence>();
        var customer = await customers.FindCustomerAsync(customerId, cancellationToken);
        var connection = await connections.FindAsync(customerId, cancellationToken);
        if (customer is null || connection is null || customer.EntraTenantId == Guid.Empty ||
            connection.ServicePrincipalObjectId is not { } storedServicePrincipalId)
            return Result(ExternalConsentRevocationStatus.Failed, customerId, customer?.EntraTenantId, evidence,
                "Verified customer tenant and service-principal identity are required.");

        try
        {
            var clientApplicationId = Guid.Parse(options.ClientApplicationId);
            var servicePrincipals = await GraphAsync(customer.EntraTenantId, HttpMethod.Get,
                $"v1.0/servicePrincipals?$filter=appId eq '{clientApplicationId:D}'&$select=id,appId,accountEnabled", null,
                "graph.resolveServicePrincipal", evidence, cancellationToken);
            if (!servicePrincipals.IsSuccess())
                return Result(ExternalConsentRevocationStatus.Failed, customerId, customer.EntraTenantId, evidence, "Microsoft Graph could not verify the enterprise application.");

            var resolved = ReadValues(servicePrincipals.Body);
            if (resolved.Count > 1 || resolved.Count == 1 &&
                (!TryGuid(resolved[0], "id", out var resolvedId) || resolvedId != storedServicePrincipalId ||
                 !TryGuid(resolved[0], "appId", out var resolvedAppId) || resolvedAppId != clientApplicationId))
                return Result(ExternalConsentRevocationStatus.Failed, customerId, customer.EntraTenantId, evidence, "Graph returned a wrong or non-unique tenant service principal; no write was attempted.");

            var grantValues = new List<JsonElement>();
            var nextPage = new Uri(graphRoot, $"v1.0/oauth2PermissionGrants?$filter=clientId eq '{storedServicePrincipalId:D}'&$select=id,clientId");
            while (nextPage is not null)
            {
                if (nextPage.Scheme != Uri.UriSchemeHttps || !string.Equals(nextPage.Host, graphRoot.Host, StringComparison.OrdinalIgnoreCase))
                    return Result(ExternalConsentRevocationStatus.Failed, customerId, customer.EntraTenantId, evidence, "Graph pagination left the configured Graph origin; no write was attempted.");
                var grants = await SendAsync(customer.EntraTenantId, CollectorResources.Graph, HttpMethod.Get, nextPage, null,
                    "graph.listDelegatedPermissionGrants", evidence, cancellationToken);
                if (!grants.IsSuccess())
                    return Result(ExternalConsentRevocationStatus.Failed, customerId, customer.EntraTenantId, evidence, "Microsoft Graph could not enumerate delegated permission grants.");
                grantValues.AddRange(ReadValues(grants.Body));
                nextPage = ReadNextLink(grants.Body);
            }
            if (grantValues.Any(value => !TryGuid(value, "id", out _) ||
                !TryGuid(value, "clientId", out var clientId) || clientId != storedServicePrincipalId))
                return Result(ExternalConsentRevocationStatus.Failed, customerId, customer.EntraTenantId, evidence, "Graph returned a grant for the wrong service principal; no grant was removed.");

            var changed = false;
            foreach (var grant in grantValues)
            {
                TryGuid(grant, "id", out var grantId);
                var deleted = await GraphAsync(customer.EntraTenantId, HttpMethod.Delete,
                    $"v1.0/oauth2PermissionGrants/{grantId:D}", null, "graph.deleteDelegatedPermissionGrant", evidence, cancellationToken);
                if (!deleted.IsDeleteSuccess())
                    return Result(ExternalConsentRevocationStatus.Partial, customerId, customer.EntraTenantId, evidence, "At least one delegated permission grant could not be verified as removed.");
                changed |= deleted.StatusCode != HttpStatusCode.NotFound;
            }

            var servicePrincipalExists = resolved.Count == 1;
            if (servicePrincipalExists && options.EnterpriseApplicationPolicy == EnterpriseApplicationOffboardingPolicy.Disable)
            {
                var disabled = await GraphAsync(customer.EntraTenantId, HttpMethod.Patch,
                    $"v1.0/servicePrincipals/{storedServicePrincipalId:D}", "{\"accountEnabled\":false}",
                    "graph.disableServicePrincipal", evidence, cancellationToken);
                if (!disabled.IsWriteSuccess())
                    return Result(ExternalConsentRevocationStatus.Partial, customerId, customer.EntraTenantId, evidence, "Delegated grants were handled, but the enterprise application could not be disabled.");
                changed |= disabled.StatusCode != HttpStatusCode.NotFound;
            }
            else if (servicePrincipalExists && options.EnterpriseApplicationPolicy == EnterpriseApplicationOffboardingPolicy.Remove)
            {
                var removed = await GraphAsync(customer.EntraTenantId, HttpMethod.Delete,
                    $"v1.0/servicePrincipals/{storedServicePrincipalId:D}", null,
                    "graph.removeServicePrincipal", evidence, cancellationToken);
                if (!removed.IsDeleteSuccess())
                    return Result(ExternalConsentRevocationStatus.Partial, customerId, customer.EntraTenantId, evidence, "Delegated grants were handled, but the enterprise application could not be removed.");
                changed |= removed.StatusCode != HttpStatusCode.NotFound;
            }

            if (!string.IsNullOrWhiteSpace(connection.RbacRoleAssignmentId))
            {
                if (string.Equals(connection.RbacRoleAssignmentId, "verified", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(options.PowerPlatformRbacEndpoint))
                    return Result(ExternalConsentRevocationStatus.PendingManualAction, customerId, customer.EntraTenantId, evidence,
                        "Power Platform RBAC was verified during onboarding, but no concrete assignment ID and allowlisted removal endpoint are configured.");

                var endpoint = new Uri(options.PowerPlatformRbacEndpoint.Replace(
                    "{assignmentId}", Uri.EscapeDataString(connection.RbacRoleAssignmentId), StringComparison.Ordinal));
                var rbac = await SendAsync(customer.EntraTenantId, options.PowerPlatformRbacResourceScope, HttpMethod.Delete,
                    endpoint, null, "powerPlatform.deleteRbacAssignment", evidence, cancellationToken);
                if (!rbac.IsDeleteSuccess())
                    return Result(ExternalConsentRevocationStatus.Partial, customerId, customer.EntraTenantId, evidence, "Power Platform RBAC removal was not verified and requires manual action.");
                changed |= rbac.StatusCode != HttpStatusCode.NotFound;
            }

            return Result(changed ? ExternalConsentRevocationStatus.Succeeded : ExternalConsentRevocationStatus.AlreadyRevoked,
                customerId, customer.EntraTenantId,
                evidence, changed ? "External delegated consent and configured local access were revoked." : "External consent was already revoked.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(evidence.Count > 0 ? ExternalConsentRevocationStatus.Partial : ExternalConsentRevocationStatus.Failed,
                customerId, customer?.EntraTenantId,
                evidence, $"External revocation failed closed: {exception.GetType().Name}.");
        }
    }

    private ValueTask<ExternalRevocationResponse> GraphAsync(Guid tenantId, HttpMethod method, string relativeUri,
        string? jsonBody, string operation, List<ExternalConsentRevocationEvidence> evidence, CancellationToken cancellationToken) =>
        SendAsync(tenantId, CollectorResources.Graph, method, new Uri(graphRoot, relativeUri), jsonBody, operation, evidence, cancellationToken);

    private async ValueTask<ExternalRevocationResponse> SendAsync(Guid tenantId, string resourceScope, HttpMethod method,
        Uri endpoint, string? jsonBody, string operation, List<ExternalConsentRevocationEvidence> evidence, CancellationToken cancellationToken)
    {
        var response = await transport.SendAsync(tenantId, resourceScope, method, endpoint, jsonBody, cancellationToken);
        evidence.Add(new(operation, endpoint.AbsoluteUri, (int)response.StatusCode, response.RequestId, response.ResponseSha256));
        return response;
    }

    private ExternalConsentRevocationResult Result(ExternalConsentRevocationStatus status, Guid customerId, Guid? tenantId,
        IReadOnlyCollection<ExternalConsentRevocationEvidence> evidence, string detail)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            customerId,
            tenantId,
            options.GraphBaseUrl,
            options.ClientApplicationId,
            options.EnterpriseApplicationPolicy,
            options.PowerPlatformRbacEndpoint,
            evidence
        });
        var hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new(status, evidence.Count == 0 ? null : $"sha256:{hash}", evidence.ToArray(), detail);
    }

    private static List<JsonElement> ReadValues(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("value").EnumerateArray().Select(value => value.Clone()).ToList();
    }

    private static Uri? ReadNextLink(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) ? uri : null;
    }

    private static bool TryGuid(JsonElement value, string propertyName, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(propertyName, out var property) && Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
    }
}

public sealed class MicrosoftExternalRevocationTransport(
    HttpClient httpClient,
    IAppOnlyTokenAcquirer tokens,
    TimeProvider timeProvider) : IExternalRevocationTransport
{
    public async ValueTask<ExternalRevocationResponse> SendAsync(Guid tenantId, string resourceScope, HttpMethod method,
        Uri endpoint, string? jsonBody, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await tokens.GetAccessTokenAsync(tenantId, resourceScope, cancellationToken));
            request.Headers.TryAddWithoutValidation("client-request-id", Guid.NewGuid().ToString("D"));
            if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
            {
                await Task.Delay(ConsentRetryPolicy.GetDelay(response, attempt, timeProvider), timeProvider, cancellationToken);
                continue;
            }
            return new(response.StatusCode, body, ReadRequestId(response),
                Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        }
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "request-id", "x-ms-request-id", "client-request-id" })
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return null;
    }
}

internal static class ExternalRevocationResponseExtensions
{
    public static bool IsSuccess(this ExternalRevocationResponse response) =>
        (int)response.StatusCode is >= 200 and < 300;

    public static bool IsDeleteSuccess(this ExternalRevocationResponse response) =>
        response.IsSuccess() || response.StatusCode == HttpStatusCode.NotFound;

    public static bool IsWriteSuccess(this ExternalRevocationResponse response) => response.IsDeleteSuccess();
}