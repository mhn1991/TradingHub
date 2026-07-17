using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class FileCalibrationArtifactRepositoryTests
{
    [Test]
    public async Task StoreSetupAsync_ThenGetSetupAsync_RoundTrips()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        SetupCalibrationArtifact artifact = BuildSetupArtifact();

        CalibrationArtifactMetadata metadata = await repository.StoreSetupAsync(artifact, "test artifact");
        SetupCalibrationArtifact? loaded = await repository.GetSetupAsync(metadata.Id);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Type, Is.EqualTo(CalibrationArtifactType.Setup));
            Assert.That(metadata.CalibrationId, Is.EqualTo(artifact.CalibrationId));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.CalibrationId, Is.EqualTo(artifact.CalibrationId));
            Assert.That(loaded.Buckets, Has.Count.EqualTo(1));
            Assert.That(loaded.Buckets[0].WinRate, Is.EqualTo(artifact.Buckets[0].WinRate));
        });
    }

    [Test]
    public async Task StoreManagementAsync_ThenGetManagementAsync_RoundTrips()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        TradeManagementCalibration artifact = BuildManagementArtifact();

        CalibrationArtifactMetadata metadata = await repository.StoreManagementAsync(artifact);
        TradeManagementCalibration? loaded = await repository.GetManagementAsync(metadata.Id);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Type, Is.EqualTo(CalibrationArtifactType.Management));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Cohorts, Has.Count.EqualTo(1));
            Assert.That(loaded.Cohorts[0].CohortId, Is.EqualTo(artifact.Cohorts[0].CohortId));
        });
    }

    [Test]
    public async Task GetSetupAsync_WrongType_ReturnsNull()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        CalibrationArtifactMetadata metadata = await repository.StoreManagementAsync(BuildManagementArtifact());

        SetupCalibrationArtifact? loaded = await repository.GetSetupAsync(metadata.Id);

        Assert.That(loaded, Is.Null);
    }

    [Test]
    public async Task GetSetupAsync_UnknownId_ReturnsNull()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());

        Assert.That(await repository.GetSetupAsync(Guid.NewGuid()), Is.Null);
    }

    [Test]
    public async Task ListAsync_FiltersByType_AndOrdersNewestFirst()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        await repository.StoreSetupAsync(BuildSetupArtifact());
        await Task.Delay(10);
        CalibrationArtifactMetadata secondSetup = await repository.StoreSetupAsync(
            BuildSetupArtifact() with { CalibrationId = "second" });
        await repository.StoreManagementAsync(BuildManagementArtifact());

        IReadOnlyList<CalibrationArtifactMetadata> setups = await repository.ListAsync(CalibrationArtifactType.Setup);

        Assert.Multiple(() =>
        {
            Assert.That(setups, Has.Count.EqualTo(2));
            Assert.That(setups.All(item => item.Type == CalibrationArtifactType.Setup), Is.True);
            Assert.That(setups[0].Id, Is.EqualTo(secondSetup.Id));
        });
    }

    [Test]
    public async Task DeleteAsync_RemovesArtifact_AndReturnsTrueOnce()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        CalibrationArtifactMetadata metadata = await repository.StoreSetupAsync(BuildSetupArtifact());

        bool firstDelete = await repository.DeleteAsync(metadata.Id);
        bool secondDelete = await repository.DeleteAsync(metadata.Id);

        Assert.Multiple(() =>
        {
            Assert.That(firstDelete, Is.True);
            Assert.That(secondDelete, Is.False);
        });
        Assert.That(await repository.GetSetupAsync(metadata.Id), Is.Null);
    }

    [Test]
    public async Task GetSetupAsync_CorruptFile_QuarantinesAndReturnsNull()
    {
        string root = TempDirectory();
        var repository = new FileCalibrationArtifactRepository(root);
        CalibrationArtifactMetadata metadata = await repository.StoreSetupAsync(BuildSetupArtifact());
        string path = Path.Combine(root, $"{metadata.Id:N}.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        SetupCalibrationArtifact? loaded = await repository.GetSetupAsync(metadata.Id);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Null);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(File.Exists(Path.Combine(root, "quarantine", $"{metadata.Id:N}.json")), Is.True);
        });
    }

    [Test]
    public async Task GetSetupAsync_TamperedContentHash_ThrowsDataIntegrityError()
    {
        string root = TempDirectory();
        var repository = new FileCalibrationArtifactRepository(root);
        CalibrationArtifactMetadata metadata = await repository.StoreSetupAsync(BuildSetupArtifact());
        string path = Path.Combine(root, $"{metadata.Id:N}.json");
        string content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace("\"totalSamples\": 120", "\"totalSamples\": 999"));

        Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetSetupAsync(metadata.Id));
    }

    private static SetupCalibrationArtifact BuildSetupArtifact() => new()
    {
        SchemaVersion = 1,
        CalibrationId = "cal-1",
        TrainingFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        TrainingTo = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        Instruments = ["FX:GBP/USD"],
        StrategyVersion = "improved-v1",
        FeatureSchemaHash = "tradinghub-meta-v1",
        Parameters = new Dictionary<string, string> { ["bucketWidth"] = "5" },
        TotalSamples = 120,
        CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        DataHash = "abc123",
        Buckets =
        [
            new SetupCalibrationBucket
            {
                StrategyId = "improved",
                InstrumentGroup = "FX",
                Regime = "TrendingUp",
                ConfidenceFrom = 60m,
                ConfidenceTo = 65m,
                Samples = 40,
                WinRate = 0.55m,
                AverageR = 0.6m,
                ExpectedR = 0.3m,
                BrierScore = 0.2m
            }
        ]
    };

    private static TradeManagementCalibration BuildManagementArtifact() => new()
    {
        CalibrationId = "mgmt-1",
        SourceDataHash = "def456",
        CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        Cohorts =
        [
            new TradeManagementCohort
            {
                CohortId = "cohort-1",
                StrategyId = "improved",
                InstrumentGroup = "FX",
                Regime = "TrendingUp",
                SetupType = "Breakout",
                Direction = "Buy",
                Session = "London",
                VolatilityBucket = "Normal",
                ConfidenceBucket = 65,
                Samples = 30,
                WinnerMfe80PercentileByBar = new Dictionary<int, decimal> { [5] = 1.2m },
                MedianMaeBeforeHalfR = 0.2m,
                MedianDurationBars = 12m,
                MedianExitEfficiency = 0.7m,
                MedianStopDistance = 0.005m
            }
        ]
    };

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "tradinghub-sim-tests", "calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
