using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors;

public sealed class SnapshotCollectorOrchestrator(
    IEnumerable<ISnapshotCollector> collectors,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyCollection<SnapshotSection>> ExecuteAsync(
        SnapshotCollectorContext context,
        ISnapshotEvidenceSink evidenceSink,
        IReadOnlyCollection<string>? requestedSections,
        CancellationToken cancellationToken)
    {
        var selected = requestedSections is null || requestedSections.Count == 0
            ? collectors.ToArray()
            : collectors.Where(collector => requestedSections.Contains(collector.SectionKey, StringComparer.OrdinalIgnoreCase)).ToArray();
        var unknown = requestedSections is null
            ? []
            : requestedSections.Where(requested => selected.All(collector => !string.Equals(collector.SectionKey, requested, StringComparison.OrdinalIgnoreCase))).ToArray();
        var sections = new List<SnapshotSection>(selected.Length + unknown.Length);

        foreach (var sectionKey in unknown)
        {
            sections.Add(new(
                Guid.NewGuid(), context.CustomerId, context.SnapshotId, sectionKey,
                SectionCoverage.Skipped, 0, "No collector is registered for the requested section.", timeProvider.GetUtcNow()));
        }

        foreach (var collector in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SectionResult result;
            try
            {
                result = await collector.CollectAsync(context with
                {
                    Confidence = collector.Confidence,
                    RequestedSections = requestedSections
                }, evidenceSink, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = new(SectionCoverage.Failed, 0, [], [$"Collector failed: {exception.GetType().Name}: {exception.Message}"]);
            }

            var reason = result.Coverage is SectionCoverage.Failed or SectionCoverage.Skipped
                ? string.Join(" ", result.Warnings)
                : result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;
            sections.Add(new(
                Guid.NewGuid(),
                context.CustomerId,
                context.SnapshotId,
                collector.SectionKey,
                result.Coverage,
                result.ItemCount,
                reason,
                timeProvider.GetUtcNow()));
        }
        return sections;
    }
}