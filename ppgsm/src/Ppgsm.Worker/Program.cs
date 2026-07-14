using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ppgsm.Collectors;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;
using Ppgsm.Data;
using Ppgsm.Worker;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "worker";
if (command == "schedule") command = "scheduler";
if (command is not ("worker" or "scheduler" or "migrate" or "exports"))
{
	throw new ArgumentException($"Unknown worker command '{command}'. Expected worker, scheduler, exports, or migrate.");
}

var builder = Host.CreateApplicationBuilder(args);
if (command == "migrate")
{
	var connectionString = builder.Configuration.GetConnectionString("Ppgsm")
		?? throw new InvalidOperationException("ConnectionStrings:Ppgsm is required for migrate.");
	var options = new DbContextOptionsBuilder<PpgsmDbContext>().UseSqlServer(connectionString).Options;
	var migrationTenant = new CurrentTenant();
	migrationTenant.Set(new(Guid.Empty,
		SubjectIdentity.Create(Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"), Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff2")),
		MembershipRole.InternalAdmin, IsInternal: true));
	await using var context = new PpgsmDbContext(options, migrationTenant);
	var migrator = context.GetService<IMigrator>();
	if (!(await context.Database.GetMigrationsAsync()).Any())
	{
		throw new InvalidOperationException("No EF migrations are embedded. Production schema migration cannot fall back to EnsureCreated.");
	}
	var schemaScript = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
	await context.Database.OpenConnectionAsync();
	await using var transaction = await context.Database.BeginTransactionAsync();
	try
	{
		foreach (var batch in SplitSqlBatches(schemaScript))
		{
			await context.Database.ExecuteSqlRawAsync(batch);
		}
		await transaction.CommitAsync();
	}
	catch
	{
		await transaction.RollbackAsync();
		throw;
	}
	return;
}

var runtime = CollectorRuntimeOptions.Load(builder.Configuration);
runtime.RequireProductionAdapters();
if (command == "scheduler" && !builder.Configuration.GetValue("FeatureManagement:EnableScheduledSnapshots", false))
	throw new InvalidOperationException("Scheduled snapshots are disabled. Set FeatureManagement:EnableScheduledSnapshots=true to run the scheduler.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDelegatedOboTokenAcquirer, UnavailableDelegatedTokenAcquirer>();
var workerConnectionString = builder.Configuration.GetConnectionString("Ppgsm")
	?? throw new InvalidOperationException("ConnectionStrings:Ppgsm is required for the worker.");
builder.Services.AddSingleton<IPpgsmDbContextFactory>(new SqlPpgsmDbContextFactory(workerConnectionString));
builder.Services.AddSingleton(AzureCollectorOptions.Blob(builder.Configuration));
builder.Services.AddSingleton<IRawEvidenceContentStore, AzureBlobRawEvidenceContentStore>();
builder.Services.AddSingleton(new ExportArtifactOptions
{
	Endpoint = new Uri(builder.Configuration["Azure:BlobEndpoint"]!),
	ContainerName = builder.Configuration["Azure:ExportsContainerName"] ?? "exports"
});
builder.Services.AddSingleton<IExportArtifactStore, AzureBlobExportArtifactStore>();
builder.Services.AddSingleton(AzureCollectorOptions.Queue(builder.Configuration));
builder.Services.AddSingleton<ISnapshotCollectionJobPublisher, AzureServiceBusSnapshotJobPublisher>();
builder.Services.AddSingleton<SqlDurableStore>();
builder.Services.AddSingleton<ISnapshotEvidenceSink>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ICollectorCheckpointStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ISectionProgressSink>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ISnapshotJobStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ISnapshotStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<IEvaluationEvidenceStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<IEvidenceProjectionStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<IGovernanceStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ICustomerQueueAdapter>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<ICustomerOffboardingStore>(provider => provider.GetRequiredService<SqlDurableStore>());
builder.Services.AddSingleton<IExternalConsentRevocationAdapter, UnavailableExternalConsentRevocationAdapter>();
builder.Services.AddSingleton<CustomerOffboardingService>();
builder.Services.AddSingleton<ISnapshotCollectionJobSource, AzureServiceBusSnapshotCollectionJobSource>();
builder.Services.AddPpgsmCollectors(builder.Configuration);
builder.Services.AddSingleton<RuleEvaluatorRegistry>();
builder.Services.AddSingleton<RuleEvaluationRuntime>();
builder.Services.AddSingleton<IPublishedRuleCatalog>(provider => new FilePublishedRuleCatalog(
	Path.Combine(AppContext.BaseDirectory, "rules", "v1", "catalog.yaml"),
	Path.Combine(AppContext.BaseDirectory, "rules", "v1", "default-profile.yaml"),
	builder.Configuration["RuleCatalog:TrustedVersion"] ?? string.Empty,
	builder.Configuration["RuleCatalog:TrustedPublicationAttestation"] ?? string.Empty,
	provider.GetRequiredService<RuleEvaluatorRegistry>()));
builder.Services.AddSingleton<SnapshotEvaluationService>();
builder.Services.AddSingleton(new JsonEvidencePackageOptions(
	builder.Configuration.GetValue<long?>("Exports:MaximumPackageBytes") ?? 64 * 1024 * 1024));
builder.Services.AddSingleton<JsonEvidencePackageBuilder>();
if (command == "worker")
{
	builder.Services.AddHostedService<SnapshotJobWorker>();
	builder.Services.AddHostedService<OffboardingJobWorker>();
	builder.Services.AddHostedService<ExportJobProcessor>();
}
else if (command == "scheduler") builder.Services.AddHostedService<ScheduledSnapshotPublisher>();
await builder.Build().RunAsync();

static IEnumerable<string> SplitSqlBatches(string script) =>
	System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
		System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
	.Where(value => !string.IsNullOrWhiteSpace(value));

public sealed class ScheduledSnapshotPublisher(
	ISnapshotJobStore jobs,
	ISnapshotCollectionJobPublisher publisher,
	TimeProvider timeProvider,
	ILogger<ScheduledSnapshotPublisher> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
		do
		{
			var queued = await jobs.ListQueuedAsync(stoppingToken);
			foreach (var jobId in queued)
			{
				await publisher.PublishAsync(jobId, stoppingToken);
			}
			logger.LogInformation("Scheduled snapshot publisher submitted {JobCount} durable queued jobs.", queued.Count);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken));
	}
}

