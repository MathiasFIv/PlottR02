using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Mappers;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

public class TrialService : ITrialService
{
    private readonly ITrialRepository _trialRepository;
    private readonly ITrialWorkbookStore _workbookStore;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TrialService> _logger;
    private readonly IRatingRepository _ratingRepository;
    private readonly IUserRepository _userRepository;
    private readonly ITrialWriteGate _writeGate;

    public TrialService(
        ITrialRepository trialRepository,
        ITrialWorkbookStore workbookStore,
        IAuditService auditService,
        ICurrentUser currentUser,
        ILogger<TrialService> logger,
        IRatingRepository ratingRepository,
        IUserRepository userRepository,
        ITrialWriteGate writeGate)
    {
        _trialRepository = trialRepository;
        _workbookStore = workbookStore;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
        _ratingRepository = ratingRepository;
        _userRepository = userRepository;
        _writeGate = writeGate;
    }

    public async Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        if (_currentUser.Role is not (UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager))
        {
            throw new ForbiddenAccessException(
                "Only researchers, trial managers, or system administrators can create trials.");
        }

        if (_currentUser.UserId is not Guid creatorId)
        {
            throw new ForbiddenAccessException(
                "Your account has no user identifier; re-authenticate.");
        }

        var trialId = Guid.NewGuid();

        var relativePath = await _workbookStore.SaveAsync(trialId, fileName, content, ct);

        var trial = new Trial
        {
            Id = trialId,
            ExternalId = Path.GetFileNameWithoutExtension(fileName),
            FileName = Path.GetFileName(fileName),
            Status = TrialStatus.Created,
            SourceWorkbookPath = relativePath,
            CreatedBy = creatorId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var creatorIsAssigned =
            _currentUser.Role is UserRole.Researcher or UserRole.TrialManager;

        if (creatorIsAssigned)
        {
            trial.Assignments =
            [
                new TrialAssignment
                {
                    Id = Guid.NewGuid(),
                    TrialId = trialId,
                    UserId = creatorId,
                    AssignedBy = creatorId,
                    AssignedAt = DateTime.UtcNow,
                },
            ];
        }

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

            return TrialMapper.ToSummary(created, creatorIsAssigned, _currentUser.Name);
        }
        catch
        {
            await _workbookStore.DeleteAsync(relativePath, ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default)
    {
        if (IsManager(_currentUser.Role))
        {
            var all = await _trialRepository.GetAllAsync(ct: ct);

            IReadOnlySet<Guid>? assignedTrialIds = null;
            if (_currentUser.Role is not UserRole.SystemAdmin && _currentUser.UserId is Guid managerId)
                assignedTrialIds = (await _trialRepository.GetAssignedTrialIdsAsync(managerId, ct)).ToHashSet();

            return TrialMapper.ToSummaries(all, assignedTrialIds);
        }

        if (_currentUser.UserId is not Guid userId)
            return [];

        var assignedIds = await _trialRepository.GetAssignedTrialIdsAsync(userId, ct);
        if (assignedIds.Count == 0)
            return [];

        var assignedSet = assignedIds.ToHashSet();
        var trials = await _trialRepository.GetAllAsync(ct: ct);

        return TrialMapper.ToSummaries(trials.Where(t => assignedSet.Contains(t.Id)), assignedSet);
    }

    public async Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default)
    {
        var export = await ResolveExportAsync(trialId, ct);
        return new WorkbookExport(export.FilePath, export.FileName);
    }

