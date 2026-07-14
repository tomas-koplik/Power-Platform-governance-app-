using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Ppgsm.Core.Domain;

namespace Ppgsm.Collectors;

public sealed class RawEvidenceBlobOptions
{
    public const string SectionName = "Azure:Blob";
    public Uri? Endpoint { get; init; }
    public string ContainerName { get; init; } = "raw-snapshots";
}

public static partial class RawEvidenceBlobPath
{
    public static string Derive(
        Guid customerId,
        Guid snapshotId,
        string sectionKey,
        int pageNumber,
        int attemptNumber,
        Guid evidenceId,
        string extension)
    {
        if (customerId == Guid.Empty || snapshotId == Guid.Empty || evidenceId == Guid.Empty)
            throw new ArgumentException("Evidence path identities must not be empty.");
        if (!SafeSegment().IsMatch(sectionKey)) throw new ArgumentException("Section key is not safe for evidence storage.", nameof(sectionKey));
        if (pageNumber < 1 || attemptNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (extension is not ("json" or "bin")) throw new ArgumentException("Unsupported evidence extension.", nameof(extension));
        return $"{customerId:D}/{snapshotId:D}/{sectionKey}/{pageNumber:D6}-{attemptNumber:D3}-{evidenceId:N}.{extension}";
    }

    public static bool IsCanonical(string path)
    {
        var match = CanonicalPath().Match(path);
        return match.Success
            && Guid.TryParseExact(match.Groups["customer"].Value, "D", out _)
            && Guid.TryParseExact(match.Groups["snapshot"].Value, "D", out _)
            && Guid.TryParseExact(match.Groups["evidence"].Value, "N", out _);
    }

    public static bool BelongsToCustomer(string path, Guid customerId) =>
        IsCanonical(path) && path.StartsWith($"{customerId:D}/", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();

    [GeneratedRegex("^(?<customer>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(?<snapshot>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/[A-Za-z0-9][A-Za-z0-9._-]{0,99}/[0-9]{6}-[0-9]{3}-(?<evidence>[0-9a-f]{32})\\.(json|bin)$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPath();
}

public sealed class AzureBlobRawEvidenceContentStore
    : IRawEvidenceContentStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobRawEvidenceContentStore(RawEvidenceBlobOptions options, TokenCredential credential)
    {
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri)
            throw new InvalidOperationException("Azure:Blob:Endpoint must be an absolute Blob service endpoint.");
        if (string.IsNullOrWhiteSpace(options.ContainerName))
            throw new InvalidOperationException("Azure:Blob:ContainerName is required.");
        _container = new BlobServiceClient(options.Endpoint, credential).GetBlobContainerClient(options.ContainerName);
    }

    public AzureBlobRawEvidenceContentStore(BlobContainerClient container) => _container = container;

    public async ValueTask WriteAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken)
    {
        var extension = string.Equals(evidence.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ? "json" : "bin";
        var expectedPath = RawEvidenceBlobPath.Derive(
            evidence.CustomerId, evidence.SnapshotId, evidence.SectionKey, evidence.PageNumber,
            evidence.AttemptNumber, evidence.RawEvidenceReferenceId, extension);
        if (!string.Equals(evidence.StoragePath, expectedPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence storage path does not match its immutable identity.");

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sha256"] = evidence.ContentHash,
            ["customerId"] = evidence.CustomerId.ToString("D"),
            ["snapshotId"] = evidence.SnapshotId.ToString("D"),
            ["section"] = evidence.SectionKey,
            ["page"] = evidence.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["evidenceId"] = evidence.RawEvidenceReferenceId.ToString("N")
        };
        await _container.GetBlobClient(expectedPath).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = evidence.MediaType },
            Metadata = metadata,
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        }, cancellationToken);
    }

    public async ValueTask<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken)
    {
        EnsureCanonical(storagePath);
        try
        {
            return await _container.GetBlobClient(storagePath).OpenReadAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async ValueTask DeleteCustomerAsync(Guid customerId, IReadOnlyCollection<string> storagePaths, CancellationToken cancellationToken)
    {
        if (customerId == Guid.Empty) throw new ArgumentException("Customer ID is required.", nameof(customerId));
        if (storagePaths.Any(path => !RawEvidenceBlobPath.BelongsToCustomer(path, customerId)))
            throw new InvalidOperationException("Evidence deletion contains a non-canonical or cross-customer path.");
        foreach (var path in storagePaths.Distinct(StringComparer.Ordinal))
        {
            await _container.DeleteBlobIfExistsAsync(path, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }
    }

    private static void EnsureCanonical(string storagePath)
    {
        if (!RawEvidenceBlobPath.IsCanonical(storagePath))
            throw new InvalidOperationException("Evidence storage path is not canonical.");
    }
}