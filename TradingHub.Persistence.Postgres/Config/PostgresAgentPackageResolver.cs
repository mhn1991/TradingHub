using System.Collections.Concurrent;
using Agent.Factories;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using DBManager.Postgres.Config;
using Microsoft.EntityFrameworkCore;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradingPolicies;

namespace TradingHub.Persistence.Postgres.Config;

/// <summary>Resolves only an explicitly named immutable revision and its explicitly linked artifacts.</summary>
public sealed class PostgresAgentPackageResolver : IAgentPackageResolver
{
    private const int CacheLimit = 128;
    private readonly IPolicyConfigurationStore _policies;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly ITradingAgentCatalog _catalog;
    private readonly ConcurrentDictionary<string, Lazy<Task<ResolvedAgentPackage>>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public PostgresAgentPackageResolver(
        IDbContextFactory<TradingHubDbContext> contextFactory,
        ICalibrationArtifactRepository artifacts,
        ITradingAgentCatalog catalog)
    {
        _policies = new PolicyConfigurationStore(contextFactory);
        _artifacts = artifacts;
        _catalog = catalog;
    }

    public async Task<ResolvedAgentPackage> ResolveAsync(
        Guid policyRevisionId, CancellationToken cancellationToken = default)
    {
        if (policyRevisionId == Guid.Empty)
            throw new ArgumentException("An exact policy revision ID is required.", nameof(policyRevisionId));

        PolicyRevisionDetail revision = await _policies.GetPolicyRevisionAsync(policyRevisionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Policy revision {policyRevisionId:N} was not found.");
        string cacheKey = $"{policyRevisionId:N}:{revision.ConfigurationHash}:" + string.Join('|', revision.Artifacts
            .OrderBy(x => x.ArtifactId).Select(x => $"{x.ArtifactId:N}:{x.ContentHash}"));
        Lazy<Task<ResolvedAgentPackage>> lazy = _cache.GetOrAdd(cacheKey, key =>
        {
            _cacheOrder.Enqueue(key);
            TrimCache();
            return new Lazy<Task<ResolvedAgentPackage>>(
                () => ResolveCoreAsync(revision, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
        });
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private async Task<ResolvedAgentPackage> ResolveCoreAsync(
        PolicyRevisionDetail revision, CancellationToken cancellationToken)
    {
        TradingPolicyProfile profile = TradingPolicyProfileJson.Deserialize(revision.PolicyDocumentJson);
        if (profile.ProfileId != revision.PolicyId || profile.Revision != revision.Revision ||
            !string.Equals(profile.ConfigurationHash, revision.ConfigurationHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The immutable policy document identity or hash does not match its revision row.");
        }

        var expected = revision.Artifacts.ToDictionary(x => x.ArtifactId);
        VerifyLink(profile.SetupCalibrationArtifactId, ArtifactRole.SetupCalibration, expected);
        VerifyLink(profile.MetaModelArtifactId, ArtifactRole.MetaModel, expected);
        VerifyLink(profile.ManagementCalibrationArtifactId, ArtifactRole.ManagementCalibration, expected);
        Guid[] documentIds =
        [.. new[] { profile.SetupCalibrationArtifactId, profile.MetaModelArtifactId,
            profile.ManagementCalibrationArtifactId }.Where(x => x.HasValue).Select(x => x!.Value)];
        if (expected.Keys.Except(documentIds).Any())
            throw new InvalidOperationException("The revision contains an artifact link not declared by the policy document.");

        foreach (PolicyArtifactLink link in revision.Artifacts)
        {
            CalibrationArtifactMetadata metadata = await _artifacts.GetMetadataAsync(link.ArtifactId, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException($"Artifact {link.ArtifactId:N} was not found.");
            if (!string.Equals(metadata.ContentHash, link.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Artifact {link.ArtifactId:N} failed its content-hash check.");
        }

        SetupCalibrationArtifact? setup = profile.SetupCalibrationArtifactId is { } setupId
            ? await _artifacts.GetSetupAsync(setupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Setup artifact {setupId:N} was not found.")
            : null;
        TradeManager.TradeManagementCalibration? management = profile.ManagementCalibrationArtifactId is { } managementId
            ? await _artifacts.GetManagementAsync(managementId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Management artifact {managementId:N} was not found.")
            : null;
        ISetupMetaModel? model = null;
        if (profile.MetaModelArtifactId is { } modelId)
        {
            MetaModelArtifact artifact = await _artifacts.GetMetaModelAsync(modelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Meta-model artifact {modelId:N} was not found.");
            model = new CalibratedSetupMetaModel(artifact, profile.MetaModelPolicy);
        }

        return ResolvedAgentPackage.Create(
            revision.PolicyRevisionId,
            profile,
            _catalog,
            setup,
            model,
            management,
            expected.ToDictionary(x => x.Key, x => x.Value.ContentHash));
    }

    private static void VerifyLink(
        Guid? artifactId, ArtifactRole role, IReadOnlyDictionary<Guid, PolicyArtifactLink> links)
    {
        if (artifactId is null) return;
        if (!links.TryGetValue(artifactId.Value, out PolicyArtifactLink? link) || link.Role != role)
            throw new InvalidOperationException($"Policy artifact {artifactId:N} is missing its exact {role} link.");
    }

    private void TrimCache()
    {
        while (_cache.Count >= CacheLimit && _cacheOrder.TryDequeue(out string? oldest))
            _cache.TryRemove(oldest, out _);
    }
}
