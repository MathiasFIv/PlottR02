using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

public sealed class TrialWriteGateTests
{
    [Fact]
    public async Task AcquireAsync_SameTrial_CriticalSectionsNeverOverlap()
    {
        var gate = new TrialWriteGate();
        var trialId = Guid.NewGuid();
        var holders = 0;
        var maxHolders = 0;

        async Task EnterAsync()
        {
            using var handle = await gate.AcquireAsync(trialId);
            var inside = Interlocked.Increment(ref holders);
            RecordHighWaterMark(ref maxHolders, inside);
            await Task.Delay(20);
            Interlocked.Decrement(ref holders);
        }

        await Task.WhenAll(EnterAsync(), EnterAsync());

        maxHolders.Should().Be(1, "writers of one trial must be strictly serialized");
        gate.TrackedTrialCount.Should().Be(0, "the entry retires once the last holder leaves");
    }

    [Fact]
    public async Task AcquireAsync_DifferentTrials_AreNotSerialized()
    {
        var gate = new TrialWriteGate();
        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HoldAsync(Guid trialId, TaskCompletionSource inside)
        {
            using var handle = await gate.AcquireAsync(trialId);
            inside.SetResult();
            await release.Task;
        }

        var first = Task.Run(() => HoldAsync(Guid.NewGuid(), firstInside));
        var second = Task.Run(() => HoldAsync(Guid.NewGuid(), secondInside));

        await Task.WhenAll(firstInside.Task, secondInside.Task).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(first, second);

        gate.TrackedTrialCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_IdleEntriesDoNotAccumulate()
    {
        var gate = new TrialWriteGate();
        for (var i = 0; i < 50; i++)
        {
            using var handle = await gate.AcquireAsync(Guid.NewGuid());
            gate.TrackedTrialCount.Should().Be(1, "the trial is held, so exactly one entry exists");
        }

        gate.TrackedTrialCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_WaiterCancelled_KeepsTrialLockedAndLeavesNoEntry()
    {
        var gate = new TrialWriteGate();
        var trialId = Guid.NewGuid();
        var holder = await gate.AcquireAsync(trialId);

        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(trialId, cts.Token);
        cts.Cancel();

        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.TrackedTrialCount.Should().Be(1);

        holder.Dispose();
        gate.TrackedTrialCount.Should().Be(0, "the cancelled waiter must not leak a reservation");

        using var next = await gate.AcquireAsync(trialId);
    }

    [Fact]
    public async Task Releaser_DisposeTwice_ReleasesTheTrialOnlyOnce()
    {
        var gate = new TrialWriteGate();
        var trialId = Guid.NewGuid();
        var handle = await gate.AcquireAsync(trialId);

        handle.Dispose();
        handle.Dispose();

        using var next = await gate.AcquireAsync(trialId);
        gate.TrackedTrialCount.Should().Be(1);
    }

    private static void RecordHighWaterMark(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
