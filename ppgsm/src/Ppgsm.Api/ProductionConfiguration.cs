namespace Ppgsm.Api;

public static class ProductionConfiguration
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        Require(configuration, "Authentication:ClientId");
        RequireHttpsUri(configuration, "Authentication:Authority");
        Require(configuration, "Authentication:Audience");
        Require(configuration, "Authentication:ApiAccess:Scope");
        RequireValues(configuration, "Authentication:ApiAccess:AuthorizedClientIds");
        Require(configuration, "Onboarding:WebClientId");
        RequireHttpsUri(configuration, "Onboarding:ConsentCallbackUri");
        RequireSecret(configuration, "Onboarding:State:SigningKey", 32);
        Require(configuration, "Onboarding:Verification:ClientApplicationId");
        Require(configuration, "Onboarding:Verification:DelegatedResourceApplicationId");
        RequireValues(configuration, "Onboarding:Verification:ExpectedDelegatedScopes");
        Require(configuration, "RuleCatalog:TrustedVersion");
        Require(configuration, "RuleCatalog:TrustedPublicationAttestation");
        RequireValues(configuration, "Cors:AllowedOrigins", requireHttps: true);
        Require(configuration, "Collectors:AppOnlyCertificate:ClientId");
        RequireHttpsUri(configuration, "Collectors:AppOnlyCertificate:KeyVaultCertificateUri");
    }

    private static void Require(IConfiguration configuration, string key)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
            throw new InvalidOperationException($"Production configuration '{key}' is required.");
    }

    private static void RequireSecret(IConfiguration configuration, string key, int minimumLength)
    {
        Require(configuration, key);
        if (configuration[key]!.Length < minimumLength)
            throw new InvalidOperationException($"Production configuration '{key}' must contain at least {minimumLength} characters.");
    }

    private static void RequireHttpsUri(IConfiguration configuration, string key)
    {
        Require(configuration, key);
        if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Production configuration '{key}' must be an absolute HTTPS URI.");
    }

    private static void RequireValues(IConfiguration configuration, string key, bool requireHttps = false)
    {
        var values = configuration.GetSection(key).Get<string[]>() ?? [];
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Production configuration '{key}' must contain at least one non-empty value.");
        if (requireHttps && values.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Production configuration '{key}' must contain only absolute HTTPS origins.");
    }
}