using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IVoiceRecordingRepository"/> backed by PostgreSQL
/// via <see cref="PlottR0DbContext"/>.
/// </summary>
public sealed class VoiceRecordingRepository : IVoiceRecordingRepository
{
    private readonly PlottR0DbContext _db;

    public VoiceRecordingRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<VoiceRecording?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings.FindAsync([id], ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceRecording>> GetAllAsync(
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        IQueryable<VoiceRecording> query = _db.VoiceRecordings.OrderByDescending(v => v.CreatedAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings.AnyAsync(v => v.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<VoiceRecording> CreateAsync(VoiceRecording recording, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        try
        {
            _db.VoiceRecordings.Add(recording);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsClientRecordingIdConflict(ex))
        {
            // Translate the EF/Npgsql-specific unique-index failure at the repository boundary
            // so services can handle the concurrent-insert race without referencing EF Core.
            // Unclassified DbUpdateExceptions (FK violations, etc.) propagate unchanged.
            throw new ClientRecordingIdConflictException(
                $"A voice recording with client recording id '{recording.ClientRecordingId}' already exists.",
                ex);
        }

        return recording;
    }

    /// <inheritdoc />
    public async Task<VoiceRecording?> GetByClientRecordingIdAsync(string clientRecordingId, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings
            .FirstOrDefaultAsync(v => v.ClientRecordingId == clientRecordingId, ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(VoiceRecording recording, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        _db.VoiceRecordings.Update(recording);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// True when a <see cref="DbUpdateException"/> was caused by the
    /// <c>IX_VoiceRecordings_ClientRecordingId</c> unique index — a concurrent double-submit
    /// raced past the idempotency gate and committed first. Npgsql surfaces the underlying
    /// Postgres <c>23505 unique_violation</c> inside the inner exception message.
    /// </summary>
    private static bool IsClientRecordingIdConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message;
        return message is not null
            && (message.Contains("IX_VoiceRecordings_ClientRecordingId", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));
    }
}
