using Microsoft.EntityFrameworkCore;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;

namespace PlottR0._2.Repository.Repositories;

public sealed class VoiceRecordingRepository : IVoiceRecordingRepository
{
    private readonly PlottR0DbContext _db;

    public VoiceRecordingRepository(PlottR0DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<VoiceRecording?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings.FindAsync([id], ct);
    }

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

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings.AnyAsync(v => v.Id == id, ct);
    }

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
            throw new ClientRecordingIdConflictException(
                $"A voice recording with client recording id '{recording.ClientRecordingId}' already exists.",
                ex);
        }

        return recording;
    }

    public async Task<VoiceRecording?> GetByClientRecordingIdAsync(string clientRecordingId, CancellationToken ct = default)
    {
        return await _db.VoiceRecordings
            .FirstOrDefaultAsync(v => v.ClientRecordingId == clientRecordingId, ct);
    }

    public async Task UpdateAsync(VoiceRecording recording, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        _db.VoiceRecordings.Update(recording);
        await _db.SaveChangesAsync(ct);
    }

    // The unique index on ClientRecordingId is the PWA idempotency key; the Postgres violation is
    // translated here so services can handle a concurrent insert without referencing EF Core.
    private static bool IsClientRecordingIdConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message;
        return message is not null
            && (message.Contains("IX_VoiceRecordings_ClientRecordingId", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));
    }
}