    public async Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default)
    {
        var export = await ResolveExportAsync(trialId, ct);

        if (since is not null && since == export.Version)
        {
            return new TrialSheet(
                trialId.ToString(), export.Version, Changed: false,
                FileName: null, GeneratedAt: null, Columns: null, Rows: null, Metadata: null);
        }

        ct.ThrowIfCancellationRequested();
        var region = _ratingRepository.ReadSheetRegion(export.FilePath);
        ct.ThrowIfCancellationRequested();

        return new TrialSheet(
            trialId.ToString(), export.Version, Changed: true,
            export.FileName, DateTime.UtcNow,
            region.Columns, region.Rows, region.Metadata);
    }

    /// <summary>
    /// Serializes the whole read-modify-write behind the per-trial write gate, so a concurrent
    /// edit or sync cannot resolve the same input copy and silently drop the other's write. A
    /// filled cell without <c>overwrite</c> is refused (reported skipped), not an error.
    /// </summary>
    public async Task<CellEditResult> EditCellAsync(
        Guid trialId, EditCellRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Plot <= 0)
            throw new ArgumentException("Plot must be greater than zero.", nameof(request));
        if (request.Subsample <= 0)
            throw new ArgumentException("Subsample must be greater than zero.", nameof(request));
        if (request.TraitOrdinal is < 1 or > 9)
            throw new ArgumentException("TraitOrdinal must be within 1..9.", nameof(request));
        if (request.Value is not int value || value is < 0 or > 10)
            throw new ArgumentException("Value must be present and within 0..10.", nameof(request));

        var trial = await LoadTrialForAccessAsync(trialId, ct);

        var outputDirectory = _workbookStore.GetWorkingDirectory(trialId);

        using var writeGate = await _writeGate.AcquireAsync(trialId, ct);

        var export = await _workbookStore.ResolveExportAsync(
            trialId, trial.SourceWorkbookPath ?? string.Empty, ct)
            ?? throw new FileNotFoundException($"Workbook for trial '{trialId}' was not found on disk.");

        var result = _ratingRepository.WriteSingleCell(
            export.FilePath,
            outputDirectory,
            request.Plot,
            request.Subsample,
            request.TraitOrdinal,
            value,
            request.Overwrite);

        if (result.Written)
        {
            await _workbookStore.PersistTrialFilesAsync(trialId, ct);

            await _auditService.LogAsync(
                "Trial.CellEdited",
                "Trial",
                trialId.ToString(),
                _currentUser.UserId,
                _currentUser.Name,
                JsonSerializer.Serialize(new
                {
                    trialId,
                    plot = request.Plot,
                    subsample = request.Subsample,
                    traitOrdinal = request.TraitOrdinal,
                    cellAddress = result.CellAddress,
                    priorValue = result.PriorValue,
                    newValue = value,
                    overwrite = request.Overwrite,
                }),
                ct);
        }

        var resolved = await _workbookStore.ResolveExportAsync(
            trialId, trial.SourceWorkbookPath ?? string.Empty, ct);

        return new CellEditResult(
            trialId.ToString(),
            request.Plot,
            request.Subsample,
            request.TraitOrdinal,
            value,
            result.Written,
            result.Skipped,
            result.SkippedReason,
            result.PriorValue,
            result.CellAddress,
            resolved?.Version ?? export.Version,
            resolved?.FileName,
            DateTime.UtcNow);
    }

    public async Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default)
    {
        await LoadTrialForAccessAsync(trialId, ct);
    }

    public async Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureCanAssignUsers();

        if (!await _trialRepository.ExistsAsync(trialId, ct))
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        var requestedIds = request.UserIds;

        if (requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("UserIds must be distinct.");

        var validatedIds = new List<Guid>(requestedIds.Count);
        foreach (var userId in requestedIds)
        {
            var user = await _userRepository.GetByIdAsync(userId, ct);
            if (user is null)
                throw new ArgumentException($"User '{userId}' was not found.");

            if (user.Role is null)
                throw new ArgumentException(
                    $"User '{userId}' has no role and cannot be assigned to a trial.");

            if (user.Role == UserRole.SystemAdmin)
                throw new ArgumentException(
                    "System administrators are above the matrix and need no trial assignment.");

            if (user.Role is not (UserRole.Researcher or UserRole.TrialManager or UserRole.FieldWorker))
                throw new ArgumentException(
                    $"User '{userId}' has role '{user.Role}' which cannot be assigned to a trial.");

            validatedIds.Add(userId);
        }

        await _trialRepository.SetAssignmentsAsync(
            trialId, validatedIds, _currentUser.UserId ?? Guid.Empty, ct);

        await _auditService.LogAsync(
            "Trial.UsersAssigned",
            "Trial",
            trialId.ToString(),
            _currentUser.UserId,
            _currentUser.Name,
            JsonSerializer.Serialize(new { trialId, count = validatedIds.Count }),
            ct);
    }

    public async Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default)
    {
        EnsureCanAssignUsers();

        if (!await _trialRepository.ExistsAsync(trialId, ct))
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        var assignments = await _trialRepository.GetAssignmentsAsync(trialId, ct);

        var users = new List<UserResponse>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var user = await _userRepository.GetByIdAsync(assignment.UserId, ct);
            if (user is not null)
                users.Add(UserMapper.ToResponse(user));
        }

        return users;
    }

    private void EnsureCanAssignUsers()
    {
        if (_currentUser.Role is not (UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager))
        {
            throw new ForbiddenAccessException(
                "Only researchers, trial managers, or system administrators can assign users to trials.");
        }
    }

    private static bool IsManager(UserRole? role)
        => role is UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager;

    private async Task EnsureCanAccessTrialAsync(Guid trialId, CancellationToken ct)
    {
        if (IsManager(_currentUser.Role))
            return;

        if (_currentUser.UserId is not Guid userId
            || !await _trialRepository.IsAssignedAsync(trialId, userId, ct))
        {
            throw new ForbiddenAccessException(
                $"You do not have access to trial '{trialId}'.");
        }
    }

    // Existence is checked before access, so an unknown trial stays a 404 and its id is never
    // disclosed to a caller who may not read it.
    private async Task<Trial> LoadTrialForAccessAsync(Guid trialId, CancellationToken ct)
    {
        var trial = await _trialRepository.GetByIdAsync(trialId, ct);
        if (trial is null)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        await EnsureCanAccessTrialAsync(trial.Id, ct);
        return trial;
    }

    private async Task<ResolvedWorkbookExport> ResolveExportAsync(Guid trialId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trial = await _trialRepository.GetByIdAsync(trialId, ct);
        if (trial is null)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        await EnsureCanAccessTrialAsync(trial.Id, ct);

        ct.ThrowIfCancellationRequested();

        var export = await _workbookStore.ResolveExportAsync(
            trial.Id, trial.SourceWorkbookPath ?? string.Empty, ct);
        if (export is null)
            throw new FileNotFoundException($"Workbook for trial '{trialId}' was not found on disk.");

        return export;
    }
}
