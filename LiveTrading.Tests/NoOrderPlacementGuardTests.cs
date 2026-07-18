using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;

namespace LiveTrading.Tests;

/// <summary>
/// The live host now contains the manually approved execution path, but activation remains an
/// explicit deployment decision. These source-level guards ensure a normal checkout cannot write
/// to a broker and that enabling writes still retains the hard OANDA Practice/Demo boundary.
/// </summary>
[TestFixture]
public sealed class LiveExecutionActivationGuardTests
{
    [Test]
    public void HostDefaultsKeepEveryBrokerMutationDisabled()
    {
        string appsettings = Path.Combine(RepoRoot(), "LiveTradingHost", "appsettings.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(appsettings));
        JsonElement execution = document.RootElement.GetProperty("LiveExecution");

        Assert.Multiple(() =>
        {
            Assert.That(execution.GetProperty("BrokerWritesEnabled").GetBoolean(), Is.False);
            Assert.That(execution.GetProperty("AutomaticExecutionEnabled").GetBoolean(), Is.False);
            Assert.That(execution.GetProperty("PartialCloseEnabled").GetBoolean(), Is.False);
            Assert.That(execution.GetProperty("DynamicStopReplacementEnabled").GetBoolean(), Is.False);
        });
    }

    [Test]
    public void HostRejectsBrokerWritesOutsideOandaPractice()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "LiveTradingHost", "Program.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(program, Does.Contain("execution.BrokerWritesEnabled"));
            Assert.That(program, Does.Contain("oanda.Environment != Brokers.Abstractions.BrokerEnvironment.Demo"));
            Assert.That(program, Does.Contain("Broker writes are permitted only for OANDA Practice/Demo"));
        });
    }

    [Test]
    public void OandaTradingFactoryRetainsExplicitDemoGuard()
    {
        string factory = Path.Combine(RepoRoot(), "LiveTrading.Oanda", "OandaBrokerClientFactory.cs");
        string source = File.ReadAllText(factory);
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("CreateDemoTrading"));
            Assert.That(source, Does.Contain("options.Environment != BrokerEnvironment.Demo"));
            Assert.That(source, Does.Contain("throw new InvalidOperationException"));
        });
    }

    private static string RepoRoot([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, ".."));
}
