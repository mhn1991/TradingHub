using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

[TestFixture]
public sealed class BacktestStartupRecoveryTests
{
    [Test]
    public async Task ServiceInitialization_RecoversInterruptedJobsThroughRepositoryContract()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var repository = new RecoveringRepository(new SimulationJobSnapshot
        {
            Id = Guid.NewGuid(),
            Revision = 4,
            Status = SimulationJobStatus.Running,
            CreatedAt = now.AddMinutes(-5),
            StartedAt = now.AddMinutes(-4),
            Instrument = "FX:EUR/USD",
            RequestedFrom = now.AddDays(-1),
            RequestedTo = now,
            ProcessedBaseCandles = 500,
            ProgressPercent = 17m,
            CandlesPerSecond = 2m,
            Strategies = [],
            IsComplete = false,
            OutputDirectory = "/retained/partial-artifacts"
        });

        await using var service = new BacktestApplicationService(
            repository,
            new BacktestApplicationServiceOptions
            {
                MaxConcurrentJobs = 1,
                QueueCapacity = 1
            });

        IReadOnlyList<SimulationJobSnapshot> recovered = await service.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(repository.RecoveryCalls, Is.EqualTo(1));
            Assert.That(recovered, Has.Count.EqualTo(1));
            Assert.That(recovered[0].Status, Is.EqualTo(SimulationJobStatus.Failed));
            Assert.That(recovered[0].IsComplete, Is.True);
            Assert.That(recovered[0].Error, Does.StartWith("HostRestartedWhileRunning:"));
            Assert.That(recovered[0].OutputDirectory, Is.EqualTo("/retained/partial-artifacts"));
        });
    }

    private sealed class RecoveringRepository(SimulationJobSnapshot snapshot) : ISimulationJobRepository
    {
        private SimulationJobSnapshot _snapshot = snapshot;

        public int RecoveryCalls { get; private set; }

        public Task MarkInterruptedJobsAsync(CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            if (!_snapshot.IsComplete)
            {
                _snapshot = _snapshot with
                {
                    Revision = _snapshot.Revision + 1,
                    Status = SimulationJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = "HostRestartedWhileRunning: The host restarted before this simulation could complete. Partial replay output remains available.",
                    IsComplete = true
                };
            }

            return Task.CompletedTask;
        }

        public Task SaveAsync(
            SimulationJobSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            _snapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<SimulationJobSnapshot?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SimulationJobSnapshot?>(_snapshot.Id == id ? _snapshot : null);

        public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SimulationJobSnapshot>>([_snapshot]);
    }
}
