using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Test double for <see cref="IVoiceCellEditService"/> — records the last call's arguments
/// and either returns <see cref="ResultToReturn"/> or throws <see cref="ExceptionToThrow"/>.
/// Used by controller tests that invoke a non-voice action (the voice service is never
/// reached) and by voice-flow tests that assert the exact arguments forwarded.
/// </summary>
public sealed class FakeVoiceCellEditService : IVoiceCellEditService
{
    /// <summary>The result to return when <see cref="ExceptionToThrow"/> is null.</summary>
    public VoiceCellEditResult? ResultToReturn { get; set; }

    /// <summary>When set, the call fails with this exception instead of returning a result.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Number of <see cref="TranscribeAndWriteAsync"/> calls made so far.</summary>
    public int CallCount { get; private set; }

    /// <summary>The trial id of the last call, if any.</summary>
    public Guid? CalledWithTrialId { get; private set; }

    /// <summary>The request of the last call, if any.</summary>
    public VoiceCellEditRequest? CalledWithRequest { get; private set; }

    /// <summary>The audio file name of the last call, if any.</summary>
    public string? CalledWithFileName { get; private set; }

    /// <inheritdoc />
    public Task<VoiceCellEditResult> TranscribeAndWriteAsync(
        Guid trialId,
        VoiceCellEditRequest request,
        Stream audio,
        string fileName,
        CancellationToken ct = default)
    {
        CallCount++;
        CalledWithTrialId = trialId;
        CalledWithRequest = request;
        CalledWithFileName = fileName;

        if (ExceptionToThrow is not null)
            return Task.FromException<VoiceCellEditResult>(ExceptionToThrow);

        return Task.FromResult(ResultToReturn
            ?? throw new InvalidOperationException(
                "FakeVoiceCellEditService.ResultToReturn was not set."));
    }
}
