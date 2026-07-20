using Agent.Configuration;
using Agent.Models;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using Simulator.Calibration;
using Simulator.Experiments;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralExperimentTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    [TestCase("pullback", false, false, "Supply/demand analysis")]
    [TestCase("sweep", false, false, "Liquidity analysis")]
    [TestCase("break-retest", false, false, "Liquidity analysis")]
    [TestCase("sweep-confluence", true, false, "Supply/demand analysis")]
    public void StructuralProfile_RejectsMissingPlaybookAnalysis(
        string playbook,
        bool liquidityEnabled,
        bool supplyDemandEnabled,
        string expectedMessage)
    {
        var exception = Assert.Throws<ArgumentException>(() => SimulationStrategyProfile.Create(
            ProfileDraft(Guid.NewGuid(), playbook, liquidityEnabled, supplyDemandEnabled)));

        Assert.That(exception!.Message, Does.Contain(expectedMessage));
    }

    [TestCase("pullback", false, true)]
    [TestCase("sweep", true, false)]
    [TestCase("break-retest", true, false)]
    [TestCase("sweep-confluence", true, true)]
    public void StructuralProfile_AllowsCompatiblePlaybookAnalysis(
        string playbook,
        bool liquidityEnabled,
        bool supplyDemandEnabled)
    {
        Assert.That(
            () => SimulationStrategyProfile.Create(
                ProfileDraft(Guid.NewGuid(), playbook, liquidityEnabled, supplyDemandEnabled)),
            Throws.Nothing);
    }

    [Test]
    public void Profile_RejectsManagementThesisIntervalNotMatchingAgentTrend()
    {
        SimulationStrategyProfile draft = ProfileDraft(Guid.NewGuid(), "pullback", false, true) with
        {
            Management = new PositionManagementOptions { ThesisInterval = BarInterval.Minutes(90) }
        };

        var exception = Assert.Throws<ArgumentException>(() => SimulationStrategyProfile.Create(draft));

        Assert.That(exception!.Message, Does.Contain("ThesisInterval"));
    }

    [Test]
    public void Profile_RejectsManagementFastStructureIntervalNotMatchingAgentTrigger()
    {
        SimulationStrategyProfile draft = ProfileDraft(Guid.NewGuid(), "pullback", false, true) with
        {
            Management = new PositionManagementOptions { FastStructureInterval = BarInterval.Minutes(1) }
        };

        var exception = Assert.Throws<ArgumentException>(() => SimulationStrategyProfile.Create(draft));

        Assert.That(exception!.Message, Does.Contain("FastStructureInterval"));
    }

    [Test]
    public void Profile_RejectsRegimeProfileManagementIntervalNotMatchingAgent()
    {
        SimulationStrategyProfile draft = ProfileDraft(Guid.NewGuid(), "pullback", false, true) with
        {
            Runtime = new BacktestRuntimeProfile
            {
                Options = new BacktestRuntimeOptions
                {
                    RegimeManagement = new RegimeManagementOptions
                    {
                        Enabled = true,
                        Profiles =
                        [
                            new PositionManagementProfile
                            {
                                ProfileId = "trending",
                                ApplicableRegimes = [MarketRegime.TrendingUp],
                                Options = new PositionManagementOptions
                                {
                                    ThesisInterval = BarInterval.Minutes(90)
                                }
                            }
                        ]
                    }
                }
            }
        };

        var exception = Assert.Throws<ArgumentException>(() => SimulationStrategyProfile.Create(draft));

        Assert.That(exception!.Message, Does.Contain("RegimeManagement.Profiles['trending']"));
    }

    [Test]
    public void Profile_AllowsManagementIntervalsThatMatchTheAgentsOwnTimeframes()
    {
        // StructuralConfluenceStrategyOptions() defaults: ContextInterval=1h (coarsest -> thesis),
        // TriggerInterval=5m (finest -> fast structure).
        SimulationStrategyProfile draft = ProfileDraft(Guid.NewGuid(), "pullback", false, true) with
        {
            Management = new PositionManagementOptions
            {
                FastStructureInterval = BarInterval.Minutes(5),
                ThesisInterval = BarInterval.Hours(1)
            }
        };

        Assert.That(() => SimulationStrategyProfile.Create(draft), Throws.Nothing);
    }

    [Test]
    public void Timeline_RejectsOverlapAndInsufficientExternalEmbargo()
    {
        SimulationExperimentTimeline overlapping = Timeline() with
        {
            LearningTo = Start.AddDays(31),
            EvaluationFrom = Start.AddDays(30)
        };
        SimulationExperimentTimeline shortEmbargo = Timeline() with
        {
            LearningTo = Start.AddDays(25),
            EvaluationFrom = Start.AddDays(30),
            EmbargoDays = 10
        };

        Assert.Multiple(() =>
        {
            Assert.That(() => overlapping.Validate(), Throws.ArgumentException);
            Assert.That(() => shortEmbargo.Validate(), Throws.ArgumentException);
        });
    }

    [Test]
    public void RelativeTimeline_ResolvesStableUtcHalfOpenBoundaries()
    {
        var request = new RelativeExperimentTimelineRequest
        {
            EvaluationFrom = Start.AddMonths(4),
            EvaluationTo = Start.AddMonths(5),
            LearningMonths = 2,
            EmbargoDays = 10
        };

        SimulationExperimentTimeline first = request.Resolve();
        SimulationExperimentTimeline second = request.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.LearningTo, Is.EqualTo(request.EvaluationFrom.AddDays(-10)));
            Assert.That(first.LearningFrom, Is.EqualTo(first.LearningTo.AddMonths(-2)));
            Assert.That(first.EvaluationTo, Is.EqualTo(request.EvaluationTo));
        });
    }

    [Test]
    public void WarmupPlanner_IsDeterministicAndRejectsUnavailableHistory()
    {
        SimulationStrategyProfile profile = Profile(Guid.NewGuid());
        AnalysisWarmupPlan first = AnalysisWarmupPlanner.Plan(profile, Start.AddMonths(4), 7);
        AnalysisWarmupPlan second = AnalysisWarmupPlanner.Plan(profile, Start.AddMonths(4), 7);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.ResolvedWarmupDays, Is.GreaterThanOrEqualTo(7));
            Assert.That(first.PromotionEligible, Is.True);
            Assert.That(
                () => AnalysisWarmupPlanner.Plan(
                    profile,
                    Start.AddMonths(4),
                    7,
                    availableDataFrom: first.StreamFrom.AddMinutes(1)),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void ExperimentRanker_AppliesHardGatesAndPinsDeterministicPromotionCandidate()
    {
        SimulationExperimentProfileRun[] runs = Enumerable.Range(0, 3).Select(_ => new SimulationExperimentProfileRun
        {
            ProfileRunId = Guid.NewGuid(),
            Profile = SimulationStrategyProfileSnapshot.From(Profile(Guid.NewGuid()))
        }).ToArray();
        Guid artifactId = Guid.NewGuid();
        AnalysisWarmupPlan warmup = Warmup();
        var snapshot = new SimulationExperimentSnapshot
        {
            Id = Guid.NewGuid(),
            Revision = 8,
            Name = "rank candidates",
            State = SimulationExperimentState.Running,
            Stage = SimulationExperimentStage.Aggregating,
            CreatedAt = Start,
            Manifest = new SimulationExperimentManifest
            {
                Timeline = Timeline(),
                ProfileRuns = runs,
                TrainingWarmups = runs.ToDictionary(item => item.ProfileRunId, _ => warmup),
                EvaluationWarmups = runs.ToDictionary(item => item.ProfileRunId, _ => warmup),
                Parallelism = new SimulationExperimentParallelism(),
                Ranking = new SimulationExperimentRankingPolicy { ShortlistSize = 1 },
                DatasetStatus = "Ready",
                DatasetHash = "dataset-sha256"
            },
            Profiles =
            [
                Progress(runs[0], "stable-a", 20m, 2m, 10m, 1.5m, 60,
                    [new SimulationArtifactReference { ArtifactId = artifactId, Kind = "setup", ContentHash = "artifact-sha256" }]),
                Progress(runs[1], "stable-b", 10m, 1m, 5m, 1.4m, 50),
                Progress(runs[2], "too-small", 100m, 10m, 1m, 4m, 5)
            ]
        };

        SimulationExperimentComparisonSummary first = SimulationExperimentRanker.Build(snapshot);
        SimulationExperimentComparisonSummary second = SimulationExperimentRanker.Build(snapshot);
        SimulationExperimentComparisonRow rejected = first.Profiles.Single(item => item.ProfileName == "too-small");
        SimulationPromotionCandidate candidate = first.PromotionCandidates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(first.Profiles.Single(item => item.ProfileName == "stable-a").Rank, Is.EqualTo(1));
            Assert.That(first.Profiles.Single(item => item.ProfileName == "stable-a").ScoreBreakdown.Count, Is.EqualTo(4));
            Assert.That(rejected.IsEligible, Is.False);
            Assert.That(rejected.HardGateFailures, Does.Contain("MinimumHeldOutTradeCount"));
            Assert.That(candidate.CandidateId, Is.EqualTo(second.PromotionCandidates.Single().CandidateId));
            Assert.That(candidate.ProfileId, Is.EqualTo(runs[0].Profile.ProfileId));
            Assert.That(candidate.ProfileContentHash, Is.EqualTo(runs[0].Profile.ContentHash));
            Assert.That(candidate.DatasetHash, Is.EqualTo("dataset-sha256"));
            Assert.That(candidate.Artifacts.Single().ContentHash, Is.EqualTo("artifact-sha256"));
            Assert.That(candidate.AutomaticExecutablePermission, Is.False);
        });
    }

    [Test]
    public async Task ProfileStore_PreservesImmutableRevisionsAndReportsResolvedDiff()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tradinghub-profile-tests-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSimulationStrategyProfileStore(root);
            SimulationStrategyProfile created = await store.CreateAsync(Profile(Guid.NewGuid()));
            SimulationStrategyProfile revised = await store.CreateRevisionAsync(
                created.ProfileId,
                created with { Name = "CCI required", ContentHash = string.Empty });

            SimulationStrategyProfile original = (await store.ReadAsync(created.ProfileId, 1))!;
            SimulationProfileDiff diff = await store.DiffAsync(created.ProfileId, 1, created.ProfileId, 2);

            Assert.Multiple(() =>
            {
                Assert.That(revised.Revision, Is.EqualTo(2));
                Assert.That(original.Name, Is.EqualTo("Structural baseline"));
                Assert.That(original.ContentHash, Is.EqualTo(created.ContentHash));
                Assert.That(diff.ChangedPaths, Does.Contain("$.name"));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ProfileHash_DoesNotDependOnRegimePolicyInsertionOrder()
    {
        SimulationStrategyProfile original = Profile(Guid.NewGuid());
        var routing = original.Runtime.Options.MarketRegimeRouting;
        var reversedPolicies = routing.Policies.Reverse()
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        SimulationStrategyProfile reordered = SimulationStrategyProfile.Create(original with
        {
            Runtime = original.Runtime with
            {
                Options = original.Runtime.Options with
                {
                    MarketRegimeRouting = routing with { Policies = reversedPolicies }
                }
            },
            ContentHash = string.Empty
        });

        Assert.That(reordered.ContentHash, Is.EqualTo(original.ContentHash));
    }

    [Test]
    public void ProfileHash_DiffersBetweenV1AndV2AdaptiveTargetManagement()
    {
        // Structural Indicator and Adaptive Target Management Plan §5.4/§7.3: enabling v2 must
        // produce a different deterministic profile hash than v1, and v1's own hash must be
        // completely unaffected by the new options existing at all (default-disabled, byte-for-
        // byte the same StructuralConfluenceStrategyOptions as before this plan).
        SimulationStrategyProfile v1 = Profile(Guid.NewGuid());
        SimulationStrategyProfile v2 = SimulationStrategyProfile.Create(v1 with
        {
            ProfileId = Guid.NewGuid(),
            Agent = v1.Agent with
            {
                StructuralConfluence = v1.Agent.StructuralConfluence! with
                {
                    StrategyVersion = "structural-confluence-v2",
                    AdaptiveTargetManagement = new AdaptiveTargetManagementOptions { Enabled = true }
                }
            },
            ContentHash = string.Empty
        });

        Assert.That(v2.ContentHash, Is.Not.EqualTo(v1.ContentHash));
    }

    [Test]
    public void ProfileHash_IgnoresDisabledV1AdaptiveTargetSettings()
    {
        SimulationStrategyProfile original = Profile(Guid.NewGuid());
        SimulationStrategyProfile customizedDisabled = SimulationStrategyProfile.Create(original with
        {
            Agent = original.Agent with
            {
                StructuralConfluence = original.Agent.StructuralConfluence! with
                {
                    AdaptiveTargetManagement = new AdaptiveTargetManagementOptions
                    {
                        ManagedOpportunityMinimumR = 3m,
                        MaximumPersistedCandidates = 5
                    }
                }
            },
            ContentHash = string.Empty
        });

        Assert.That(customizedDisabled.ContentHash, Is.EqualTo(original.ContentHash));
    }

    [Test]
    public void AdaptiveTargetManagement_CannotBeEnabledUnderTheV1StrategyVersion()
    {
        var options = new StructuralConfluenceStrategyOptions
        {
            StrategyVersion = "structural-confluence-v1",
            AdaptiveTargetManagement = new AdaptiveTargetManagementOptions { Enabled = true }
        };

        Assert.That(() => options.Validate(), Throws.ArgumentException);
    }

    [Test]
    public void CanonicalJsonHash_DoesNotDependOnObjectOrderOrWhitespace()
    {
        const string beforeJsonb = "{\"name\":\"exp\\u00e9riment\",\"manifest\":{\"b\":123e-2,\"a\":1},\"profiles\":[{\"id\":1}]}";
        const string afterJsonb = "{ \"profiles\": [{\"id\": 1}], \"manifest\": {\"a\": 1.0, \"b\": 1.2300}, \"name\": \"expériment\" }";

        Assert.That(CanonicalJsonHash.Compute(afterJsonb), Is.EqualTo(CanonicalJsonHash.Compute(beforeJsonb)));
    }

    [Test]
    public async Task ResourceGovernor_BoundsWorkersAndHonoursCancellationWhileQueued()
    {
        using var governor = new SimulationResourceGovernor(new SimulationResourceGovernorOptions
        {
            MaxConcurrentExperiments = 1,
            MaxConcurrentProfileGroups = 2,
            MaxTotalStrategyWorkers = 2
        });
        await using IAsyncDisposable held = await governor.AcquireProfileGroupAsync(2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.That(
            async () => await governor.AcquireProfileGroupAsync(1, cancellationToken: cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(governor.GetUsage().StrategyWorkers, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExperimentEvaluation_CapturesReplayOnlyWithExplicitOperationalOptIn(bool optIn)
    {
        string artifacts = Path.Combine(Path.GetTempPath(), $"experiment-replay-{Guid.NewGuid():N}");
        try
        {
            var backtests = new RecordingBacktestApplicationService();
            var executor = new BacktestSimulationExperimentExecutor(
                backtests,
                new FileCalibrationArtifactRepository(artifacts),
                optIn ? new BacktestSimulationExperimentExecutorOptions { CaptureMarketReplay = true } : null);
            var run = new SimulationExperimentProfileRun
            {
                ProfileRunId = Guid.NewGuid(),
                Profile = SimulationStrategyProfileSnapshot.From(Profile(Guid.NewGuid()))
            };
            AnalysisWarmupPlan warmup = Warmup();
            var context = new SimulationExperimentExecutionContext
            {
                ExperimentId = Guid.NewGuid(),
                Manifest = new SimulationExperimentManifest
                {
                    Timeline = Timeline(),
                    ProfileRuns = [run],
                    TrainingWarmups = new Dictionary<Guid, AnalysisWarmupPlan> { [run.ProfileRunId] = warmup },
                    EvaluationWarmups = new Dictionary<Guid, AnalysisWarmupPlan> { [run.ProfileRunId] = warmup },
                    Parallelism = new SimulationExperimentParallelism()
                },
                WaitIfPausedAsync = () => ValueTask.CompletedTask,
                ReportProfileProgress = (_, _, _, _) => { }
            };

            await executor.EvaluateAsync(context, run, [], CancellationToken.None);

            Assert.That(backtests.Request!.CaptureMarketReplay, Is.EqualTo(optIn));
        }
        finally
        {
            if (Directory.Exists(artifacts))
                Directory.Delete(artifacts, recursive: true);
        }
    }

    [Test]
    public void ExperimentParallelism_DefaultsProfileGroupsToOne()
    {
        Assert.That(new SimulationExperimentParallelism().MaxProfileGroups, Is.EqualTo(1));
    }

    [Test]
    public void AgentTypeIds_UseExactCanonicalParsingWithoutFuzzyMatches()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TradingAgentTypeIds.Parse("STRUCTURAL-CONFLUENCE"),
                Is.EqualTo(TradingAgentKind.StructuralConfluence));
            Assert.That(TradingAgentTypeIds.TryParse("my-improved-experiment", out _), Is.False);
            Assert.That(TradingAgentTypeIds.Format(TradingAgentKind.StructuralConfluence),
                Is.EqualTo("structural-confluence"));
        });
    }

    [Test]
    public void CciRelationship_UsesPivotValueButIsUnavailableUntilSwingConfirmation()
    {
        var state = new CciAnalysisState(
            sampleCapacity: 20,
            pivotCapacity: 10,
            momentumLookback: 1,
            minimumCciDifference: 5m,
            minimumPriceDifferenceAtr: 0.01m);
        SwingPoint firstLow = Swing(0, confirmedIndex: 2, price: 1.1000m, SwingType.Low);
        SwingPoint secondLow = Swing(3, confirmedIndex: 5, price: 1.0990m, SwingType.Low);

        state.Update(Candle(0), -150m, [], 0.001m);
        state.Update(Candle(1), -110m, [], 0.001m);
        CciAnalysisSnapshot firstConfirmation = state.Update(Candle(2), -80m, [firstLow], 0.001m);
        state.Update(Candle(3), -100m, [], 0.001m);
        CciAnalysisSnapshot beforeConfirmation = state.Update(Candle(4), -60m, [], 0.001m);
        CciAnalysisSnapshot confirmed = state.Update(Candle(5), -40m, [secondLow], 0.001m);

        Assert.Multiple(() =>
        {
            Assert.That(firstConfirmation.LatestRelationship, Is.Null);
            Assert.That(beforeConfirmation.LatestRelationship, Is.Null);
            Assert.That(confirmed.LatestRelationship?.Type,
                Is.EqualTo(CciRelationshipType.RegularBullishDivergence));
            Assert.That(confirmed.LatestRelationship?.FirstCci, Is.EqualTo(-150m));
            Assert.That(confirmed.LatestRelationship?.SecondCci, Is.EqualTo(-100m));
            Assert.That(confirmed.LatestRelationship?.ConfirmedAt, Is.EqualTo(secondLow.ConfirmedAt));
            Assert.That(confirmed.IsNewRelationship, Is.True);
        });
    }

    [Test]
    public void Arbitrator_VetoesOpposingReadyPlaybooksAndCapsSameDirectionBonus()
    {
        PlaybookEvaluation bullish = Candidate("a", PriceActionDirection.Bullish, 60m);
        PlaybookEvaluation bearish = Candidate("b", PriceActionDirection.Bearish, 70m);
        var arbitrator = new StructuralCandidateArbitrator(new StructuralArbitrationOptions
        {
            SameDirectionConfluenceAdjustment = 8m
        });

        StructuralArbitrationResult conflict = arbitrator.Select([bullish, bearish]);
        StructuralArbitrationResult aligned = arbitrator.Select([
            bullish,
            Candidate("b", PriceActionDirection.Bullish, 99m)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(conflict.Selected, Is.Null);
            Assert.That(conflict.ReasonCode, Is.EqualTo("StructuralPlaybookConflict"));
            Assert.That(aligned.Selected?.Confidence, Is.EqualTo(100m));
            Assert.That(aligned.ConfluenceAdjustment, Is.EqualTo(8m));
        });
    }

    private static SimulationExperimentTimeline Timeline() => new()
    {
        LearningFrom = Start,
        LearningTo = Start.AddDays(20),
        EvaluationFrom = Start.AddDays(30),
        EvaluationTo = Start.AddDays(40),
        EmbargoDays = 10
    };

    private static SimulationStrategyProfile Profile(Guid id) => SimulationStrategyProfile.Create(new()
    {
        ProfileId = id,
        Revision = 1,
        Name = "Structural baseline",
        Agent = new TradingAgentDefinition
        {
            Kind = TradingAgentKind.StructuralConfluence,
            StructuralConfluence = new StructuralConfluenceStrategyOptions()
        },
        Instrument = Instrument,
        Analysis = new ChartAnnotationOptions
        {
            Liquidity = new LiquidityCalculationProfile { Enabled = true },
            SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
        },
        Calibration = new CalibrationExperimentPolicy { Mode = ExperimentCalibrationMode.Disabled },
        ContentHash = string.Empty
    });

    private static SimulationStrategyProfile ProfileDraft(
        Guid id,
        string playbook,
        bool liquidityEnabled,
        bool supplyDemandEnabled) => new()
    {
        ProfileId = id,
        Revision = 1,
        Name = $"Structural {playbook}",
        Agent = new TradingAgentDefinition
        {
            Kind = TradingAgentKind.StructuralConfluence,
            StructuralConfluence = new StructuralConfluenceStrategyOptions
            {
                LiquiditySweepReversal = new LiquiditySweepReversalOptions
                {
                    Enabled = playbook is "sweep" or "sweep-confluence",
                    SupplyDemandConfluence = playbook == "sweep-confluence"
                        ? StructuralConfluenceRequirement.Preferred
                        : StructuralConfluenceRequirement.Disabled
                },
                SupplyDemandPullback = new SupplyDemandPullbackOptions { Enabled = playbook == "pullback" },
                LiquidityBreakRetest = new LiquidityBreakRetestOptions { Enabled = playbook == "break-retest" }
            }
        },
        Instrument = Instrument,
        Analysis = new ChartAnnotationOptions
        {
            Liquidity = new LiquidityCalculationProfile { Enabled = liquidityEnabled },
            SupplyDemand = new SupplyDemandCalculationProfile { Enabled = supplyDemandEnabled }
        },
        Calibration = new CalibrationExperimentPolicy { Mode = ExperimentCalibrationMode.Disabled },
        ContentHash = string.Empty
    };

    private static AnalysisWarmupPlan Warmup() => new()
    {
        RequestedMinimumDays = 7,
        ResolvedWarmupDays = 7,
        StreamFrom = Start.AddDays(-7),
        RequiredAnalysisBars = 100,
        DominantRequirement = "test",
        HasSufficientData = true,
        PromotionEligible = true
    };

    private static SimulationProfileRunProgress Progress(
        SimulationExperimentProfileRun run,
        string name,
        decimal netProfit,
        decimal expectancy,
        decimal drawdown,
        decimal profitFactor,
        int tradeCount,
        IReadOnlyList<SimulationArtifactReference>? artifacts = null) => new()
    {
        ProfileRunId = run.ProfileRunId,
        ProfileName = name,
        Stage = SimulationExperimentStage.Aggregating,
        Artifacts = artifacts ?? [],
        Result = new SimulationExperimentComparisonRow
        {
            ProfileRunId = run.ProfileRunId,
            ProfileName = name,
            NetProfit = netProfit,
            Expectancy = expectancy,
            MaximumDrawdown = drawdown,
            ProfitFactor = profitFactor,
            TradeCount = tradeCount
        }
    };

    private static Candle Candle(int index)
    {
        DateTimeOffset open = Start.AddMinutes(index * 5);
        return new Candle
        {
            Instrument = Instrument,
            Interval = BarInterval.Minutes(5),
            OpenTime = open,
            CloseTime = open.AddMinutes(5),
            Prices = new Ohlc(1.1m, 1.101m, 1.099m, 1.1m),
            Volume = new MarketVolume(100m, VolumeKind.TickCount),
            IsComplete = true
        };
    }

    private static SwingPoint Swing(int pivotIndex, int confirmedIndex, decimal price, SwingType type) => new()
    {
        PivotTime = Start.AddMinutes(pivotIndex * 5),
        ConfirmedAt = Start.AddMinutes(confirmedIndex * 5),
        Price = price,
        Type = type,
        Strength = 2
    };

    private static PlaybookEvaluation Candidate(string id, PriceActionDirection direction, decimal confidence) => new()
    {
        PlaybookId = id,
        Version = "1",
        Direction = direction,
        Lifecycle = StructuralSetupLifecycle.CandidateProduced,
        CatalystAt = Start,
        MandatoryGates = [new MandatoryGate("structure", true, 0.8m, "StructureReady")],
        Confidence = confidence,
        GeometryQuality = 0.8m,
        ReasonCode = "Ready",
        IsReady = true,
        Geometry = new StructuralGeometry
        {
            IsValid = true,
            Entry = 1m,
            Stop = 0.9m,
            Target = 1.2m,
            RewardRisk = 2m,
            Quality = 0.8m,
            ReasonCode = "GeometryReady"
        }
    };

    private sealed class RecordingBacktestApplicationService : IBacktestApplicationService
    {
        public BacktestRequest? Request { get; private set; }

        public Task<ComparativeSimulationResult> RunToCompletionAsync(
            BacktestRequest request,
            IProgress<BacktestProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ComparativeSimulationResult
            {
                SimulationId = Guid.NewGuid(),
                InputStreamId = "experiment-test",
                InputHash = "experiment-test-hash",
                DataQuality = new MarketDataQualityReport
                {
                    CandleCount = 0, DuplicateCount = 0, OutOfOrderCount = 0,
                    MissingIntervalCount = 0, WeekendGapCount = 0, SessionGapCount = 0,
                    IncompleteAggregateCount = 0, FirstCandle = request.From, LastCandle = request.To,
                    InputHash = "experiment-test-hash"
                },
                Strategies =
                [
                    new StrategySimulationResult
                    {
                        StrategyId = request.Strategies.Single(),
                        StrategyName = request.Strategies.Single(),
                        Result = new SimulationResult
                        {
                            StartedAt = request.From, EndedAt = request.To,
                            StartingBalance = request.StartingBalance, FinalBalance = request.StartingBalance,
                            FinalEquity = request.StartingBalance, UnrealizedProfitLoss = 0m, NetProfit = 0m,
                            TotalCommission = 0m, SubmittedOrders = 0, FilledOrders = 0, RejectedOrders = 0,
                            OpenPositions = [], Ledger = [], Trades = []
                        },
                        Metrics = new StrategyWorkerMetrics
                        {
                            StrategyName = request.Strategies.Single(), ProcessedFrames = 0,
                            TotalProcessingTime = TimeSpan.Zero, MaximumFrameProcessingTime = TimeSpan.Zero,
                            AverageFrameProcessingTime = TimeSpan.Zero, BarrierWaitTime = TimeSpan.Zero,
                            PeakChannelOccupancy = 0
                        }
                    }
                ],
                OutputDirectory = request.OutputDirectory,
                TotalDuration = TimeSpan.Zero,
                ProcessedBaseCandles = 0,
                FillModel = FillModel.MidpointPlusConfiguredSpread
            });
        }

        public Task<SimulationJobHandle> StartAsync(BacktestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SimulationJobSnapshot?> GetAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
            int take = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PauseAsync(Guid simulationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(Guid simulationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(Guid simulationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
