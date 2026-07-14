using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;
using Ppgsm.Api;
using Ppgsm.Collectors;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;
using Ppgsm.Data;

var builder = WebApplication.CreateBuilder(args);
ProductionConfiguration.Validate(builder.Configuration, builder.Environment);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
if (!builder.Environment.IsDevelopment())
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()!;
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "PPGSM API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Production Entra ID bearer token. Cloud authentication requires explicit configuration."
    });
});

var useDevelopmentAuth = builder.Environment.IsDevelopment() && builder.Configuration.GetValue("Authentication:UseDevelopmentSubject", true);
if (useDevelopmentAuth)
{
    builder.Services.AddAuthentication(DevelopmentSubjectAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentSubjectAuthenticationHandler>(DevelopmentSubjectAuthenticationHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("Authentication"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();
}

builder.Services.AddAuthorization(options =>
{
    var access = builder.Configuration.GetSection("Authentication:ApiAccess").Get<ApiAccessOptions>() ?? new();
    if (access.Audiences.Length == 0 && !string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Audience"]))
        access.Audiences = [builder.Configuration["Authentication:Audience"]!];
    if (builder.Environment.IsDevelopment())
    {
        if (access.Audiences.Length == 0) access.Audiences = ["ppgsm-api"];
        if (access.AuthorizedClientIds.Length == 0) access.AuthorizedClientIds = ["ppgsm-development-client"];
    }
    options.AddPolicy("ApiAccess", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => ApiAccessPolicy.IsAuthorized(context.User, access)));
});
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<LocalDevelopmentStore>();
    builder.Services.AddSingleton<ICustomerStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ITenantMembershipStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ITenantConnectionStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ISnapshotStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IRawEvidenceAuthorizationStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ISnapshotEvidenceSink>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ICollectorCheckpointStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ISectionProgressSink>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IAuditSink>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IGovernanceStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IEvaluationEvidenceStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IEvidenceProjectionStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IPocApprovalStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IExportDownloadAuthorizer>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<IExportArtifactStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ICustomerOffboardingStore>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
    builder.Services.AddSingleton<ICustomerQueueAdapter>(provider => provider.GetRequiredService<LocalDevelopmentStore>());
}
else
{
    CollectorRuntimeOptions.Load(builder.Configuration).RequireProductionAdapters();
    var connectionString = builder.Configuration.GetConnectionString("Ppgsm")
        ?? throw new InvalidOperationException("ConnectionStrings:Ppgsm is required outside Development.");
    builder.Services.AddSingleton<IPpgsmDbContextFactory>(new SqlPpgsmDbContextFactory(connectionString));
    builder.Services.AddSingleton(AzureCollectorOptions.Blob(builder.Configuration));
    builder.Services.AddSingleton<IRawEvidenceContentStore, AzureBlobRawEvidenceContentStore>();
    builder.Services.AddSingleton(new ExportArtifactOptions
    {
        Endpoint = new Uri(builder.Configuration["Azure:BlobEndpoint"]!),
        ContainerName = builder.Configuration["Azure:ExportsContainerName"] ?? "exports"
    });
    builder.Services.AddSingleton<AzureBlobExportArtifactStore>();
    builder.Services.AddSingleton<IExportArtifactStore>(provider => provider.GetRequiredService<AzureBlobExportArtifactStore>());
    builder.Services.AddSingleton<IExportDownloadAuthorizer>(provider => provider.GetRequiredService<AzureBlobExportArtifactStore>());
    builder.Services.AddSingleton(AzureCollectorOptions.Queue(builder.Configuration));
    builder.Services.AddSingleton<ISnapshotCollectionJobPublisher, AzureServiceBusSnapshotJobPublisher>();
    builder.Services.AddSingleton<SqlDurableStore>();
    builder.Services.AddSingleton<ICustomerStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ITenantMembershipStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ITenantConnectionStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ISnapshotStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IRawEvidenceAuthorizationStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ISnapshotEvidenceSink>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ICollectorCheckpointStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ISectionProgressSink>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IAuditSink>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IGovernanceStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IEvaluationEvidenceStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IEvidenceProjectionStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<IPocApprovalStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ISnapshotJobStore>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ICustomerQueueAdapter>(provider => provider.GetRequiredService<SqlDurableStore>());
    builder.Services.AddSingleton<ICustomerOffboardingStore>(provider => provider.GetRequiredService<SqlDurableStore>());
}
builder.Services.AddSingleton<RuleEvaluatorRegistry>();
builder.Services.AddSingleton<RuleEvaluationRuntime>();
builder.Services.AddSingleton<SnapshotEvaluationService>();
builder.Services.AddSingleton<ITrustedRemediationTemplateCatalog, BuiltInTrustedRemediationTemplateCatalog>();
builder.Services.AddSingleton<RemediationEvidencePolicy>();
builder.Services.AddSingleton<TrustedRemediationProposalFactory>();
builder.Services.AddSingleton<RemediationEligibilityService>();
builder.Services.AddSingleton<ConsentDocumentService>();
var externalRevocationOptions = builder.Configuration.GetSection(ExternalConsentRevocationOptions.SectionName)
    .Get<ExternalConsentRevocationOptions>() ?? new();
externalRevocationOptions.Validate();
builder.Services.AddSingleton(externalRevocationOptions);
var liveExternalRevocationEnabled = !builder.Environment.IsDevelopment() && externalRevocationOptions.Enabled;
builder.Services.AddSingleton(new OffboardingCapability(liveExternalRevocationEnabled));
builder.Services.AddSingleton<ApiCapabilityRegistry>();
if (liveExternalRevocationEnabled)
{
    builder.Services.AddHttpClient<IExternalRevocationTransport, MicrosoftExternalRevocationTransport>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PPGSM-Offboarding/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<IExternalConsentRevocationAdapter, ExternalConsentRevocationAdapter>();
}
else
{
    builder.Services.AddSingleton<IExternalConsentRevocationAdapter, UnavailableExternalConsentRevocationAdapter>();
}
builder.Services.AddSingleton<CustomerOffboardingService>();
builder.Services.AddSingleton<IPublishedRuleCatalog>(provider => new FilePublishedRuleCatalog(
    Path.Combine(AppContext.BaseDirectory, "rules", "v1", "catalog.yaml"),
    Path.Combine(AppContext.BaseDirectory, "rules", "v1", "default-profile.yaml"),
    builder.Configuration["RuleCatalog:TrustedVersion"] ?? string.Empty,
    builder.Configuration["RuleCatalog:TrustedManifestDigests"] ?? string.Empty,
    provider.GetRequiredService<RuleEvaluatorRegistry>()));
builder.Services.AddSingleton<TenantAuthorizer>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SnapshotRequestService>();
builder.Services.AddScoped<IAuditService, AuditService>();
if (useDevelopmentAuth)
{
    builder.Services.AddSingleton<IDelegatedOboTokenAcquirer, UnavailableDelegatedTokenAcquirer>();
}
else
{
    builder.Services.AddScoped<IDelegatedOboTokenAcquirer, MicrosoftIdentityWebOboTokenAcquirer>();
}
var onboardingStateOptions = builder.Configuration.GetSection(OnboardingStateOptions.SectionName).Get<OnboardingStateOptions>() ?? new();
builder.Services.AddSingleton(onboardingStateOptions);
if (builder.Environment.IsDevelopment()) builder.Services.AddSingleton<IOnboardingStateReplayStore, InMemoryOnboardingStateReplayStore>();
else builder.Services.AddSingleton<IOnboardingStateReplayStore, SqlOnboardingStateReplayStore>();
builder.Services.AddSingleton<IOnboardingStateProtector, HmacOnboardingStateProtector>();
builder.Services.AddSingleton<IConsentCallbackValidator, ConsentCallbackValidator>();
builder.Services.AddSingleton<TenantConnectionService>();
builder.Services.AddPpgsmCollectors(builder.Configuration);
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<ITenantConsentVerifier, UnavailableTenantConsentVerifier>();
}
else
{
    var verificationOptions = builder.Configuration.GetSection(TenantConsentVerificationOptions.SectionName)
        .Get<TenantConsentVerificationOptions>() ?? new();
    verificationOptions.Validate();
    builder.Services.AddSingleton(verificationOptions);
    builder.Services.AddHttpClient<IConsentGraphTransport, MicrosoftGraphConsentTransport>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PPGSM-ConsentVerifier/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHttpClient<IConsentCapabilityProbeTransport, PowerPlatformConsentCapabilityProbeTransport>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PPGSM-ConsentVerifier/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddScoped<ITenantConsentVerifier, LiveTenantConsentVerifier>();
}

if (!builder.Environment.IsDevelopment()) ProductionServiceGuard.RejectLocalStores(builder.Services);

var app = builder.Build();
if (!app.Environment.IsDevelopment()) ProductionServiceGuard.ValidateResolved(app.Services);
if (!app.Environment.IsDevelopment() && await app.Services.GetRequiredService<IPublishedRuleCatalog>().GetCurrentAsync(CancellationToken.None) is null)
    throw new InvalidOperationException("The deployed rule catalog does not match the trusted version and publication attestation.");
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<AuditMiddleware>();
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is OnboardingValidationException { State: not null } onboarding)
    {
        context.SetAuthorizedTenant(new(onboarding.State.CustomerId,
            SubjectIdentity.Create(onboarding.State.InitiatingTenantId, onboarding.State.InitiatingObjectId), MembershipRole.Consultant));
    }
    var (status, title) = exception switch
    {
        TenantAccessDeniedException => (StatusCodes.Status403Forbidden, "Access denied"),
        DomainConflictException => (StatusCodes.Status409Conflict, "Domain conflict"),
        OnboardingValidationException => (StatusCodes.Status400BadRequest, "Invalid onboarding callback"),
        StaleEvidenceException => (StatusCodes.Status409Conflict, "Stale evidence"),
        CapabilityUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Capability unavailable"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
    };
    await Results.Problem(statusCode: status, title: title, detail: status < 500 ? exception?.Message : null).ExecuteAsync(context);
}));
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous()
    .ExcludeFromDescription();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }))
    .AllowAnonymous()
    .ExcludeFromDescription();

