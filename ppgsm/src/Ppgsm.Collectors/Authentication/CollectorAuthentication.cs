using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Ppgsm.Core.Domain;

namespace Ppgsm.Collectors.Authentication;

public static class CollectorResources
{
    public const string PowerPlatform = "https://api.powerplatform.com/.default";
    public const string BusinessApplications = "https://service.powerapps.com//.default";
    public const string Flow = "https://service.flow.microsoft.com//.default";
    public const string Graph = "https://graph.microsoft.com/.default";
}

public sealed record CollectorTokenRequest(
    Guid EntraTenantId,
    SnapshotMode Mode,
    string ResourceScope,
    string CapturedIdentity,
    ClaimsPrincipal? AuthenticatedPrincipal);

public interface ICollectorTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CollectorTokenRequest request, CancellationToken cancellationToken);
}

public interface IDelegatedOboTokenAcquirer
{
    ValueTask<string> GetAccessTokenAsync(
        Guid entraTenantId,
        string resourceScope,
        ClaimsPrincipal authenticatedPrincipal,
        CancellationToken cancellationToken);
}

public sealed class MicrosoftIdentityWebOboTokenAcquirer(ITokenAcquisition tokenAcquisition) : IDelegatedOboTokenAcquirer
{
    public async ValueTask<string> GetAccessTokenAsync(
        Guid entraTenantId,
        string resourceScope,
        ClaimsPrincipal authenticatedPrincipal,
        CancellationToken cancellationToken)
    {
        if (authenticatedPrincipal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("A delegated snapshot requires the authenticated user identity.");
        }

        return await tokenAcquisition.GetAccessTokenForUserAsync(
            [resourceScope],
            tenantId: entraTenantId.ToString("D"),
            user: authenticatedPrincipal,
            tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
    }
}

public interface IAppOnlyTokenAcquirer
{
    ValueTask<string> GetAccessTokenAsync(Guid entraTenantId, string resourceScope, CancellationToken cancellationToken);
}

public sealed class AppOnlyCertificateOptions
{
    public const string SectionName = "Collectors:AppOnlyCertificate";
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public Uri? KeyVaultCertificateUri { get; set; }
}

public sealed class AzureIdentityCertificateTokenAcquirer(
    AppOnlyCertificateOptions options,
    TokenCredential credential) : IAppOnlyTokenAcquirer
{
    private readonly SemaphoreSlim _certificateLock = new(1, 1);
    private X509Certificate2? _certificate;

    public async ValueTask<string> GetAccessTokenAsync(
        Guid entraTenantId,
        string resourceScope,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("App-only certificate collection is disabled pending PoC verification.");
        }
        if (string.IsNullOrWhiteSpace(options.ClientId) || options.KeyVaultCertificateUri is null)
        {
            throw new InvalidOperationException("App-only collection requires a client ID and Key Vault certificate URI.");
        }

        var certificate = await GetCertificateAsync(cancellationToken);
        var application = ConfidentialClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, entraTenantId.ToString("D"))
            .WithCertificate(certificate, sendX5C: true)
            .Build();
        var result = await application.AcquireTokenForClient([resourceScope]).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private async ValueTask<X509Certificate2> GetCertificateAsync(CancellationToken cancellationToken)
    {
        if (_certificate is not null) return _certificate;

        await _certificateLock.WaitAsync(cancellationToken);
        try
        {
            if (_certificate is not null) return _certificate;

            var response = await credential.GetTokenAsync(
                new TokenRequestContext(["https://vault.azure.net/.default"]),
                cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, options.KeyVaultCertificateUri);
            request.Headers.Authorization = new("Bearer", response.Token);
            using var client = new HttpClient();
            using var certificateResponse = await client.SendAsync(request, cancellationToken);
            certificateResponse.EnsureSuccessStatusCode();
            var secret = await certificateResponse.Content.ReadFromJsonAsync<KeyVaultSecretValue>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Key Vault returned an empty certificate secret.");
            _certificate = new X509Certificate2(
                Convert.FromBase64String(secret.Value),
                (string?)null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            return _certificate;
        }
        finally
        {
            _certificateLock.Release();
        }
    }

    private sealed record KeyVaultSecretValue(string Value);
}

public sealed class CollectorTokenProvider(
    IDelegatedOboTokenAcquirer delegated,
    IAppOnlyTokenAcquirer appOnly) : ICollectorTokenProvider
{
    public ValueTask<string> GetAccessTokenAsync(CollectorTokenRequest request, CancellationToken cancellationToken) => request.Mode switch
    {
        SnapshotMode.Delegated => delegated.GetAccessTokenAsync(
            request.EntraTenantId,
            request.ResourceScope,
            request.AuthenticatedPrincipal ?? throw new InvalidOperationException(
                "Delegated OBO requires the request-scoped authenticated principal."),
            cancellationToken),
        SnapshotMode.AppOnly => appOnly.GetAccessTokenAsync(
            request.EntraTenantId,
            request.ResourceScope,
            cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unsupported snapshot mode.")
    };
}