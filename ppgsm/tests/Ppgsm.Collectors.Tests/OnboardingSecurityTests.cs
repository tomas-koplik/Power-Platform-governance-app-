using System.Security.Claims;
using Ppgsm.Api;

namespace Ppgsm.Collectors.Tests;

public sealed class OnboardingSecurityTests
{
    [Fact]
    public async Task Signed_state_is_one_time_and_bound_to_all_context_fields()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var protector = Protector(time);
        var expected = State(time.GetUtcNow().AddMinutes(10));
        var token = protector.Protect(expected);

        var actual = await protector.ValidateAndConsumeAsync(token, CancellationToken.None);

        Assert.Equal(expected, actual);
        await Assert.ThrowsAsync<OnboardingValidationException>(async () =>
            await protector.ValidateAndConsumeAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_expired_state()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var protector = Protector(time);
        var token = protector.Protect(State(time.GetUtcNow().AddSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<OnboardingValidationException>(async () =>
            await protector.ValidateAndConsumeAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Consent_callback_rejects_tenant_substitution()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var protector = Protector(time);
        var validator = new ConsentCallbackValidator(protector);
        var token = protector.Protect(State(time.GetUtcNow().AddMinutes(10)));

        await Assert.ThrowsAsync<OnboardingValidationException>(async () => await validator.ValidateAsync(
            new(token, Guid.NewGuid(), true, null, null), new(stateTenantId, Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Consent_callback_rejects_denial()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var protector = Protector(time);
        var validator = new ConsentCallbackValidator(protector);
        var state = State(time.GetUtcNow().AddMinutes(10));
        var token = protector.Protect(state);

        await Assert.ThrowsAsync<OnboardingValidationException>(async () => await validator.ValidateAsync(
            new(token, state.EntraTenantId, false, "access_denied", "Admin declined"),
            new(state.EntraTenantId, Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Consent_callback_rejects_authenticated_admin_from_wrong_tenant()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var protector = Protector(time);
        var validator = new ConsentCallbackValidator(protector);
        var state = State(time.GetUtcNow().AddMinutes(10));
        var token = protector.Protect(state);

        await Assert.ThrowsAsync<OnboardingValidationException>(async () => await validator.ValidateAsync(
            new(token, state.EntraTenantId, true, null, null), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public void Callback_admin_identity_requires_authenticated_tid_and_oid_claims()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tid", tenantId.ToString("D")), new Claim("oid", objectId.ToString("D"))], "test"));

        Assert.Equal(new AuthenticatedCallbackIdentity(tenantId, objectId), AuthenticatedCallbackIdentity.From(principal));
        Assert.Throws<OnboardingValidationException>(() => AuthenticatedCallbackIdentity.From(new ClaimsPrincipal()));
    }

    private static HmacOnboardingStateProtector Protector(TimeProvider time) => new(
        new OnboardingStateOptions { SigningKey = "fixture-only-signing-key-with-32-bytes" },
        new InMemoryOnboardingStateReplayStore(time),
        time);

    private static OnboardingState State(DateTimeOffset expiresAt) => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("10000000-0000-0000-0000-000000000002"),
        Guid.Parse("10000000-0000-0000-0000-000000000003"),
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        "delegated-admin-consent",
        "one-time-nonce",
        expiresAt);

    private static readonly Guid stateTenantId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}