var api = app.MapGroup("/api/v1").RequireAuthorization("ApiAccess");

api.MapGet("/capabilities", (ApiCapabilityRegistry capabilities) => Results.Ok(capabilities.Status));

api.MapGet("/session/workspace", async (
    ClaimsPrincipal principal,
    ICustomerStore customersStore,
    ITenantMembershipStore membershipsStore,
    ITenantConnectionStore connectionStore,
    ISnapshotStore snapshotStore,
    IGovernanceStore governanceStore,
    ApiCapabilityRegistry capabilities,
    IPublishedRuleCatalog rules,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Portfolio);
    var subject = principal.Subject();
    var memberships = await membershipsStore.ListForSubjectAsync(subject, cancellationToken);
    var customers = new List<WorkspaceCustomerResponse>();
    var snapshots = new List<SnapshotResponse>();
    foreach (var membership in memberships)
    {
        var customer = await customersStore.FindCustomerAsync(membership.CustomerId, cancellationToken);
        if (customer is null) continue;
        var connection = await connectionStore.FindAsync(customer.CustomerId, cancellationToken);
        customers.Add(new(customer.CustomerId, customer.Name, customer.EntraTenantId, customer.Region, customer.Status,
            new(connection?.Mode ?? ConnectionMode.Delegated, connection?.Status ?? ConnectionStatus.Pending,
                connection is null ? "Consent has not been verified." : $"Last verified: {connection.LastValidatedAt:O}")));
        snapshots.AddRange((await snapshotStore.ListAsync(customer.CustomerId, cancellationToken)).Select(SnapshotResponse.From));
    }

    var latest = snapshots.OrderByDescending(value => value.RequestedAt).FirstOrDefault();
    var findings = latest is null
        ? Array.Empty<Finding>()
        : (await governanceStore.ListFindingsAsync(latest.CustomerId, latest.SnapshotId, timeProvider.GetUtcNow(), cancellationToken)).ToArray();
    var publishedRules = await rules.GetCurrentAsync(cancellationToken);
    var score = latest is null || publishedRules is null
        ? new GovernanceScore(Guid.Empty, Guid.Empty, 0, "Not evaluated", 0, 0, SectionCoverage.Skipped, new Dictionary<string, int>())
        : GovernanceScoring.Calculate(latest.CustomerId, latest.SnapshotId, findings,
            EvidenceCoverageAggregator.Aggregate(latest.Sections.Select(value => value.Coverage)));
    var role = memberships.OrderByDescending(value => value.Role).Select(value => value.Role).FirstOrDefault();
    return Results.Ok(new WorkspaceResponse(
        new(principal.Identity?.Name ?? subject.ToString(), role, "bff", subject.ToString()), customers, snapshots,
        new(score.Overall, score.Tier, score.Evaluated, score.Total, score.Confidence, score.Areas),
        [], [], findings, [], capabilities.ForRole(role)));
}).Produces<WorkspaceResponse>();

