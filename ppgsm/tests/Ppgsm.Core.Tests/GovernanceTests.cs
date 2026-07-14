using Ppgsm.Core.Domain;

namespace Ppgsm.Core.Tests;

public sealed class GovernanceTests
{
    [Fact]
    public void Score_excludes_non_scoring_outcomes_and_applies_critical_cap()
    {
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var findings = new[]
        {
            Finding(customerId, snapshotId, FindingStatus.Pass, FindingSeverity.Low),
            Finding(customerId, snapshotId, FindingStatus.Pass, FindingSeverity.Low),
            Finding(customerId, snapshotId, FindingStatus.Fail, FindingSeverity.Critical),
            Finding(customerId, snapshotId, FindingStatus.NotEvaluated, FindingSeverity.High),
            Finding(customerId, snapshotId, FindingStatus.NotApplicable, FindingSeverity.High),
            Finding(customerId, snapshotId, FindingStatus.Fail, FindingSeverity.Informational),
            Finding(customerId, snapshotId, FindingStatus.Excepted, FindingSeverity.High)
        };

        var score = GovernanceScoring.Calculate(customerId, snapshotId, findings, SectionCoverage.Partial);

        Assert.Equal(17, score.Overall);
        Assert.Equal(3, score.Evaluated);
        Assert.Equal(7, score.Total);
        Assert.Equal("At Risk", score.Tier);
    }

    [Fact]
    public void Score_uses_area_applicability_and_evaluator_partial_ratio()
    {
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var findings = new[]
        {
            Finding(customerId, snapshotId, FindingStatus.Pass, FindingSeverity.High) with { AreaWeight = 2m },
            Finding(customerId, snapshotId, FindingStatus.Partial, FindingSeverity.Low) with { EvaluatorRatio = 0.25m },
            Finding(customerId, snapshotId, FindingStatus.Fail, FindingSeverity.Informational)
        };

        var score = GovernanceScoring.Calculate(customerId, snapshotId, findings, SectionCoverage.Full);

        Assert.Equal(93, score.Overall);
        Assert.Equal(2, score.Evaluated);
        Assert.Equal("Excellent", score.Tier);
    }

    [Fact]
    public void Failed_critical_finding_caps_otherwise_high_score_at_59()
    {
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var findings = new[]
        {
            Finding(customerId, snapshotId, FindingStatus.Fail, FindingSeverity.Critical) with { ApplicabilityWeight = 0.1m },
            Finding(customerId, snapshotId, FindingStatus.Pass, FindingSeverity.High) with { AreaWeight = 10m }
        };

        var score = GovernanceScoring.Calculate(customerId, snapshotId, findings, SectionCoverage.Full);

        Assert.Equal(59, score.Overall);
        Assert.Equal("At Risk", score.Tier);
    }

