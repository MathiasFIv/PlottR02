using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Mappers;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="ITrialService"/> that orchestrates trial creation (workbook
/// upload + DB persistence + audit), trial listing, workbook export and the read-only
/// sheet endpoint. File I/O is delegated to <see cref="ITrialWorkbookStore"/> and
/// <see cref="IRatingRepository"/>; persistence to <see cref="ITrialRepository"/>.
/// </summary>
public class TrialService : ITrialService
{
    private readonly ITrialRepository _trialRepository;
    private readonly ITrialWorkbookStore _workbookStore;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TrialService> _logger;
    private readonly IRatingRepository _ratingRepository;

    public TrialService(
        ITrialRepository trialRepository,
        ITrialWorkbookStore workbookStore,
        IAuditService auditService,
        ICurrentUser currentUser,
        ILogger<TrialService> logger,
        IRatingRepository ratingRepository)
    {
        _trialRepository = trialRepository;
        _workbookStore = workbookStore;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
        _ratingRepository = ratingRepository;
    }

    /// <inheritdoc />
    public async Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var trialId = Guid.NewGuid();

        // Delegate workbook validation + file persistence to the store.
        // The store throws InvalidDataException for non-workbooks and ArgumentException
        // for wrong extensions — those propagate to the controller as BadRequest.
        var relativePath = await _workbookStore.SaveAsync(trialId, fileName, content, ct);

        var trial = new Trial
        {
            Id = trialId,
            ExternalId = Path.GetFileNameWithoutExtension(fileName),
            FileName = Path.GetFileName(fileName),
            Status = TrialStatus.Created,
            SourceWorkbookPath = relativePath,
            CreatedBy = _currentUser.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            var created = await _trialRepository.CreateAsync(trial, ct);

            _logger.LogInformation(
                "Trial {TrialId} created with workbook '{FileName}', source path '{Path}'.",
                created.Id, created.FileName, relativePath);

            await _auditService.LogAsync(
                "Trial.Created",
                "Trial",
                created.Id.ToString(),
                _currentUser.UserId,
                _currentUser.Name,
                JsonSerializer.Serialize(new
                {
                    trialId = created.Id,
                    externalId = created.ExternalId,
                    fileName = created.FileName,
                    sourceWorkbookPath = relativePath,
                }),
                ct);

            return TrialMapper.ToSummary(created);
        }
        catch
        {
            // Best-effort cleanup: remove the workbook file that was already persisted
            // before the DB insert failed, so no orphaned files are left on disk.
            await _workbookStore.DeleteAsync(relativePath, ct);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default)
    {
        var trials = await _trialRepository.GetAllAsync(ct: ct);
        return TrialMapper.ToSummaries(trials);
    }

    /// <inheritdoc />
    public async Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default)
    {
        var export = await ResolveExportAsync(trialId, ct);
        return new WorkbookExport(export.FilePath, export.FileName);
    }

    /// <inheritdoc />
    public async Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default)
    {
        // Resolve path + version in ONE store call (no path/version TOCTOU).
        var export = await ResolveExportAsync(trialId, ct);

        // Fast path (decision A3): version unchanged ⇒ return the slim unchanged payload
        // WITHOUT opening the workbook. A malformed/non-matching `since` simply never
        // equals the version, so a poller can never hard-fail on its own query string.
        if (since is not null && since == export.Version)
        {
            return new TrialSheet(
                trialId.ToString(), export.Version, Changed: false,
                FileName: null, GeneratedAt: null, Columns: null, Rows: null, Metadata: null);
        }

        // One region read per changed poll (sync by design — ClosedXML cannot abort
        // mid-parse; ~10–60 ms, so no Task.Run hop). No ClosedXML/file I/O in this layer:
        // everything lives behind IRatingRepository.
        ct.ThrowIfCancellationRequested();
        var region = _ratingRepository.ReadSheetRegion(export.FilePath);
        ct.ThrowIfCancellationRequested();

        return new TrialSheet(
            trialId.ToString(), export.Version, Changed: true,
            export.FileName, DateTime.UtcNow,
            region.Columns, region.Rows, region.Metadata);
    }

    /// <summary>
    /// Loads the trial and resolves its current workbook export (path + file name + opaque
    /// version) — the ONE place path resolution happens, shared by
    /// <see cref="GetWorkbookExportAsync"/> and <see cref="GetSheetAsync"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="FileNotFoundException">No workbook file is left on disk for the trial.</exception>
    private async Task<ResolvedWorkbookExport> ResolveExportAsync(Guid trialId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trial = await _trialRepository.GetByIdAsync(trialId, ct);
        if (trial is null)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        ct.ThrowIfCancellationRequested();

        var export = _workbookStore.ResolveExport(trial.Id, trial.SourceWorkbookPath ?? string.Empty);
        if (export is null)
            throw new FileNotFoundException($"Workbook for trial '{trialId}' was not found on disk.");

        return export;
    }
}