api.MapPost("/customers", async (
    CreateCustomerRequest request,
    ClaimsPrincipal principal,
    ICustomerStore store,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var subject = principal.Subject();
    var customer = await store.CreateCustomerAsync(request.Name, request.EntraTenantId, request.Region, subject, cancellationToken);
    httpContext.SetAuthorizedTenant(new TenantContext(customer.CustomerId, subject, MembershipRole.Consultant));
    return Results.Created($"/api/v1/customers/{customer.CustomerId}", CustomerResponse.From(customer));
}).Produces<CustomerResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

api.MapPost("/customers/{customerId:guid}/snapshots", async (
    Guid customerId,
    StartSnapshotRequest request,
    ClaimsPrincipal principal,
    HttpRequest httpRequest,
    HttpContext httpContext,
    TenantAuthorizer authorizer,
    SnapshotRequestService service,
    ISnapshotStore snapshots,
    ITenantConnectionStore connections,
    ICustomerStore store,
    SnapshotCollectorOrchestrator orchestrator,
    ISnapshotEvidenceSink evidenceSink,
    SnapshotEvaluationService evaluator,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    httpContext.SetAuthorizedTenant(tenant);
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) return Results.Problem(statusCode: 400, title: "Idempotency-Key is required");

    var result = await service.RequestAsync(tenant, new SnapshotRequest(idempotencyKey, request.Mode, request.Sections, request.EnvironmentIds), cancellationToken);
    if (request.Mode == SnapshotMode.AppOnly)
    {
        var jobs = httpContext.RequestServices.GetService<ISnapshotJobStore>()
            ?? throw new InvalidOperationException("A durable snapshot job store is required for app-only collection.");
        var jobPublisher = httpContext.RequestServices.GetService<ISnapshotCollectionJobPublisher>()
            ?? throw new InvalidOperationException("A production snapshot job publisher is required.");
        var customer = await store.FindCustomerAsync(customerId, cancellationToken)
            ?? throw new DomainConflictException("Snapshot customer no longer exists.");
        var connection = await connections.FindAsync(customerId, cancellationToken)
            ?? throw new DomainConflictException("An app-only tenant connection is required.");
        if (connection.Mode != ConnectionMode.AppOnly || connection.Status != ConnectionStatus.Active)
            throw new DomainConflictException("The tenant connection is not active for app-only collection.");
        var job = new SnapshotJobRecord(
            result.Snapshot.SnapshotId, customerId, result.Snapshot.SnapshotId, connection.ConnectionId, customer.EntraTenantId,
            SnapshotMode.AppOnly, tenant.Subject.TenantId, tenant.Subject.ObjectId,
            request.Sections is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Sections),
            request.EnvironmentIds is null ? null : System.Text.Json.JsonSerializer.Serialize(request.EnvironmentIds),
            SnapshotJobStatus.Queued, timeProvider.GetUtcNow(), null, null);
        await jobs.AddAsync(job, cancellationToken);
        await jobPublisher.PublishAsync(job.JobId, cancellationToken);
    }
    if (result.Created && request.Mode == SnapshotMode.Delegated)
    {
        var customer = await store.FindCustomerAsync(customerId, cancellationToken)
            ?? throw new DomainConflictException("Snapshot customer no longer exists.");
        result.Snapshot.Start(timeProvider.GetUtcNow());
        var context = new SnapshotCollectorContext(
            tenant,
            result.Snapshot.SnapshotId,
            customer.EntraTenantId,
            SnapshotMode.Delegated,
            tenant.Subject.ObjectId.ToString("D"),
            principal,
            CollectorConfidence.PocRequired,
            request.Sections,
            request.EnvironmentIds);
        var sections = await orchestrator.ExecuteAsync(context, evidenceSink, request.Sections, cancellationToken);
        foreach (var section in sections) result.Snapshot.RecordSection(section);
        result.Snapshot.Complete(timeProvider.GetUtcNow());
        await snapshots.SaveAsync(result.Snapshot, cancellationToken);
        await evaluator.EvaluateAndPersistAsync(customerId, result.Snapshot.SnapshotId, result.Snapshot.SchemaVersion,
            sections, request.EnvironmentIds, cancellationToken);
    }
    var response = SnapshotResponse.From(result.Snapshot);
    return result.Created
        ? Results.Accepted($"/api/v1/customers/{customerId}/snapshots/{result.Snapshot.SnapshotId}", response)
        : Results.Ok(response);
}).Produces<SnapshotResponse>(StatusCodes.Status202Accepted).Produces<SnapshotResponse>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/consent-url", async (
    Guid customerId,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    TenantAuthorizer authorizer,
    ICustomerStore store,
    IOnboardingStateProtector states,
    IConfiguration configuration,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    httpContext.SetAuthorizedTenant(tenant);
    var customer = await store.FindCustomerAsync(customerId, cancellationToken);
    if (customer is null) return Results.NotFound();
    var clientId = configuration["Onboarding:WebClientId"];
    var redirectUri = configuration["Onboarding:ConsentCallbackUri"];
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
    {
        return Results.Problem(statusCode: 503, title: "Onboarding is not configured");
    }
    var protectedState = states.Protect(new(
        customerId,
        tenant.Subject.TenantId,
        tenant.Subject.ObjectId,
        customer.EntraTenantId,
        "delegated-admin-consent",
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        timeProvider.GetUtcNow().AddMinutes(Math.Clamp(onboardingStateOptions.LifetimeMinutes, 1, 30))));
    var url = $"https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(protectedState)}";
    return Results.Ok(new ConsentUrlResponse(url, customer.EntraTenantId));
}).Produces<ConsentUrlResponse>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPost("/customers/{customerId:guid}/onboarding/consent-url", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, ICustomerStore store, IOnboardingStateProtector states,
    IConfiguration configuration, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Consultant, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var customer = await store.FindCustomerAsync(customerId, cancellationToken);
    if (customer is null) return Results.NotFound();
    var clientId = configuration["Onboarding:WebClientId"];
    var redirectUri = configuration["Onboarding:ConsentCallbackUri"];
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
        return Results.Problem(statusCode: 503, title: "Onboarding is not configured");
    var protectedState = states.Protect(new(customerId, tenant.Subject.TenantId, tenant.Subject.ObjectId,
        customer.EntraTenantId, "delegated-admin-consent", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        timeProvider.GetUtcNow().AddMinutes(Math.Clamp(onboardingStateOptions.LifetimeMinutes, 1, 30))));
    var url = $"https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(protectedState)}";
    return Results.Ok(new ConsentUrlResponse(url, customer.EntraTenantId));
});

