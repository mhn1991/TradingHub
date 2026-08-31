using System.Reflection;
using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Evaluation;
using TradingClassifier.ML.Experiments;
using TradingClassifier.Models;

namespace Simulator.Tests;

/// <summary>
/// V2 Phase 0a: every command must measure the same executable strategy.
/// <para>
/// `ClassifierBacktester.Run`, `ModelEvaluator`, `WalkForwardRunner` and `FeatureExperimentRunner`
/// all defaulted `allowOverlappingPositions` to <c>true</c>, while only `walk-forward` passed the
/// command-line flag. `train`, `ladder` and `ablate` therefore scored a strategy holding up to
/// `PredictionHorizon` positions at once, and their numbers were never comparable with
/// `walk-forward`'s. These tests pin both halves of the fix: the safe default, and the fact that
/// every construction in the runner threads one shared policy.
/// </para>
/// </summary>
[TestFixture]
public sealed class OverlapPolicyParityTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 3, 0, 0, TimeSpan.Zero);

    // ---- the parameter is load-bearing, and its default is the tradeable one -------------------

    [Test]
    public void Backtester_DefaultsToOneOpenPositionAndTheFlagChangesTheTradeCount()
    {
        (LabeledFeatureRow[] rows, Prediction[] predictions) = RisingSeries(count: 7);
        ClassifierOptions options = new() { PredictionHorizon = 3, BuyProbabilityThreshold = 0.5 };

        TradingReport defaulted = ClassifierBacktester.Run(rows, predictions, options);
        TradingReport serial = ClassifierBacktester.Run(rows, predictions, options,
            allowOverlappingPositions: false);
        TradingReport overlapping = ClassifierBacktester.Run(rows, predictions, options,
            allowOverlappingPositions: true);

        Assert.Multiple(() =>
        {
            // Four bars are actionable; serialised, only two of them can be traded.
            Assert.That(overlapping.Trades, Is.EqualTo(4));
            Assert.That(serial.Trades, Is.EqualTo(2));

            // The default must be the tradeable policy, not the inflated one.
            Assert.That(defaulted.Trades, Is.EqualTo(serial.Trades),
                "ClassifierBacktester must default to one position at a time.");

            Assert.That(serial.MaximumConcurrentPositions, Is.EqualTo(1));
            Assert.That(overlapping.MaximumConcurrentPositions, Is.GreaterThan(1),
                "The overlap scenario must actually overlap, or this test proves nothing.");
        });
    }

    // ---- the defect class cannot silently return ----------------------------------------------

    [Test]
    public void EveryEvaluationEntryPoint_DefaultsToNoOverlap()
    {
        (string Where, ParameterInfo Parameter)[] entryPoints =
        [
            ("ClassifierBacktester.Run", Parameter(typeof(ClassifierBacktester).GetMethod(
                nameof(ClassifierBacktester.Run))!)),
            ("ModelEvaluator..ctor", Parameter(Constructor(typeof(ModelEvaluator)))),
            ("WalkForwardRunner..ctor", Parameter(Constructor(typeof(WalkForwardRunner)))),
            ("FeatureExperimentRunner..ctor", Parameter(Constructor(typeof(FeatureExperimentRunner))))
        ];

        Assert.Multiple(() =>
        {
            foreach ((string where, ParameterInfo parameter) in entryPoints)
            {
                Assert.That(parameter.HasDefaultValue, Is.True, $"{where} must keep an explicit default.");
                Assert.That(parameter.DefaultValue, Is.False,
                    $"{where} defaults to overlapping positions, which no single account can trade.");
            }
        });
    }

    // ---- all commands genuinely share one policy ----------------------------------------------

    [Test]
    public void RunnerCommands_AllThreadTheSameOverlapPolicy()
    {
        string source = File.ReadAllText(FindRunnerProgram());

        // The flag is read exactly once, into one variable every command then passes on.
        Assert.That(CountOccurrences(source, "flags.ContainsKey(\"allow-overlap\")"), Is.EqualTo(1),
            "The overlap flag must be read once and shared, not re-read per command.");

        string[] constructions =
        [
            "ModelEvaluator evaluator = new(options, costs, allowOverlappingPositions: allowOverlap)",
            "new ModelEvaluator(tuned, costs, allowOverlappingPositions: allowOverlap)",
            "WalkForwardRunner runner = new(options, NewTrainer(), costs, allowOverlap)"
        ];

        Assert.Multiple(() =>
        {
            foreach (string construction in constructions)
            {
                Assert.That(source, Does.Contain(construction),
                    $"A runner command no longer passes the shared overlap policy: {construction}");
            }

            // ladder and ablate build the same shape; both must pass the policy.
            Assert.That(CountOccurrences(source, "FeatureExperimentRunner runner = new("), Is.EqualTo(2));
            Assert.That(CountOccurrences(source, "        allowOverlap);"), Is.EqualTo(2),
                "Both ladder and ablate must pass the shared overlap policy to FeatureExperimentRunner.");
        });
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Seven rising bars, every one of them a confident BUY.</summary>
    private static (LabeledFeatureRow[] Rows, Prediction[] Predictions) RisingSeries(int count)
    {
        LabeledFeatureRow[] rows = [.. Enumerable.Range(0, count).Select(index => new LabeledFeatureRow
        {
            Features = new FeatureVector
            {
                Timestamp = Start.AddMinutes(index * 5),
                Close = 100m + index,
                LabelAtr = 1m,
                Values = [0f]
            },
            Label = TradeLabel.NoTrade,
            LabelExcursionAtr = 0m
        })];

        Prediction[] predictions = [.. Enumerable.Repeat(
            new Prediction { SellProbability = 0.1, NoTradeProbability = 0.2, BuyProbability = 0.7 }, count)];

        return (rows, predictions);
    }

    private static ParameterInfo Parameter(MethodBase method) =>
        method.GetParameters().Single(item => item.Name == "allowOverlappingPositions");

    private static ConstructorInfo Constructor(Type type) =>
        type.GetConstructors().Single(item =>
            item.GetParameters().Any(parameter => parameter.Name == "allowOverlappingPositions"));

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRunnerProgram()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "TradingClassifierRunner", "Program.cs");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate TradingClassifierRunner/Program.cs for the parity check.");
    }
}
