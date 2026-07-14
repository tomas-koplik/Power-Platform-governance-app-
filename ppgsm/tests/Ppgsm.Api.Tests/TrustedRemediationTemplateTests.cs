using System.Text.Json;
using Ppgsm.Api;

namespace Ppgsm.Api.Tests;

public sealed class TrustedRemediationTemplateTests
{
    [Fact]
    public void Trusted_template_renders_fixed_command_from_typed_allowlisted_parameters()
    {
        var template = new BuiltInTrustedRemediationTemplateCatalog().Find("tenant-settings.restrict-production-creation.v1")!;
        var tenantId = Guid.NewGuid();
        using var document = JsonDocument.Parse("""{"disabled":true}""");
        var parameters = document.RootElement.EnumerateObject().ToDictionary(value => value.Name, value => value.Value);

        var script = template.Render(parameters, tenantId.ToString("D"));

        Assert.Equal($"Set-TenantSettings -TenantId '{tenantId:D}' -DisableEnvironmentCreationByNonAdminUsers $true", script);
    }

    [Fact]
    public void Trusted_template_has_exact_parameter_allowlist_and_rejects_script_fragments_as_scope()
    {
        var template = new BuiltInTrustedRemediationTemplateCatalog().Find("tenant-settings.restrict-production-creation.v1")!;
        Assert.Equal(["disabled"], template.AllowedParameters);
        using var document = JsonDocument.Parse("""{"disabled":true}""");
        var parameters = document.RootElement.EnumerateObject().ToDictionary(value => value.Name, value => value.Value);

        Assert.Throws<ArgumentException>(() => template.Render(parameters, "tenant'; Remove-Item -Recurse *; '"));
    }
}