public sealed class ExportJobProcessor(
	IGovernanceStore governance,
	ISnapshotStore snapshots,
	IEvaluationEvidenceStore evidence,
	IEvidenceProjectionStore projections,
	IPublishedRuleCatalog rules,
	IExportArtifactStore artifacts,
	JsonEvidencePackageBuilder packageBuilder,
	TimeProvider timeProvider,
	ILogger<ExportJobProcessor> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
		do
		{
			var jobIds = await governance.ListQueuedExportsAsync(stoppingToken);
			foreach (var jobId in jobIds)
			{
				var job = await governance.ClaimExportAsync(jobId, stoppingToken);
				if (job is null) continue;
				if (timeProvider.GetUtcNow() - job.CreatedAt > TimeSpan.FromMinutes(15))
					logger.LogWarning("Export {ExportJobId} queued beyond service objective for tenant {CustomerId}.", job.ExportJobId, job.CustomerId);
				await ProcessAsync(job, stoppingToken);
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken));
	}

	public async ValueTask ProcessAsync(ExportJob job, CancellationToken cancellationToken)
	{
		try
		{
			var snapshot = await snapshots.FindByIdAsync(job.CustomerId, job.SnapshotId, cancellationToken)
				?? throw new DomainConflictException("Export snapshot does not exist for the tenant.");
			if (snapshot.CustomerId != job.CustomerId || snapshot.SnapshotId != job.SnapshotId)
				throw new TenantAccessDeniedException();
			var published = await rules.GetCurrentAsync(cancellationToken)
				?? throw new DomainConflictException("Export requires a trusted published rule catalog.");
			var tenantSettings = await projections.ListTenantSettingsAsync(job.CustomerId, job.SnapshotId, cancellationToken);
			var environments = await projections.ListEnvironmentsAsync(job.CustomerId, job.SnapshotId, cancellationToken);
			var dlpPolicies = await projections.ListDlpPoliciesAsync(job.CustomerId, job.SnapshotId, cancellationToken);
			var metadata = await LoadEvidenceMetadataAsync(job.CustomerId, job.SnapshotId, cancellationToken);
			IReadOnlyDictionary<Guid, byte[]> privilegedBodies = new Dictionary<Guid, byte[]>();
			if (job.IncludesPii)
			{
				var payloads = await evidence.LoadEvaluationEvidenceAsync(job.CustomerId, job.SnapshotId, cancellationToken);
				privilegedBodies = payloads.ToDictionary(value => value.Reference.RawEvidenceReferenceId, value => value.Content);
			}
			var findings = await governance.ListFindingsAsync(job.CustomerId, job.SnapshotId, timeProvider.GetUtcNow(), cancellationToken);
			await using var stream = new MemoryStream();
			var package = await packageBuilder.WriteAsync(new(job, snapshot, published, tenantSettings, environments,
				dlpPolicies, metadata, privilegedBodies, findings, timeProvider.GetUtcNow()), stream, cancellationToken);
			stream.Position = 0;
			var artifact = await artifacts.WriteAsync(job.CustomerId, job.ExportJobId, stream, cancellationToken);
			if (artifact.ContentLength != package.ContentLength)
				throw new InvalidOperationException("Uploaded export artifact length verification failed.");
			var route = $"/api/v1/customers/{job.CustomerId:D}/exports/{job.ExportJobId:D}/download";
			await governance.CompleteExportAsync(job.ExportJobId, route, timeProvider.GetUtcNow().AddHours(1), artifact, cancellationToken);
			logger.LogInformation("Export {ExportJobId} completed for tenant {CustomerId} with package hash {PackageHash} and artifact hash {ArtifactHash}.",
				job.ExportJobId, job.CustomerId, package.ContentHash, artifact.ContentHash);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			var diagnostic = $"Export failed during evidence package generation or immutable upload ({exception.GetType().Name}).";
			await governance.FailExportAsync(job.ExportJobId, diagnostic, cancellationToken);
			logger.LogError(exception, "Export {ExportJobId} failed for tenant {CustomerId}.", job.ExportJobId, job.CustomerId);
		}
	}

	private async ValueTask<IReadOnlyCollection<RawEvidenceReference>> LoadEvidenceMetadataAsync(
		Guid customerId,
		Guid snapshotId,
		CancellationToken cancellationToken)
	{
		const int pageSize = 500;
		var items = new List<RawEvidenceReference>();
		for (var pageNumber = 1; ; pageNumber++)
		{
			var page = await projections.ListEvidenceMetadataAsync(customerId, snapshotId, pageNumber, pageSize, cancellationToken);
			items.AddRange(page.Items);
			if (items.Count >= page.Total) return items;
			if (page.Items.Count == 0) throw new InvalidOperationException("Evidence metadata paging made no progress.");
		}
	}
}