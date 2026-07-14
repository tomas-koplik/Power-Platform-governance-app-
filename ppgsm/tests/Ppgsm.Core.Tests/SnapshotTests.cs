using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Core.Tests;

public sealed class SnapshotTests
{
    private static readonly Guid CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly SubjectIdentity Subject = SubjectIdentity.Create(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_sections_complete_snapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.Start(Now.AddSeconds(1));
        snapshot.RecordSection(new SnapshotSection(Guid.NewGuid(), CustomerId, snapshot.SnapshotId, "environments", SectionCoverage.Full, 3, null, Now.AddSeconds(2)));
        snapshot.Complete(Now.AddSeconds(3));

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(Now.AddSeconds(3), snapshot.CompletedAt);
    }

    [Fact]
    public void Mixed_coverage_produces_partial_snapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.Start(Now.AddSeconds(1));
        snapshot.RecordSection(new SnapshotSection(Guid.NewGuid(), CustomerId, snapshot.SnapshotId, "environments", SectionCoverage.Full, 3, null, Now.AddSeconds(2)));
        snapshot.RecordSection(new SnapshotSection(Guid.NewGuid(), CustomerId, snapshot.SnapshotId, "flows", SectionCoverage.Partial, 2, "Service principal scope is incomplete.", Now.AddSeconds(2)));
        snapshot.Complete(Now.AddSeconds(3));

        Assert.Equal(SnapshotStatus.Partial, snapshot.Status);
    }

    [Fact]
    public void Completed_snapshot_rejects_further_mutation()
    {
        var snapshot = CreateSnapshot();
        snapshot.Start(Now.AddSeconds(1));
        snapshot.RecordSection(new SnapshotSection(Guid.NewGuid(), CustomerId, snapshot.SnapshotId, "settings", SectionCoverage.Full, 1, null, Now.AddSeconds(2)));
        snapshot.Complete(Now.AddSeconds(3));

        Assert.Throws<DomainConflictException>(() => snapshot.RecordSection(
            new SnapshotSection(Guid.NewGuid(), CustomerId, snapshot.SnapshotId, "environments", SectionCoverage.Full, 1, null, Now.AddSeconds(4))));
    }

    [Fact]
    public async Task Same_customer_and_idempotency_key_returns_original_snapshot()
    {
        var store = new TestSnapshotStore();
        var service = new SnapshotRequestService(store, new FixedTimeProvider(Now));
        var tenant = new TenantContext(CustomerId, Subject, MembershipRole.CustomerAdmin);

        var first = await service.RequestAsync(tenant, new SnapshotRequest("request-1", SnapshotMode.Delegated));
        var second = await service.RequestAsync(tenant, new SnapshotRequest("request-1", SnapshotMode.Delegated));

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Same(first.Snapshot, second.Snapshot);
    }

    [Fact]
    public async Task Membership_from_another_customer_is_denied()
    {
        var allowedCustomer = CustomerId;
        var deniedCustomer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new TestMembershipStore(new TenantMembership(Guid.NewGuid(), allowedCustomer, Subject.TenantId, Subject.ObjectId, MembershipRole.CustomerAdmin, Now));
        var authorizer = new TenantAuthorizer(store);

        await Assert.ThrowsAsync<TenantAccessDeniedException>(async () =>
            await authorizer.AuthorizeAsync(Subject, deniedCustomer, MembershipRole.Reader));
    }

    [Fact]
    public async Task Consultant_cannot_satisfy_customer_admin_role()
    {
        var store = new TestMembershipStore(new TenantMembership(Guid.NewGuid(), CustomerId, Subject.TenantId, Subject.ObjectId, MembershipRole.Consultant, Now));
        var authorizer = new TenantAuthorizer(store);

        await Assert.ThrowsAsync<TenantAccessDeniedException>(async () =>
            await authorizer.AuthorizeAsync(Subject, CustomerId, MembershipRole.CustomerAdmin));
    }

    [Fact]
    public async Task Revoked_connection_denies_existing_membership()
    {
        var memberships = new TestMembershipStore(new TenantMembership(Guid.NewGuid(), CustomerId, Subject.TenantId, Subject.ObjectId, MembershipRole.CustomerAdmin, Now));
        var connections = new TestConnectionStore(new(Guid.NewGuid(), CustomerId, ConnectionMode.Delegated, null, null, null, false, null, null, null, ConnectionStatus.Revoked, Now));
        var authorizer = new TenantAuthorizer(memberships, connections);

        await Assert.ThrowsAsync<TenantAccessDeniedException>(async () =>
            await authorizer.AuthorizeAsync(Subject, CustomerId, MembershipRole.Reader));
    }

    private static Snapshot CreateSnapshot() => new(Guid.NewGuid(), CustomerId, "request-1", "subject-a", SnapshotMode.Delegated, 1, Now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestMembershipStore(TenantMembership membership) : ITenantMembershipStore
    {
        public ValueTask<TenantMembership?> FindAsync(SubjectIdentity subject, Guid customerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(membership.Subject == subject && membership.CustomerId == customerId ? membership : null);

        public ValueTask<IReadOnlyList<TenantMembership>> ListForSubjectAsync(SubjectIdentity subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<TenantMembership>>(membership.Subject == subject ? [membership] : []);
        public ValueTask<TenantMembership> GrantAsync(Guid customerId, SubjectIdentity subject, MembershipRole role, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TenantMembership(Guid.NewGuid(), customerId, subject.TenantId, subject.ObjectId, role, Now));
    }

    private sealed class TestConnectionStore(TenantConnection connection) : ITenantConnectionStore
    {
        public ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection.CustomerId == customerId ? connection : null);
        public ValueTask<TenantConnection> SaveAsync(TenantConnection value, CancellationToken cancellationToken) => ValueTask.FromResult(value);
        public ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<TenantCapability>>([]);
        public ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TestSnapshotStore : ISnapshotStore
    {
        private readonly List<Snapshot> _items = [];
        public ValueTask<Snapshot?> FindByIdAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken) => ValueTask.FromResult(_items.SingleOrDefault(value => value.CustomerId == customerId && value.SnapshotId == snapshotId));
        public ValueTask<Snapshot?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken) => ValueTask.FromResult(_items.SingleOrDefault(value => value.CustomerId == customerId && value.IdempotencyKey == idempotencyKey));
        public ValueTask<IReadOnlyList<Snapshot>> ListAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<Snapshot>>(_items.Where(value => value.CustomerId == customerId).ToArray());
        public ValueTask AddAsync(Snapshot snapshot, CancellationToken cancellationToken) { _items.Add(snapshot); return ValueTask.CompletedTask; }
        public ValueTask SaveAsync(Snapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}