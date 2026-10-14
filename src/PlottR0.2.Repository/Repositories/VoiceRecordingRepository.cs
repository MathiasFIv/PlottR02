using Microsoft.EntityFrameworkCore;
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
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        return await _db.VoiceRecordings.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<VoiceRecording> CreateAsync(VoiceRecording recording, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        _db.VoiceRecordings.Add(recording);
        await _db.SaveChangesAsync(ct);
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
}