    [Fact]
    public void Exception_is_inactive_at_expiry()
    {
        var expiry = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var item = new GovernanceException(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Accepted risk", "approver", expiry.AddDays(-1), expiry);

        Assert.True(item.IsActive(expiry.AddTicks(-1)));
        Assert.False(item.IsActive(expiry));
    }

    [Fact]
    public void Comparison_rejects_snapshots_from_different_tenants()
    {
        var baseline = Snapshot(Guid.NewGuid());
        var current = Snapshot(Guid.NewGuid());

        Assert.Throws<TenantAccessDeniedException>(() => SnapshotComparisonGuard.EnsureSameTenant(baseline, current));
    }

    [Fact]
    public void Proposer_cannot_approve_own_proposal()
    {
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var snapshotId = Guid.NewGuid();
        var proposal = Proposal(snapshotId, "alice", now);

        Assert.Throws<DomainConflictException>(() => proposal.Approve("ALICE", now.AddMinutes(1), snapshotId));
    }

    [Fact]
    public void Approval_rejects_expired_or_superseded_evidence()
    {
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var snapshotId = Guid.NewGuid();

        Assert.Throws<StaleEvidenceException>(() => Proposal(snapshotId, "alice", now).Approve("bob", now.AddHours(2), snapshotId));
        Assert.Throws<StaleEvidenceException>(() => Proposal(snapshotId, "alice", now).Approve("bob", now.AddMinutes(1), Guid.NewGuid()));
    }

    [Fact]
    public void Independent_approver_can_approve_fresh_script_without_execution_state()
    {
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var snapshotId = Guid.NewGuid();
        var proposal = Proposal(snapshotId, "alice", now);

        proposal.Approve("bob", now.AddMinutes(1), snapshotId);

        Assert.Equal(RemediationProposalStatus.Approved, proposal.Status);
        Assert.Equal("bob", proposal.ReviewedBy);
        Assert.DoesNotContain(proposal.GetType().GetMethods(), method => method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Active_legal_hold_blocks_queue_rows_and_evidence_deletion()
    {
        var customerId = Guid.NewGuid();
        var adapters = new OffboardingAdapters(new(customerId, "Investigation", DateTimeOffset.UtcNow, null));
        var service = new CustomerOffboardingService(adapters, adapters, adapters, TimeProvider.System);

        await Assert.ThrowsAsync<LegalHoldException>(async () => await service.RequestAsync(customerId, "alice", DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Empty(adapters.Calls);
    }

    [Fact]
    public async Task Offboarding_requires_independent_approval()
    {
        var customerId = Guid.NewGuid();
        var adapters = new OffboardingAdapters(null);
        var service = new CustomerOffboardingService(adapters, adapters, adapters, TimeProvider.System);
        await service.RequestAsync(customerId, "alice", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(async () => await service.ApproveAsync(customerId, "ALICE", CancellationToken.None));
    }

    [Fact]
    public async Task Offboarding_records_counts_adapter_evidence_and_retained_certificate()
    {
        var customerId = Guid.NewGuid();
        var adapters = new OffboardingAdapters(null);
        var service = new CustomerOffboardingService(adapters, adapters, adapters, TimeProvider.System);
        var deletion = await service.RequestAsync(customerId, "alice", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        await service.ApproveAsync(customerId, "bob", CancellationToken.None);

        await service.ProcessAsync(deletion.JobId, CancellationToken.None);

        Assert.Equal(["counts", "consent", "queue", "physical"], adapters.Calls);
        Assert.Equal(DeletionStatus.Completed, deletion.Status);
        Assert.NotNull(deletion.BeforeCountsJson);
        Assert.NotNull(deletion.AfterCountsJson);
        Assert.NotNull(deletion.CertificateId);
        Assert.Equal("consent-proof", deletion.ConsentRevocationReference);
    }

    [Fact]
    public async Task Unverified_external_revocation_fails_without_physical_deletion()
    {
        var customerId = Guid.NewGuid();
        var adapters = new OffboardingAdapters(null) { ConsentSucceeds = false };
        var service = new CustomerOffboardingService(adapters, adapters, adapters, TimeProvider.System);
        var deletion = await service.RequestAsync(customerId, "alice", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        await service.ApproveAsync(customerId, "bob", CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(async () => await service.ProcessAsync(deletion.JobId, CancellationToken.None));

        Assert.Equal(DeletionStatus.Failed, deletion.Status);
        Assert.Equal(["counts", "consent"], adapters.Calls);
        Assert.Null(deletion.CertificateId);
    }

    private static Finding Finding(Guid customerId, Guid snapshotId, FindingStatus status, FindingSeverity severity) =>
        new(Guid.NewGuid(), customerId, snapshotId, Guid.NewGuid().ToString("N"), "Rule", "Area", severity, status, "Tenant", "Observed", "Interpreted", "Action", RemediationKind.Manual);

    private static Snapshot Snapshot(Guid customerId) =>
        new(Guid.NewGuid(), customerId, Guid.NewGuid().ToString("N"), "tester", SnapshotMode.Delegated, 1, DateTimeOffset.UtcNow);

    private static RemediationProposal Proposal(Guid snapshotId, string proposer, DateTimeOffset now) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshotId, "Write-Output 'review only'", proposer, now, now.AddMinutes(-1), now.AddHours(1), RemediationKind.Script);

    private sealed class OffboardingAdapters(CustomerLegalHold? legalHold) : ICustomerOffboardingStore, ICustomerQueueAdapter, IExternalConsentRevocationAdapter
    {
        private CustomerDeletionRecord? _deletion;
        public List<string> Calls { get; } = [];
        public bool ConsentSucceeds { get; init; } = true;
        public ValueTask<CustomerLegalHold?> GetLegalHoldAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult(legalHold);
        public ValueTask<CustomerDeletionRecord?> GetDeletionAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult(_deletion?.CustomerId == customerId ? _deletion : null);
        public ValueTask<CustomerDeletionRecord?> GetDeletionJobAsync(Guid jobId, CancellationToken cancellationToken) => ValueTask.FromResult(_deletion?.JobId == jobId ? _deletion : null);
        public ValueTask SaveDeletionAsync(CustomerDeletionRecord deletion, CancellationToken cancellationToken) { _deletion = deletion; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyCollection<Guid>> ListExecutableDeletionJobsAsync(DateTimeOffset now, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyCollection<Guid>>([]);
        public ValueTask<IReadOnlyDictionary<string, long>> CountTenantDataAsync(Guid customerId, CancellationToken cancellationToken)
        {
            Calls.Add("counts");
            return ValueTask.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["Customers"] = 1 });
        }
        public ValueTask<PhysicalDeletionResult> DeleteTenantDataAsync(Guid customerId, CancellationToken cancellationToken) { Calls.Add("physical"); return ValueTask.FromResult(new PhysicalDeletionResult(true, "deletion-proof", new Dictionary<string, long> { ["Customers"] = 0 }, "deleted")); }
        public ValueTask CancelCustomerJobsAsync(Guid customerId, CancellationToken cancellationToken) { Calls.Add("queue"); return ValueTask.CompletedTask; }
        public ValueTask<ExternalConsentRevocationResult> RevokeAsync(Guid customerId, CancellationToken cancellationToken) { Calls.Add("consent"); return ValueTask.FromResult(new ExternalConsentRevocationResult(ConsentSucceeds ? ExternalConsentRevocationStatus.Succeeded : ExternalConsentRevocationStatus.Failed, ConsentSucceeds ? "consent-proof" : null, [], ConsentSucceeds ? "revoked" : "unavailable")); }
    }
}