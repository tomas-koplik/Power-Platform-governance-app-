using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Ppgsm.Core.Domain;
using System.Security.Cryptography;

namespace Ppgsm.Collectors;

public sealed class ExportArtifactOptions
{
    public Uri? Endpoint { get; init; }
    public string ContainerName { get; init; } = "exports";
}

public sealed class AzureBlobExportArtifactStore : IExportArtifactStore, IExportDownloadAuthorizer
{
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;

    public AzureBlobExportArtifactStore(ExportArtifactOptions options, TokenCredential credential)
    {
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri)
            throw new InvalidOperationException("Azure:Blob:Endpoint must be an absolute Blob service endpoint.");
        if (string.IsNullOrWhiteSpace(options.ContainerName))
            throw new InvalidOperationException("Azure:Blob:ExportsContainerName is required.");
        _service = new BlobServiceClient(options.Endpoint, credential);
        _container = _service.GetBlobContainerClient(options.ContainerName);
    }

    public AzureBlobExportArtifactStore(BlobContainerClient container)
    {
        _service = container.GetParentBlobServiceClient();
        _container = container;
    }

    public async ValueTask<ExportArtifactDescriptor> WriteAsync(Guid customerId, Guid exportJobId, Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanSeek) throw new ArgumentException("Export artifact stream must be seekable for integrity verification.", nameof(content));
        var originalPosition = content.Position;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
        var contentLength = content.Position - originalPosition;
        content.Position = originalPosition;
        var path = Path(customerId, exportJobId);
        var response = await _container.GetBlobClient(path).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Metadata = new Dictionary<string, string>
            {
                ["customerId"] = customerId.ToString("D"),
                ["exportJobId"] = exportJobId.ToString("D"),
                ["contentHash"] = $"sha256-{hash}",
                ["contentLength"] = contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        }, cancellationToken);
        return new($"sha256:{hash}", contentLength, "application/json", response.Value.ETag.ToString());
    }

    public async ValueTask<Stream?> OpenReadAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken)
    {
        try
        {
            return await _container.GetBlobClient(Path(customerId, exportJobId)).OpenReadAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async ValueTask DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await foreach (var blob in _container.GetBlobsAsync(prefix: $"{customerId:D}/", cancellationToken: cancellationToken))
            await _container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
    }

    public async ValueTask<Uri?> CreateAuthorizedDownloadAsync(ExportJob job, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (job.Status != ExportJobStatus.Completed) return null;
        var blob = _container.GetBlobClient(Path(job.CustomerId, job.ExportJobId));
        if (!(await blob.ExistsAsync(cancellationToken)).Value) return null;
        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expiresOn = startsOn.Add(lifetime);
        var key = (await _service.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken)).Value;
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            ContentDisposition = $"attachment; filename=ppgsm-export-{job.ExportJobId:N}.json",
            ContentType = "application/json"
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return new UriBuilder(blob.Uri) { Query = sas.ToSasQueryParameters(key, _service.AccountName).ToString() }.Uri;
    }

    private static string Path(Guid customerId, Guid exportJobId)
    {
        if (customerId == Guid.Empty || exportJobId == Guid.Empty) throw new ArgumentException("Export artifact identities are required.");
        return $"{customerId:D}/{exportJobId:D}/evidence-package.json";
    }
}