api.MapPost("/onboarding/consent-callback", async (
    ConsentCallbackRequest request,
    HttpContext context,
    IConsentCallbackValidator validator,
    TenantConnectionService connections,
    CancellationToken cancellationToken) =>
{
    var validated = await validator.ValidateAsync(new(
        request.State,
        request.Tenant,
        string.Equals(request.AdminConsent, "True", StringComparison.OrdinalIgnoreCase),
        request.Error,
        request.ErrorDescription), AuthenticatedCallbackIdentity.From(context.User), cancellationToken);
    var verification = await connections.VerifyConsentAsync(validated, cancellationToken);
    context.SetAuthorizedTenant(new(validated.State.CustomerId,
        SubjectIdentity.Create(validated.AdminIdentity.TenantId, validated.AdminIdentity.ObjectId), MembershipRole.CustomerAdmin));
    return Results.Ok(new ConsentCallbackResponse(
        validated.State.CustomerId,
        validated.EntraTenantId,
        validated.State.Operation,
        verification.Connection.Status,
        verification.Evidence.EnterpriseApplicationPresent,
        verification.Evidence.DelegatedScopeGranted,
        verification.Evidence.PowerPlatformRoleAssigned,
        verification.Evidence.Detail));
}).Produces<ConsentCallbackResponse>().ProducesProblem(StatusCodes.Status400BadRequest);

api.MapGet("/customers/{customerId:guid}/connection", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ITenantConnectionStore connections, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var connection = await connections.FindAsync(customerId, cancellationToken);
    return connection is null ? Results.NotFound() : Results.Ok(connection);
});

api.MapGet("/customers/{customerId:guid}/capabilities", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ITenantConnectionStore connections, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    return Results.Ok(await connections.ListCapabilitiesAsync(customerId, cancellationToken));
});

api.MapPost("/customers/{customerId:guid}/connection/revoke", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ITenantConnectionStore connections, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var connection = await connections.FindAsync(customerId, cancellationToken);
    if (connection is null) return Results.NotFound();
    return Results.Ok(await connections.SaveAsync(connection with { Status = ConnectionStatus.Revoked, LastValidatedAt = timeProvider.GetUtcNow() }, cancellationToken));
});

