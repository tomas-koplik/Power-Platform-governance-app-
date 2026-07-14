using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Transport;

public sealed class CollectorTransportOptions
{
    public const string SectionName = "Collectors:Transport";
    public int MaxPerTenantConcurrency { get; set; } = 4;
    public int MaxRetryAttempts { get; set; } = 4;
    public int MaxRetryDelaySeconds { get; set; } = 60;
    public Dictionary<string, List<string>> AllowedRoutePrefixes { get; set; } = new(StringComparer.Ordinal)
    {
        [CollectorResources.PowerPlatform] = ["https://api.powerplatform.com/"],
        [CollectorResources.BusinessApplications] = ["https://api.bap.microsoft.com/", "https://api.powerapps.com/"],
        [CollectorResources.Flow] = ["https://api.flow.microsoft.com/"],
        [CollectorResources.Graph] = ["https://graph.microsoft.com/"]
    };
}

public interface ICollectorDestinationResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class CollectorDestinationResolver : ICollectorDestinationResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class CollectorDestinationPolicy(
    CollectorTransportOptions options,
    ICollectorDestinationResolver resolver)
{
    public async ValueTask ValidateAsync(
        Uri uri,
        string resourceScope,
        Uri initialUri,
        bool isContinuation,
        CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Collector destinations must use absolute HTTPS URIs.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Collector destinations must not contain user information.");
        if (isContinuation && !SameOrigin(initialUri, uri))
            throw new InvalidOperationException("Collector pagination must remain on the initial route origin.");
        if (!options.AllowedRoutePrefixes.TryGetValue(resourceScope, out var prefixes) ||
            !prefixes.Any(prefix => IsAllowedPrefix(uri, prefix)))
            throw new InvalidOperationException("Collector destination does not match the token resource allowlist.");

        var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
            ? [literal]
            : await resolver.ResolveAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
            throw new InvalidOperationException("Collector destinations must resolve only to public addresses.");
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsAllowedPrefix(Uri uri, string prefixValue)
    {
        if (!Uri.TryCreate(prefixValue, UriKind.Absolute, out var prefix) ||
            !string.Equals(prefix.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !SameOrigin(uri, prefix)) return false;
        var allowedPath = prefix.AbsolutePath.EndsWith('/') ? prefix.AbsolutePath : prefix.AbsolutePath + "/";
        var candidatePath = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return candidatePath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 || bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                bytes[0] == 0;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            (bytes[0] & 0xfe) == 0xfc || address.Equals(IPAddress.IPv6None);
    }
}

public sealed record CollectorApiRequest(
    HttpMethod Method,
    Uri Uri,
    string ResourceScope,
    string ApiVersion,
    HttpContent? Content = null);

public sealed record PagedCollectionResult(
    SectionCoverage Coverage,
    int ItemCount,
    IReadOnlyCollection<RawEvidenceReference> Evidence,
    IReadOnlyCollection<string> Warnings);

public interface ICollectorHttpPipeline
{
    Task<PagedCollectionResult> CollectPagesAsync(
        SnapshotCollectorContext context,
        string sectionKey,
        CollectorApiRequest initialRequest,
        ISnapshotEvidenceSink evidenceSink,
        CancellationToken cancellationToken);
}

public sealed class TenantConcurrencyLimiter(CollectorTransportOptions options)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _tenantLimits = new();

    public async ValueTask<IDisposable> EnterAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var concurrency = Math.Max(1, options.MaxPerTenantConcurrency);
        var semaphore = _tenantLimits.GetOrAdd(customerId, _ => new SemaphoreSlim(concurrency, concurrency));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

public sealed class CollectorHttpPipeline(
    HttpClient httpClient,
    ICollectorTokenProvider tokens,
    ICollectorCheckpointStore checkpoints,
    ISectionProgressSink progress,
    TenantConcurrencyLimiter concurrencyLimiter,
    CollectorDestinationPolicy destinationPolicy,
    CollectorTransportOptions options,
    TimeProvider timeProvider) : ICollectorHttpPipeline
{
    public async Task<PagedCollectionResult> CollectPagesAsync(
        SnapshotCollectorContext context,
        string sectionKey,
        CollectorApiRequest initialRequest,
        ISnapshotEvidenceSink evidenceSink,
        CancellationToken cancellationToken)
    {
        await destinationPolicy.ValidateAsync(
            initialRequest.Uri, initialRequest.ResourceScope, initialRequest.Uri, false, cancellationToken);
        var evidence = new List<RawEvidenceReference>();
        var warnings = new List<string>();
        var checkpoint = await checkpoints.ReadAsync(context.CustomerId, context.SnapshotId, sectionKey, cancellationToken);
        if (checkpoint is not null) evidence.AddRange(checkpoint.Evidence);
        var incompleteResumedChain = checkpoint is { CompletedPages: > 0 } && checkpoint.Evidence.Count < checkpoint.CompletedPages;
        if (incompleteResumedChain)
            warnings.Add("Resumed checkpoint does not contain a complete evidence chain for previously captured pages.");
        var nextUri = checkpoint?.ContinuationToken is { Length: > 0 } continuation
            ? new Uri(continuation, UriKind.Absolute)
            : initialRequest.Uri;
        var page = checkpoint?.CompletedPages ?? 0;
        var itemCount = checkpoint?.ItemCount ?? 0;

        await progress.PublishAsync(new(
            context.CustomerId, context.SnapshotId, sectionKey, SectionProgressStatus.Started,
            page, itemCount, null, checkpoint is null ? null : "Resuming from checkpoint.", timeProvider.GetUtcNow()), cancellationToken);

        while (nextUri is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destinationPolicy.ValidateAsync(
                nextUri, initialRequest.ResourceScope, initialRequest.Uri, nextUri != initialRequest.Uri, cancellationToken);
            using var lease = await concurrencyLimiter.EnterAsync(context.CustomerId, cancellationToken);
            var sendResult = await SendWithRetryAsync(
                context, sectionKey, initialRequest, nextUri, page + 1, evidenceSink, evidence, cancellationToken);
            using var response = sendResult.Response;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var captured = CreateEvidence(
                context, sectionKey, evidence.Count, initialRequest, nextUri, response, body,
                page + 1, sendResult.Attempt, evidence.LastOrDefault()?.RawEvidenceReferenceId,
                incompleteResumedChain ? "Incomplete resumed evidence chain." : "Page captured; final completeness is evaluated by the collector.");
            await using (var stream = new MemoryStream(body, writable: false))
            {
                await evidenceSink.WriteRawAsync(captured, stream, cancellationToken);
            }
            evidence.Add(captured);

            if (!response.IsSuccessStatusCode)
            {
                var reason = $"Endpoint returned {(int)response.StatusCode} ({response.StatusCode}); raw response was retained.";
                warnings.Add(reason);
                var coverage = page > 0 ? SectionCoverage.Partial : SectionCoverage.Failed;
                return new(coverage, itemCount, evidence, warnings);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                itemCount += CountItems(document.RootElement);
                nextUri = ReadNextLink(document.RootElement);
            }
            catch (JsonException exception)
            {
                warnings.Add($"Page {page + 1} was retained but could not be parsed: {exception.Message}");
                return new(page > 0 ? SectionCoverage.Partial : SectionCoverage.Failed, itemCount, evidence, warnings);
            }

            page++;
            await checkpoints.WriteAsync(new(
                context.CustomerId, context.SnapshotId, sectionKey, nextUri?.AbsoluteUri,
                page, itemCount, evidence.Select(item => item.RawEvidenceReferenceId).ToArray(),
                evidence.ToArray(), timeProvider.GetUtcNow()), cancellationToken);
            await progress.PublishAsync(new(
                context.CustomerId, context.SnapshotId, sectionKey, SectionProgressStatus.PageCaptured,
                page, itemCount, null, null, timeProvider.GetUtcNow()), cancellationToken);
        }

        await checkpoints.CompleteAsync(context.CustomerId, context.SnapshotId, sectionKey, cancellationToken);
        await progress.PublishAsync(new(
            context.CustomerId, context.SnapshotId, sectionKey, SectionProgressStatus.Completed,
            page, itemCount, SectionCoverage.Full, null, timeProvider.GetUtcNow()), cancellationToken);
        return new(incompleteResumedChain ? SectionCoverage.Partial : SectionCoverage.Full, itemCount, evidence, warnings);
    }

    private async Task<SendResult> SendWithRetryAsync(
        SnapshotCollectorContext context,
        string sectionKey,
        CollectorApiRequest apiRequest,
        Uri uri,
        int pageNumber,
        ISnapshotEvidenceSink evidenceSink,
        List<RawEvidenceReference> evidence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(apiRequest.Method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(new(
                context.EntraTenantId, context.Mode, apiRequest.ResourceScope, context.CapturedIdentity,
                context.AuthenticatedPrincipal), cancellationToken));
            if (apiRequest.Content is not null)
            {
                var bytes = await apiRequest.Content.ReadAsByteArrayAsync(cancellationToken);
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = apiRequest.Content.Headers.ContentType;
            }

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!ShouldRetry(response.StatusCode) || attempt >= Math.Max(0, options.MaxRetryAttempts))
                return new(response, attempt + 1);

            var retryBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var captured = CreateEvidence(
                context, sectionKey, evidence.Count, apiRequest, uri, response, retryBody,
                pageNumber, attempt + 1, evidence.LastOrDefault()?.RawEvidenceReferenceId,
                "Transient response retained before retry; this attempt did not complete the page.");
            await using (var stream = new MemoryStream(retryBody, writable: false))
            {
                await evidenceSink.WriteRawAsync(captured, stream, cancellationToken);
            }
            evidence.Add(captured);
            var delay = GetRetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var maximum = TimeSpan.FromSeconds(Math.Max(1, options.MaxRetryDelaySeconds));
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
        {
            retryAfter = date - timeProvider.GetUtcNow();
        }

        var basis = retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero
            ? serverDelay
            : TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var bounded = basis > maximum ? maximum : basis;
        var jitterMilliseconds = Random.Shared.Next(25, 251);
        return bounded + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private RawEvidenceReference CreateEvidence(
        SnapshotCollectorContext context,
        string sectionKey,
        int sequence,
        CollectorApiRequest request,
        Uri uri,
        HttpResponseMessage response,
        byte[] body,
        int pageNumber,
        int attemptNumber,
        Guid? previousEvidenceId,
        string completenessRationale)
    {
        var evidenceId = Guid.NewGuid();
        var contentType = response.Content.Headers.ContentType;
        var extension = string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ? "json" : "bin";
        var storagePath = RawEvidenceBlobPath.Derive(
            context.CustomerId, context.SnapshotId, sectionKey, pageNumber, attemptNumber, evidenceId, extension);
        return new(
            evidenceId,
            context.CustomerId,
            context.SnapshotId,
            sectionKey,
            storagePath,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
            contentType?.MediaType ?? "application/octet-stream",
            request.ApiVersion,
            timeProvider.GetUtcNow(),
            context.Confidence switch
            {
                CollectorConfidence.Preview => EvidenceConfidence.Preview,
                CollectorConfidence.PocRequired => EvidenceConfidence.PocRequired,
                _ => EvidenceConfidence.Documented
            },
            sectionKey,
            typeof(CollectorHttpPipeline).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            "raw-json-v1",
            context.Confidence == CollectorConfidence.PocRequired ? request.ApiVersion : null,
            request.Method.Method,
            SanitizeUri(uri),
            (int)response.StatusCode,
            request.ResourceScope,
            context.Mode,
            context.EntraTenantId,
            context.Tenant.Subject.ToString(),
            ReadRequestId(response),
            ReadRedactedHeaders(response),
            pageNumber,
            attemptNumber,
            previousEvidenceId,
            completenessRationale);
    }

    private static string SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        if (string.IsNullOrEmpty(builder.Query)) return builder.Uri.AbsoluteUri;
        var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "token", "sig", "signature", "code", "key" };
        var pairs = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => sensitive.Contains(Uri.UnescapeDataString(parts[0]))
                ? $"{parts[0]}=REDACTED"
                : string.Join('=', parts));
        builder.Query = string.Join('&', pairs);
        return builder.Uri.AbsoluteUri;
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "request-id", "x-ms-request-id", "client-request-id" })
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadRedactedHeaders(HttpResponseMessage response)
    {
        var allowed = new[] { "request-id", "x-ms-request-id", "client-request-id", "Retry-After" };
        return allowed.Where(name => response.Headers.Contains(name)).ToDictionary(
            name => name,
            name => string.Join(',', response.Headers.GetValues(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SendResult(HttpResponseMessage Response, int Attempt);

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static int CountItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength();
        if (root.ValueKind != JsonValueKind.Object) return 1;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array) return value.GetArrayLength();
        return 1;
    }

    private static Uri? ReadNextLink(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "@odata.nextLink", "nextLink", "nextlink" })
        {
            if (!root.TryGetProperty(name, out var next)) continue;
            if (next.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Pagination property '{name}' is not a valid absolute URI.");
            return uri;
        }
        return null;
    }
}