using Ppgsm.Core.Domain;

namespace Ppgsm.Api;

public sealed class FilePublishedRuleCatalog(
    string catalogPath,
    string profilePath,
    string trustedVersion,
    string trustedManifestDigest,
    RuleEvaluatorRegistry? evaluatorRegistry = null) : IPublishedRuleCatalog
{
    private readonly Ppgsm.Core.Domain.FilePublishedRuleCatalog inner =
        new(catalogPath, profilePath, trustedVersion, trustedManifestDigest, evaluatorRegistry);

    public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) => inner.GetCurrentAsync(cancellationToken);

    public ValueTask<PublishedRuleSet?> GetByDigestAsync(string contentDigest, CancellationToken cancellationToken) =>
        inner.GetByDigestAsync(contentDigest, cancellationToken);
}