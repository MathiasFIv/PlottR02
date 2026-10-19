namespace PlottR0._2.Contracts.Recordings;

/// <summary>
/// Thrown by <c>IVoiceRecordingRepository.CreateAsync</c> when a concurrent insert already
/// owns the same <c>VoiceRecording.ClientRecordingId</c> idempotency key (the
/// <c>IX_VoiceRecordings_ClientRecordingId</c> unique index rejected the row).
/// <para>
/// This is the repository-to-service seam: the EF/Npgsql-specific classification stays in
/// the Repository layer, so business services can treat the race as "already written" without
/// referencing Entity Framework. Any other persistence failure propagates unchanged.
/// </para>
/// </summary>
public sealed class ClientRecordingIdConflictException : Exception
{
    /// <summary>Creates a new <see cref="ClientRecordingIdConflictException"/>.</summary>
    /// <param name="message">The reason the insert was rejected.</param>
    /// <param name="innerException">The original persistence exception, when available.</param>
    public ClientRecordingIdConflictException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
