namespace Ppgsm.Core.Domain;

public enum CustomerStatus { Pending, Active, Suspended, Offboarded }
public enum MembershipRole { Reader, Auditor, CustomerAdmin, Consultant, InternalAdmin }
public enum ConnectionMode { Delegated, AppOnly }
public enum ConnectionStatus { Pending, Active, Degraded, Revoked }

public readonly record struct SubjectIdentity(Guid TenantId, Guid ObjectId)
{
    public static SubjectIdentity Create(Guid tenantId, Guid objectId) =>
        tenantId == Guid.Empty || objectId == Guid.Empty
            ? throw new TenantAccessDeniedException()
            : new SubjectIdentity(tenantId, objectId);

    public override string ToString() => $"{TenantId:D}/{ObjectId:D}";
}

public sealed record Customer(
    Guid CustomerId,
    string Name,
    Guid EntraTenantId,
    string Region,
    CustomerStatus Status,
    DateTimeOffset CreatedAt);

public sealed record TenantMembership(
    Guid TenantMembershipId,
    Guid CustomerId,
    Guid SubjectTenantId,
    Guid SubjectObjectId,
    MembershipRole Role,
    DateTimeOffset CreatedAt)
{
    public SubjectIdentity Subject => SubjectIdentity.Create(SubjectTenantId, SubjectObjectId);
}

public sealed record TenantConnection(
    Guid ConnectionId,
    Guid CustomerId,
    ConnectionMode Mode,
    Guid? AppRegistrationId,
    Guid? ServicePrincipalObjectId,
    string? RbacRoleAssignmentId,
    bool LegacyManagementAppRegistered,
    string? CertificateThumbprint,
    string? ConsentGrantedBy,
    DateTimeOffset? ConsentGrantedAt,
    ConnectionStatus Status,
    DateTimeOffset? LastValidatedAt);

public sealed record TenantCapability(
    Guid TenantCapabilityId,
    Guid CustomerId,
    Guid ConnectionId,
    string Endpoint,
    string Identity,
    bool Available,
    string Detail,
    DateTimeOffset VerifiedAt);

public interface ITenantConnectionStore
{
    ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask<TenantConnection> SaveAsync(TenantConnection connection, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken);
}

public sealed record TenantContext(Guid CustomerId, SubjectIdentity Subject, MembershipRole Role, bool IsInternal = false);

public interface ITenantMembershipStore
{
    ValueTask<TenantMembership?> FindAsync(SubjectIdentity subject, Guid customerId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<TenantMembership>> ListForSubjectAsync(SubjectIdentity subject, CancellationToken cancellationToken);
    ValueTask<TenantMembership> GrantAsync(Guid customerId, SubjectIdentity subject, MembershipRole role, CancellationToken cancellationToken);
}

public sealed class TenantAuthorizer(ITenantMembershipStore memberships, ITenantConnectionStore? connections = null)
{
    public async ValueTask<TenantContext> AuthorizeAsync(
        SubjectIdentity subject,
        Guid customerId,
        MembershipRole minimumRole,
        CancellationToken cancellationToken = default)
    {
        var membership = await memberships.FindAsync(subject, customerId, cancellationToken);
        if (membership is null || !HasPermission(membership.Role, minimumRole)) throw new TenantAccessDeniedException();
        var connection = connections is null ? null : await connections.FindAsync(customerId, cancellationToken);
        if (connection?.Status == ConnectionStatus.Revoked) throw new TenantAccessDeniedException();

        return new TenantContext(customerId, subject, membership.Role, membership.Role == MembershipRole.InternalAdmin);
    }

    private static bool HasPermission(MembershipRole actual, MembershipRole required) => actual switch
    {
        MembershipRole.InternalAdmin => true,
        MembershipRole.Consultant => required is MembershipRole.Reader or MembershipRole.Auditor or MembershipRole.Consultant,
        MembershipRole.CustomerAdmin => required is MembershipRole.Reader or MembershipRole.Auditor or MembershipRole.CustomerAdmin,
        MembershipRole.Auditor => required is MembershipRole.Reader or MembershipRole.Auditor,
        MembershipRole.Reader => required == MembershipRole.Reader,
        _ => false
    };
}

public sealed class TenantAccessDeniedException : UnauthorizedAccessException
{
    public TenantAccessDeniedException() : base("The authenticated identity is not a member of the requested customer.")
    {
    }
}