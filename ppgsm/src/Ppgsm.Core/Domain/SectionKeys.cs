namespace Ppgsm.Core.Domain;

public static class SectionKeys
{
    public const string TenantSettings = "tenantSettings";
    public const string Environments = "environments";
    public const string DlpPolicies = "dlpPolicies";
    public const string Connectors = "connectors";
    public const string TenantIsolation = "tenantIsolation";
    public const string Apps = "apps";
    public const string Flows = "flows";
    public const string OwnerDirectory = "ownerDirectory";
    public const string EnvironmentGroups = "environmentGroups";

    public static IReadOnlySet<string> Canonical { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TenantSettings, Environments, DlpPolicies, Connectors, TenantIsolation,
        Apps, Flows, OwnerDirectory, EnvironmentGroups
    };

    private static readonly IReadOnlyDictionary<string, string> ReadAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant-settings"] = TenantSettings,
            ["tenant_settings"] = TenantSettings,
            ["environment"] = Environments,
            ["dlp"] = DlpPolicies,
            ["dlp-policies"] = DlpPolicies,
            ["tenant-isolation"] = TenantIsolation,
            ["owner-directory"] = OwnerDirectory,
            ["environment-groups"] = EnvironmentGroups
        };

    public static string Canonicalize(string sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey)) throw new ArgumentException("A section key is required.", nameof(sectionKey));
        var trimmed = sectionKey.Trim();
        var canonical = Canonical.FirstOrDefault(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (canonical is not null) return canonical;
        return ReadAliases.TryGetValue(trimmed, out var alias) ? alias : trimmed;
    }
}