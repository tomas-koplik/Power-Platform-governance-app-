using Ppgsm.Core.Domain;

namespace Ppgsm.Worker;

public sealed class OffboardingJobWorker(
    ICustomerOffboardingStore jobs,
    CustomerOffboardingService service,
    TimeProvider timeProvider,
    ILogger<OffboardingJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        do
        {
            var jobIds = await jobs.ListExecutableDeletionJobsAsync(timeProvider.GetUtcNow(), stoppingToken);
            foreach (var jobId in jobIds)
            {
                try
                {
                    await service.ProcessAsync(jobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Offboarding failed for durable job {JobId}.", jobId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}