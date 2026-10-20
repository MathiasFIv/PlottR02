namespace PlottR0._2.Contracts.Recordings;

/// <summary>
/// Raised at the repository boundary when a concurrent insert loses the race on the idempotency
/// key, so the service layer can answer the loser as a replay without seeing EF Core types.
/// </summary>
public sealed class ClientRecordingIdConflictException : Exception
{
    public ClientRecordingIdConflictException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
