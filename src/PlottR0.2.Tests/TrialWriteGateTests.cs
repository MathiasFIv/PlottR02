using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for <see cref="TrialWriteGate"/> — the per-trial serialization every workbook
/// critical section is wrapped in (see <c>ITrialWriteGate</c>, <c>TrialService.EditCellAsync</c>
/// and the two write blocks of <c>RecordingSyncService</c>).
/// <para>
/// Pure in-process tests: no Postgres, no workbooks — only the locking semantics, so they run
/// on every build and pin down the behaviour the workbook tests can only exercise end to end.
/// </para>
/// </summary>
public sealed class TrialWriteGateTests
{
    [Fact]
    public async Task AcquireAsync_SameTrial_CriticalSectionsNeverOverlap()
    {
        // Arrange — two callers that each hold the gate briefly and report how many holders
        // are inside at the same moment.
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

        // Act — fired together, so without serialization both would be inside at once.
        await Task.WhenAll(EnterAsync(), EnterAsync());

        // Assert
        maxHolders.Should().Be(1, "writers of one trial must be strictly serialized");
        gate.TrackedTrialCount.Should().Be(0, "the entry retires once the last holder leaves");
    }

    [Fact]
    public async Task AcquireAsync_DifferentTrials_AreNotSerialized()
    {
        // Arrange — each holder blocks its own trial open until told to leave, so both only
        // reach the "inside" signal while the OTHER trial is still held.
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

        // Act — both must get in while the other still holds its trial: a gate that also
        // serialized across trials would deadlock here and trip the timeout.
        await Task.WhenAll(firstInside.Task, secondInside.Task).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(first, second);

        // Assert — reached only if the parallel case above completed.
        gate.TrackedTrialCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_IdleEntriesDoNotAccumulate()
    {
        // Arrange & Act — acquire and release a gate per trial, one trial at a time.
        var gate = new TrialWriteGate();
        for (var i = 0; i < 50; i++)
        {
            using var handle = await gate.AcquireAsync(Guid.NewGuid());
            gate.TrackedTrialCount.Should().Be(1, "the trial is held, so exactly one entry exists");
        }

        // Assert — nothing outlives its users, so the gate stays O(active trials), not O(all
        // trials this process ever wrote).
        gate.TrackedTrialCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_WaiterCancelled_KeepsTrialLockedAndLeavesNoEntry()
    {
        // Arrange — the first caller holds the trial while a second one waits, then gives up.
        var gate = new TrialWriteGate();
        var trialId = Guid.NewGuid();
        var holder = await gate.AcquireAsync(trialId);

        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(trialId, cts.Token);
        cts.Cancel();

        // Act & Assert — the cancellation unwinds the wait without touching the holder.
        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The still-held trial reports its holder, and only that holder's entry exists.
        gate.TrackedTrialCount.Should().Be(1);

        holder.Dispose();
        gate.TrackedTrialCount.Should().Be(0, "the cancelled waiter must not leak a reservation");

        // …and the trial is free again for the next writer.
        using var next = await gate.AcquireAsync(trialId);
    }

    [Fact]
    public async Task Releaser_DisposeTwice_ReleasesTheTrialOnlyOnce()
    {
        // Arrange — a double dispose must not hand out a second permit (which would let two
        // writers into the same trial) nor corrupt the entry bookkeeping.
        var gate = new TrialWriteGate();
        var trialId = Guid.NewGuid();
        var handle = await gate.AcquireAsync(trialId);

        // Act
        handle.Dispose();
        handle.Dispose();

        // Assert — the trial is free (not locked by the phantom second release) and clean.
        using var next = await gate.AcquireAsync(trialId);
        gate.TrackedTrialCount.Should().Be(1);
    }

    /// <summary>CAS-loops the highest observed value into <paramref name="target"/>.</summary>
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
