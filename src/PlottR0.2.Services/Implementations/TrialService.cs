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
/// upload + DB persistence + audit) and trial listing. File I/O is delegated to
/// <see cref="ITrialWorkbookStore"/>; persistence to <see cref="ITrialRepository"/>.
/// </summary>
public class TrialService : ITrialService
{
    private readonly ITrialRepository _trialRepository;
    private readonly ITrialWorkbookStore _workbookStore;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TrialService> _logger;

    public TrialService(
        ITrialRepository trialRepository,
        ITrialWorkbookStore workbookStore,
        IAuditService auditService,
        ICurrentUser currentUser,
        ILogger<TrialService> logger)
    {
        _trialRepository = trialRepository;
        _workbookStore = workbookStore;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
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
        var trial = await _trialRepository.GetByIdAsync(trialId, ct);
        if (trial is null)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        var path = _workbookStore.ResolveExportPath(trial.Id, trial.SourceWorkbookPath ?? string.Empty);
        if (path is null)
            throw new FileNotFoundException($"Workbook for trial '{trialId}' was not found on disk.");

        return new WorkbookExport(path, Path.GetFileName(path));
    }
}