api.MapPost("/customers/{customerId:guid}/connection/reconsent", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, ITenantMembershipStore memberships,
    ITenantConnectionStore connections, CancellationToken cancellationToken) =>
{
    var subject = principal.Subject();
    var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
    if (membership?.Role is not (MembershipRole.CustomerAdmin or MembershipRole.InternalAdmin)) throw new TenantAccessDeniedException();
    context.SetAuthorizedTenant(new(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin));
    var connection = await connections.FindAsync(customerId, cancellationToken);
    if (connection is null) return Results.NotFound();
    await connections.ReplaceCapabilitiesAsync(customerId, connection.ConnectionId, [], cancellationToken);
    return Results.Ok(await connections.SaveAsync(connection with { Status = ConnectionStatus.Pending, LastValidatedAt = null }, cancellationToken));
});

api.MapGet("/customers/{customerId:guid}/snapshots", async (
    Guid customerId,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    TenantAuthorizer authorizer,
    ISnapshotStore snapshots,
    CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    httpContext.SetAuthorizedTenant(tenant);
    var result = await snapshots.ListAsync(tenant.CustomerId, cancellationToken);
    return Results.Ok(result.Select(SnapshotResponse.From));
}).Produces<IReadOnlyList<SnapshotResponse>>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}", async (
    Guid customerId,
    Guid snapshotId,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    HttpResponse response,
    TenantAuthorizer authorizer,
    ISnapshotStore snapshots,
    CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    httpContext.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(tenant.CustomerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    response.Headers.ETag = $"\"{snapshot.SnapshotId:N}-{snapshot.Status}\"";
    return Results.Ok(SnapshotResponse.From(snapshot));
}).Produces<SnapshotResponse>().ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/evidence/{evidenceId:guid}", async (
    Guid customerId,
    Guid snapshotId,
    Guid evidenceId,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    TenantAuthorizer authorizer,
    IRawEvidenceAuthorizationStore evidence,
    bool raw,
    CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    httpContext.SetAuthorizedTenant(tenant);
    var item = await evidence.OpenAuthorizedAsync(tenant.CustomerId, snapshotId, evidenceId, cancellationToken);
    if (item is null) return Results.NotFound();
    if (raw && tenant.Role != MembershipRole.InternalAdmin) throw new TenantAccessDeniedException();
    return Results.Ok(await RawEvidenceProjection.CreateAsync(item, raw, cancellationToken));
}).Produces(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/evidence", async (
    Guid customerId, Guid snapshotId, int page, int pageSize, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, ISnapshotStore snapshots, IEvidenceProjectionStore evidence, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    page = Math.Max(page, 1);
    pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 100);
    var result = await evidence.ListEvidenceMetadataAsync(customerId, snapshotId, page, pageSize, cancellationToken);
    var coverage = EvidenceCoverageAggregator.Aggregate(snapshot.Sections.Select(value => value.Coverage));
    var confidence = result.Items.Count == 0 ? EvidenceConfidence.PocRequired : result.Items.Min(value => value.LifecycleConfidence);
    return Results.Ok(new EvidenceIndexResponse(snapshotId, coverage, confidence,
        result.Items.Select(value => value.RawEvidenceReferenceId).ToArray(), result.Items.Select(value => new EvidenceMetadataResponse(
            value.RawEvidenceReferenceId, value.SectionKey, value.MediaType, value.ContentHash, value.CapturedAt,
            value.LifecycleConfidence, value.PageNumber, value.CollectorId, value.CollectorVersion, value.ParserSchemaVersion,
            value.CompletenessRationale)).ToArray(), result.Page, result.PageSize, result.Total));
}).Produces<EvidenceIndexResponse>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/evidence/tenant-settings", async (
    Guid customerId, Guid snapshotId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IEvidenceProjectionStore evidence, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    var items = await evidence.ListTenantSettingsAsync(customerId, snapshotId, cancellationToken);
    var projected = items.SelectMany(value => new TenantSettingProjection[]
    {
        new("trialEnvironmentsDisabled", value.TrialEnvironmentsDisabled, value.RawEvidenceReferenceId),
        new("developerEnvironmentsRestricted", value.DeveloperEnvironmentsRestricted, value.RawEvidenceReferenceId),
        new("copilotDataMovementRestricted", value.CopilotDataMovementRestricted, value.RawEvidenceReferenceId)
    }).ToArray();
    var metadata = await evidence.ListEvidenceMetadataAsync(customerId, snapshotId, 1, 200, cancellationToken);
    return Results.Ok(ProjectEvidence(snapshot, SectionKeys.TenantSettings, projected, items.Select(value => value.RawEvidenceReferenceId), metadata.Items));
});

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/evidence/environments", async (
    Guid customerId, Guid snapshotId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IEvidenceProjectionStore evidence, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    var items = await evidence.ListEnvironmentsAsync(customerId, snapshotId, cancellationToken);
    var projected = items.Select(value => new EnvironmentProjection(value.EnvironmentId, value.DisplayName, value.EnvironmentType,
        value.Region, value.IsDefault, value.IsManaged, value.ProtectionLevel, value.HasDataverse, value.SecurityGroupId,
        value.RawEvidenceReferenceId)).ToArray();
    var metadata = await evidence.ListEvidenceMetadataAsync(customerId, snapshotId, 1, 200, cancellationToken);
    return Results.Ok(ProjectEvidence(snapshot, SectionKeys.Environments, projected, items.Select(value => value.RawEvidenceReferenceId), metadata.Items));
});

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/evidence/dlp-policies", async (
    Guid customerId, Guid snapshotId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IEvidenceProjectionStore evidence, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    var items = await evidence.ListDlpPoliciesAsync(customerId, snapshotId, cancellationToken);
    var projected = items.Select(value => new DlpPolicyProjection(value.PolicyId, value.DisplayName,
        value.Properties.RootElement.Clone(), value.RawEvidenceReferenceId)).ToArray();
    var metadata = await evidence.ListEvidenceMetadataAsync(customerId, snapshotId, 1, 200, cancellationToken);
    return Results.Ok(ProjectEvidence(snapshot, SectionKeys.DlpPolicies, projected, items.Select(value => value.RawEvidenceReferenceId), metadata.Items));
});

