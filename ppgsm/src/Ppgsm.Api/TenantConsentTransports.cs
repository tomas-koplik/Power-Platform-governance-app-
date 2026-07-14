using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Ppgsm.Collectors.Authentication;

namespace Ppgsm.Api;

public sealed class MicrosoftGraphConsentTransport(
    HttpClient httpClient,
    IAppOnlyTokenAcquirer tokens,
    TimeProvider timeProvider) : IConsentGraphTransport
{
    private static readonly HashSet<string> AdminRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Global Administrator", "Power Platform Administrator", "Dynamics 365 Administrator"
    };

    public async ValueTask<GraphConsentFacts> ReadAsync(
        Guid tenantId,
        Guid clientApplicationId,
        Guid resourceApplicationId,
        Guid adminObjectId,
        CancellationToken cancellationToken)
    {
        var client = await FindServicePrincipalAsync(tenantId, clientApplicationId, cancellationToken);
        var resource = await FindServicePrincipalAsync(tenantId, resourceApplicationId, cancellationToken);
        if (client is null || resource is null)
            return new(tenantId, client?.Id, client?.AccountEnabled == true, [], false, "Expected local service principal was not uniquely resolved.");

        var grants = await GetAsync(tenantId,
            $"v1.0/oauth2PermissionGrants?$filter=clientId eq '{client.Id}' and resourceId eq '{resource.Id}'&$select=scope,consentType",
            cancellationToken);
        var scopes = grants.RootElement.GetProperty("value").EnumerateArray()
            .Where(value => string.Equals(value.GetProperty("consentType").GetString(), "AllPrincipals", StringComparison.Ordinal))
            .SelectMany(value => (value.GetProperty("scope").GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var assignments = await GetAsync(tenantId,
            $"v1.0/roleManagement/directory/roleAssignments?$filter=principalId eq '{adminObjectId:D}'&$expand=roleDefinition($select=displayName)",
            cancellationToken);
        var roleNames = assignments.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.TryGetProperty("roleDefinition", out var definition) && definition.TryGetProperty("displayName", out var name)
                ? name.GetString() : null)
            .Where(value => value is not null).Cast<string>().ToArray();
        return new(tenantId, client.Id, client.AccountEnabled, scopes, roleNames.Any(AdminRoles.Contains),
            $"Resolved client service principal and {scopes.Length} delegated scopes; administrator role evidence: {string.Join(", ", roleNames)}.");
    }

    private async ValueTask<ServicePrincipal?> FindServicePrincipalAsync(Guid tenantId, Guid appId, CancellationToken cancellationToken)
    {
        var document = await GetAsync(tenantId,
            $"v1.0/servicePrincipals?$filter=appId eq '{appId:D}'&$select=id,appId,accountEnabled", cancellationToken);
        var values = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (values.Length != 1) return null;
        var value = values[0];
        return new(value.GetProperty("id").GetString()!, value.TryGetProperty("accountEnabled", out var enabled) && enabled.GetBoolean());
    }

    private async ValueTask<JsonDocument> GetAsync(Guid tenantId, string relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri("https://graph.microsoft.com/"), relativeUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.GetAccessTokenAsync(tenantId, CollectorResources.Graph, cancellationToken));
        using var response = await SendWithRetryAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Graph consent verification failed with HTTP {(int)response.StatusCode}.");
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            clone.Headers.Authorization = request.Headers.Authorization;
            var response = await httpClient.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) || attempt >= 3)
                return response;
            var delay = ConsentRetryPolicy.GetDelay(response, attempt, timeProvider);
            response.Dispose();
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private sealed record ServicePrincipal(string Id, bool AccountEnabled);
}

public sealed class PowerPlatformConsentCapabilityProbeTransport(
    HttpClient httpClient,
    IAppOnlyTokenAcquirer tokens,
    TimeProvider timeProvider) : IConsentCapabilityProbeTransport
{
    public async ValueTask<EndpointCapabilityVerification> ProbeAsync(
        Guid tenantId,
        AuthenticatedCallbackIdentity adminIdentity,
        ConsentCapabilityProbe probe,
        CancellationToken cancellationToken)
    {
        probe.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Get, probe.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.GetAccessTokenAsync(tenantId, probe.ResourceScope, cancellationToken));
        using var response = await SendWithRetryAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var requestId = ReadRequestId(response);
        var state = probe.Preview ? CapabilityVerificationState.PreviewOnly : response.StatusCode switch
        {
            HttpStatusCode.OK => CapabilityVerificationState.Verified,
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => CapabilityVerificationState.Unavailable,
            _ => CapabilityVerificationState.Unknown
        };
        return new(probe.Endpoint, "app-only", state, (int)response.StatusCode, requestId, hash,
            $"{probe.Name} returned HTTP {(int)response.StatusCode}{(probe.Preview ? " from a preview-only API" : string.Empty)}; raw body represented by SHA-256 evidence input.");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            clone.Headers.Authorization = request.Headers.Authorization;
            var response = await httpClient.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) || attempt >= 3)
                return response;
            var delay = ConsentRetryPolicy.GetDelay(response, attempt, timeProvider);
            response.Dispose();
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "request-id", "x-ms-request-id", "client-request-id" })
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return null;
    }
}

internal static class ConsentRetryPolicy
{
    public static TimeSpan GetDelay(HttpResponseMessage response, int attempt, TimeProvider timeProvider)
    {
        var serverDelay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - timeProvider.GetUtcNow() : null);
        var basis = serverDelay is { } requested && requested > TimeSpan.Zero
            ? requested
            : TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var bounded = basis > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : basis;
        return bounded + TimeSpan.FromMilliseconds(Random.Shared.Next(25, 251));
    }
}