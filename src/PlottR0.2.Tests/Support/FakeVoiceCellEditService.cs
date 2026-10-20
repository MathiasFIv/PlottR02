using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Tests.Support;

public sealed class FakeVoiceCellEditService : IVoiceCellEditService
{
    public VoiceCellEditResult? ResultToReturn { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public Guid? CalledWithTrialId { get; private set; }

    public VoiceCellEditRequest? CalledWithRequest { get; private set; }

    public string? CalledWithFileName { get; private set; }

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