api.MapGet("/customers/{customerId:guid}/consent-metadata", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ConsentDocumentService documents, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var document = await documents.GetAsync(customerId, cancellationToken);
    return document is null ? Results.NotFound() : Results.Ok(document);
}).Produces<ConsentDocumentResponse>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPost("/customers/{customerId:guid}/offboarding", async (
    Guid customerId, RequestOffboardingRequest request, ClaimsPrincipal principal, HttpContext context, ITenantMembershipStore memberships,
    CustomerOffboardingService offboarding, OffboardingCapability capability,
    CancellationToken cancellationToken) =>
{
    capability.Require();
    var subject = principal.Subject();
    var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
    if (membership?.Role is not (MembershipRole.CustomerAdmin or MembershipRole.InternalAdmin)) throw new TenantAccessDeniedException();
    context.SetAuthorizedTenant(new(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin));
    var deletion = await offboarding.RequestAsync(customerId, subject.ToString(), request.RetentionExpiresAt, cancellationToken);
    return Results.Accepted($"/api/v1/customers/{customerId}/deletion", deletion);
});

api.MapPost("/customers/{customerId:guid}/offboarding/approve", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, ITenantMembershipStore memberships,
    CustomerOffboardingService offboarding, OffboardingCapability capability, CancellationToken cancellationToken) =>
{
    capability.Require();
    var subject = principal.Subject();
    var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
    if (membership?.Role is not (MembershipRole.CustomerAdmin or MembershipRole.InternalAdmin)) throw new TenantAccessDeniedException();
    context.SetAuthorizedTenant(new(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin));
    return Results.Accepted($"/api/v1/customers/{customerId}/deletion",
        await offboarding.ApproveAsync(customerId, subject.ToString(), cancellationToken));
});

api.MapGet("/customers/{customerId:guid}/deletion", async (
    Guid customerId, ClaimsPrincipal principal, HttpContext context, ITenantMembershipStore memberships,
    ICustomerOffboardingStore offboarding, CancellationToken cancellationToken) =>
{
    var subject = principal.Subject();
    var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
    if (membership?.Role is not (MembershipRole.CustomerAdmin or MembershipRole.InternalAdmin)) throw new TenantAccessDeniedException();
    context.SetAuthorizedTenant(new(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin));
    var deletion = await offboarding.GetDeletionAsync(customerId, cancellationToken);
    return deletion is null ? Results.NotFound() : Results.Ok(deletion);
});

api.MapGet("/customers/{customerId:guid}/deletion/certificate", async (
    Guid customerId, bool download, ClaimsPrincipal principal, HttpContext context, ITenantMembershipStore memberships,
    ICustomerOffboardingStore offboarding, CancellationToken cancellationToken) =>
{
    var subject = principal.Subject();
    var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
    if (membership?.Role is not (MembershipRole.CustomerAdmin or MembershipRole.InternalAdmin)) throw new TenantAccessDeniedException();
    context.SetAuthorizedTenant(new(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin));
    var deletion = await offboarding.GetDeletionAsync(customerId, cancellationToken);
    if (deletion?.Status != DeletionStatus.Completed || deletion.CompletedAt is null || string.IsNullOrWhiteSpace(deletion.CertificateId)) return Results.NotFound();
    var certificate = new DeletionCertificateResponse(deletion.CertificateId, customerId, deletion.JobId, deletion.RequestedAt,
        deletion.ApprovedAt, deletion.StartedAt, deletion.CompletedAt.Value, deletion.RetentionExpiresAt, "Completed",
        ParseCounts(deletion.BeforeCountsJson), ParseCounts(deletion.AfterCountsJson), deletion.ConsentRevocationReference!, deletion.PhysicalDeletionReference!);
    if (!download) return Results.Ok(certificate);
    context.Response.Headers.ContentDisposition = $"attachment; filename=ppgsm-deletion-{deletion.CertificateId}.json";
    return Results.Json(certificate);
}).ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/findings", async (
    Guid customerId, Guid snapshotId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Findings);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    if (await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken) is null) return Results.NotFound();
    return Results.Ok(await governance.ListFindingsAsync(customerId, snapshotId, timeProvider.GetUtcNow(), cancellationToken));
}).Produces<IReadOnlyList<Finding>>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/score", async (
    Guid customerId, Guid snapshotId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Score);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var snapshot = await snapshots.FindByIdAsync(customerId, snapshotId, cancellationToken);
    if (snapshot is null) return Results.NotFound();
    var findings = await governance.ListFindingsAsync(customerId, snapshotId, timeProvider.GetUtcNow(), cancellationToken);
    return Results.Ok(GovernanceScoring.Calculate(customerId, snapshotId, findings,
        EvidenceCoverageAggregator.Aggregate(snapshot.Sections.Select(value => value.Coverage))));
}).Produces<GovernanceScore>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapPost("/customers/{customerId:guid}/comparisons", async (
    Guid customerId, SnapshotComparisonRequest request, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, ISnapshotStore snapshots, IGovernanceStore governance, ApiCapabilityRegistry capabilities,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Compare);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var baseline = await snapshots.FindByIdAsync(customerId, request.BaselineSnapshotId, cancellationToken);
    var current = await snapshots.FindByIdAsync(customerId, request.CurrentSnapshotId, cancellationToken);
    if (baseline is null || current is null) return Results.NotFound();
    SnapshotComparisonGuard.EnsureSameTenant(baseline, current);
    var now = timeProvider.GetUtcNow();
    var before = (await governance.ListFindingsAsync(customerId, baseline.SnapshotId, now, cancellationToken)).ToDictionary(value => value.RuleId);
    var after = (await governance.ListFindingsAsync(customerId, current.SnapshotId, now, cancellationToken)).ToDictionary(value => value.RuleId);
    return Results.Ok(new SnapshotComparison(customerId, baseline.SnapshotId, current.SnapshotId,
        after.Keys.Except(before.Keys).Count(), before.Keys.Except(after.Keys).Count(),
        before.Keys.Intersect(after.Keys).Count(key => before[key].Status != after[key].Status)));
}).Produces<SnapshotComparison>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapPost("/customers/{customerId:guid}/exports", async (
    Guid customerId, CreateExportRequest request, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    ISnapshotStore snapshots, IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Exports);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Auditor, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    if (request.Format != ExportFormat.Json) throw new CapabilityUnavailableException(ApiCapability.Exports);
    if (await snapshots.FindByIdAsync(customerId, request.SnapshotId, cancellationToken) is null) return Results.NotFound();
    if (request.IncludePii && tenant.Role != MembershipRole.InternalAdmin) throw new TenantAccessDeniedException();
    var exportJobId = Guid.NewGuid();
    var job = new ExportJob(exportJobId, customerId, request.Format, ExportJobStatus.Queued, timeProvider.GetUtcNow(),
        tenant.Subject.ToString(), SnapshotId: request.SnapshotId, IncludesPii: request.IncludePii);
    await governance.AddExportAsync(job, cancellationToken);
    return Results.Accepted($"/api/v1/customers/{customerId}/exports/{job.ExportJobId}", job);
}).Produces<ExportJob>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/exports/{exportJobId:guid}", async (
    Guid customerId, Guid exportJobId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    IGovernanceStore governance, ApiCapabilityRegistry capabilities, CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Exports);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var job = await governance.FindExportAsync(customerId, exportJobId, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
}).Produces<ExportJob>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/exports/{exportJobId:guid}/download", async (
    Guid customerId, Guid exportJobId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    IGovernanceStore governance, IExportArtifactStore artifacts, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Exports);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var job = await governance.FindExportAsync(customerId, exportJobId, cancellationToken);
    if (job is null) return Results.NotFound();
    if (job.Status != ExportJobStatus.Completed || job.DownloadExpiresAt <= timeProvider.GetUtcNow()) return Results.Conflict();
    if (job.IncludesPii && tenant.Role != MembershipRole.InternalAdmin) throw new TenantAccessDeniedException();
    var stream = await artifacts.OpenReadAsync(customerId, exportJobId, cancellationToken);
    if (stream is null) return Results.NotFound();
    if (job.ArtifactContentLength is { } contentLength) context.Response.ContentLength = contentLength;
    if (job.ArtifactContentHash is { } contentHash) context.Response.Headers["X-Content-SHA256"] = contentHash;
    if (job.ArtifactStorageETag is { } storageETag) context.Response.Headers.ETag = storageETag;
    return Results.Stream(stream, job.ArtifactMediaType ?? "application/json", $"ppgsm-evidence-{job.SnapshotId:N}.json");
}).Produces(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapPost("/customers/{customerId:guid}/exports/{exportJobId:guid}/download-url", async (
    Guid customerId, Guid exportJobId, ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Exports);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var job = await governance.FindExportAsync(customerId, exportJobId, cancellationToken);
    if (job is null) return Results.NotFound();
    if (job.Status != ExportJobStatus.Completed || job.DownloadExpiresAt <= timeProvider.GetUtcNow()) return Results.Conflict();
    if (job.IncludesPii && tenant.Role != MembershipRole.InternalAdmin) throw new TenantAccessDeniedException();
    var exports = context.RequestServices.GetService<IExportDownloadAuthorizer>();
    if (exports is null) return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Cloud download URL adapter is unavailable");
    var download = await exports.CreateAuthorizedDownloadAsync(job, TimeSpan.FromMinutes(5), cancellationToken);
    return download is null ? Results.Conflict() : Results.Ok(new { Url = download });
}).ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapPost("/customers/{customerId:guid}/findings/{findingId:guid}/exceptions", async (
    Guid customerId, Guid findingId, CreateExceptionRequest request, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Exceptions);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var item = new GovernanceException(Guid.NewGuid(), customerId, findingId, request.Reason, tenant.Subject.ToString(), timeProvider.GetUtcNow(), request.ExpiresAt);
    return Results.Created($"/api/v1/customers/{customerId}/findings/{findingId}/exceptions/{item.ExceptionId}", await governance.AddExceptionAsync(item, cancellationToken));
}).Produces<GovernanceException>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status403Forbidden);

api.MapPost("/customers/{customerId:guid}/remediation/proposals", async (
    Guid customerId, CreateRemediationProposalRequest request, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, IGovernanceStore governance, TrustedRemediationProposalFactory proposalFactory, ApiCapabilityRegistry capabilities,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Remediation);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var proposal = await proposalFactory.CreateAsync(customerId, request, tenant.Subject.ToString(), cancellationToken);
    return Results.Created($"/api/v1/customers/{customerId}/remediation/proposals/{proposal.ProposalId}", await governance.AddProposalAsync(proposal, cancellationToken));
}).Produces<RemediationProposal>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/remediation/proposals", async (
    Guid customerId, RemediationProposalStatus? status, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, IGovernanceStore governance, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    return Results.Ok(await governance.ListProposalsAsync(customerId, status, cancellationToken));
}).Produces<IReadOnlyList<RemediationProposal>>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapGet("/customers/{customerId:guid}/remediation/proposals/{proposalId:guid}", async (
    Guid customerId, Guid proposalId, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, IGovernanceStore governance, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.Reader, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var proposal = await governance.FindProposalAsync(customerId, proposalId, cancellationToken);
    return proposal is null ? Results.NotFound() : Results.Ok(proposal);
}).Produces<RemediationProposal>().ProducesProblem(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

api.MapGet("/customers/{customerId:guid}/snapshots/{snapshotId:guid}/findings/{findingId:guid}/remediation-eligibility", async (
    Guid customerId, Guid snapshotId, Guid findingId, string evidenceHash,
    ClaimsPrincipal principal, HttpContext context, TenantAuthorizer authorizer,
    RemediationEligibilityService eligibility, CancellationToken cancellationToken) =>
{
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    return Results.Ok(await eligibility.GetAsync(customerId, snapshotId, findingId, evidenceHash, cancellationToken));
}).Produces<RemediationEligibilityResponse>().ProducesProblem(StatusCodes.Status403Forbidden);

api.MapPost("/customers/{customerId:guid}/remediation/proposals/{proposalId:guid}/review", async (
    Guid customerId, Guid proposalId, ReviewRemediationRequest request, ClaimsPrincipal principal, HttpContext context,
    TenantAuthorizer authorizer, IGovernanceStore governance, ApiCapabilityRegistry capabilities, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    capabilities.Require(ApiCapability.Approvals);
    var tenant = await authorizer.AuthorizeAsync(principal.Subject(), customerId, MembershipRole.CustomerAdmin, cancellationToken);
    context.SetAuthorizedTenant(tenant);
    var proposal = await governance.FindProposalAsync(customerId, proposalId, cancellationToken);
    if (proposal is null) return Results.NotFound();
    if (request.Approved) proposal.Approve(tenant.Subject.ToString(), timeProvider.GetUtcNow(), request.LatestSnapshotId);
    else proposal.Reject(tenant.Subject.ToString(), request.Reason ?? string.Empty, timeProvider.GetUtcNow());
    await governance.SaveProposalAsync(proposal, cancellationToken);
    return Results.Ok(proposal);
}).Produces<RemediationProposal>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict);

app.Run();

static ProjectedEvidenceResponse<T> ProjectEvidence<T>(Snapshot snapshot, string sectionKey, IReadOnlyCollection<T> items,
    IEnumerable<Guid> evidenceIds, IReadOnlyCollection<RawEvidenceReference> metadata)
{
    var section = snapshot.Sections.SingleOrDefault(value => SectionKeys.Canonicalize(value.SectionKey) == sectionKey);
    var coverage = section?.Coverage ?? SectionCoverage.Skipped;
    var state = section is null || coverage is SectionCoverage.Skipped or SectionCoverage.Failed ? "Unavailable"
        : coverage == SectionCoverage.Full ? "Complete" : "Partial";
    if (items.Count == 0 && state == "Complete") state = "Partial";
    var ids = evidenceIds.Distinct().ToArray();
    var confidence = metadata.Where(value => ids.Contains(value.RawEvidenceReferenceId)).Select(value => value.LifecycleConfidence).DefaultIfEmpty(EvidenceConfidence.PocRequired).Min();
    return new(snapshot.SnapshotId, state, coverage, confidence, ids, items, state == "Unavailable" ? "Collector did not produce persisted normalized evidence."
            : state == "Partial" ? "Persisted evidence is incomplete; absence is not interpreted as a complete empty result."
            : "Persisted normalized evidence is complete for the collected section.");
}

static IReadOnlyDictionary<string, long> ParseCounts(string? json) => string.IsNullOrWhiteSpace(json)
    ? new Dictionary<string, long>()
    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new Dictionary<string, long>();

public partial class Program;

public sealed record CreateCustomerRequest(string Name, Guid EntraTenantId, string Region = "westeurope");
public sealed record StartSnapshotRequest(SnapshotMode Mode, IReadOnlyCollection<string>? Sections, IReadOnlyCollection<string>? EnvironmentIds);
public sealed record SnapshotComparisonRequest(Guid BaselineSnapshotId, Guid CurrentSnapshotId);
public sealed record ConsentUrlResponse(string Url, Guid EntraTenantId);
public sealed record ConsentCallbackRequest(string State, Guid? Tenant, string? AdminConsent, string? Error, string? ErrorDescription);
public sealed record ConsentCallbackResponse(Guid CustomerId, Guid EntraTenantId, string Operation, ConnectionStatus Status,
    bool EnterpriseApplicationPresent, bool DelegatedScopeGranted, bool PowerPlatformRoleAssigned, string Detail);
public sealed record CustomerResponse(Guid CustomerId, string Name, Guid EntraTenantId, string Region, CustomerStatus Status, DateTimeOffset CreatedAt)
{
    public static CustomerResponse From(Customer value) => new(value.CustomerId, value.Name, value.EntraTenantId, value.Region, value.Status, value.CreatedAt);
}
public sealed record SnapshotResponse(Guid SnapshotId, Guid CustomerId, SnapshotStatus Status, SnapshotMode Mode, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, IReadOnlyCollection<SnapshotSection> Sections)
{
    public static SnapshotResponse From(Snapshot value) => new(value.SnapshotId, value.CustomerId, value.Status, value.Mode, value.RequestedAt, value.StartedAt, value.CompletedAt, value.Sections);
}