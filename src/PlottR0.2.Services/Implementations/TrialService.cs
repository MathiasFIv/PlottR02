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

/// <summary>
/// Concrete <see cref="ITrialService"/> that orchestrates trial creation (workbook
/// upload + DB persistence + audit), trial listing, trial-to-user assignment,
/// workbook export and the read-only sheet endpoint. File I/O is delegated to
/// <see cref="ITrialWorkbookStore"/> and <see cref="IRatingRepository"/>; persistence
/// to <see cref="ITrialRepository"/> and <see cref="IUserRepository"/>.
/// </summary>
public class TrialService : ITrialService
{
    private readonly ITrialRepository _trialRepository;
    private readonly ITrialWorkbookStore _workbookStore;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TrialService> _logger;
    private readonly IRatingRepository _ratingRepository;
    private readonly IUserRepository _userRepository;

    public TrialService(
        ITrialRepository trialRepository,
        ITrialWorkbookStore workbookStore,
        IAuditService auditService,
        ICurrentUser currentUser,
        ILogger<TrialService> logger,
        IRatingRepository ratingRepository,
        IUserRepository userRepository)
    {
        _trialRepository = trialRepository;
        _workbookStore = workbookStore;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
        _ratingRepository = ratingRepository;
        _userRepository = userRepository;
    }

    /// <inheritdoc />
    public async Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        // Role gate — BEFORE any file or DB write. Only researchers, trial managers
        // and system administrators may create trials; a null role (legacy/anonymous
        // actor) is denied identically.
        if (_currentUser.Role is not (UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager))
        {
            throw new ForbiddenAccessException(
                "Only researchers, trial managers, or system administrators can create trials.");
        }

        // Identity gate — also BEFORE any file or DB write. A privileged role with no user
        // id (e.g. a stale/malformed token) cannot be attributed: persisting Guid.Empty
        // would poison CreatedBy and any creator assignment, so fail fast instead.
        if (_currentUser.UserId is not Guid creatorId)
        {
            throw new ForbiddenAccessException(
                "Your account has no user identifier; re-authenticate.");
        }

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
            CreatedBy = creatorId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Computed once and reused for both the persisted auto-assignment and the response
        // badge: a researcher or trial manager creator IS assigned to their own trial; a
        // system administrator is not.
        var creatorIsAssigned =
            _currentUser.Role is UserRole.Researcher or UserRole.TrialManager;

        // A researcher or trial manager is implicitly assigned to their own trial; the row
        // rides along inside CreateAsync's single SaveChangesAsync — no second round-trip.
        // A system administrator is "above the matrix" (they bypass every per-trial gate via
        // IsManager), so the row is pointless and is deliberately omitted. Omitting it also
        // closes a dead-end: an admin-authored trial used to carry an admin assignment that
        // the crew UI round-tripped back through AssignTrialUsersAsync, which rejects
        // SystemAdmin targets → an inescapable 400.
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

            // Mirror the auto-assignment above in the response: a researcher/trial-manager
            // creator is assigned to the new trial (badge on), a system-administrator
            // creator is above the matrix and carries no assignment (badge off). The freshly
            // created entity's CreatedByUser navigation is not loaded, so the actor's name is
            // passed explicitly rather than resolved from that (absent) navigation.
            return TrialMapper.ToSummary(created, creatorIsAssigned, _currentUser.Name);
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
        // Managers (system administrators, researchers, trial managers) see every trial.
        if (IsManager(_currentUser.Role))
        {
            var all = await _trialRepository.GetAllAsync(ct: ct);

            // Managers see every trial regardless of assignment, but the per-row badge must
            // reflect their OWN assignments: load the actor's assigned trial ids and let the
            // mapper compute membership per row. A missing identity (legacy/malformed token)
            // means "assigned to nothing", so every row stays false rather than failing the read.
            //
            // SystemAdmin is deliberately skipped: the assignment write path rejects
            // SystemAdmin targets outright (AssignTrialUsersAsync) and trial creation never
            // writes an admin assignment row, so an admin can never hold one — the query
            // would always come back empty. Leaving the set null yields all-false for free.
            IReadOnlySet<Guid>? assignedTrialIds = null;
            if (_currentUser.Role is not UserRole.SystemAdmin && _currentUser.UserId is Guid managerId)
                assignedTrialIds = (await _trialRepository.GetAssignedTrialIdsAsync(managerId, ct)).ToHashSet();

            return TrialMapper.ToSummaries(all, assignedTrialIds);
        }

        // Everyone else (a field worker, or a legacy/role-less actor) sees ONLY the trials
        // they are assigned to. A missing identity is treated as "nothing assigned".
        if (_currentUser.UserId is not Guid userId)
            return [];

        var assignedIds = await _trialRepository.GetAssignedTrialIdsAsync(userId, ct);
        if (assignedIds.Count == 0)
            return [];

        // The repository exposes no "get many by id" call; trials are a small field set, so
        // filter the full list in memory while preserving its CreatedAt-descending ordering.
        // Every row this branch returns is one the actor is assigned to, so pass the same set
        // as the badge source (the flag is true for all of them, by construction).
        var assignedSet = assignedIds.ToHashSet();
        var trials = await _trialRepository.GetAllAsync(ct: ct);
        // The Where filter already guarantees membership, so the mapper's per-row membership
        // re-check can only re-affirm true — it never filters a row back out.
        return TrialMapper.ToSummaries(trials.Where(t => assignedSet.Contains(t.Id)), assignedSet);
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

    /// <inheritdoc />
    public async Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Actor gate first — before any lookup or write.
        EnsureCanAssignUsers();

        if (!await _trialRepository.ExistsAsync(trialId, ct))
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        var requestedIds = request.UserIds;

        // Duplicate ids are a client error, not a silent de-dupe.
        if (requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("UserIds must be distinct.");

        // Validate every target BEFORE persisting (all-or-nothing). Lookups are
        // performed in a fixed order; the checks themselves are order-independent.
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

        // Replace semantics: the creator's auto-assignment is part of the replaced set,
        // so it survives only if the caller keeps them in the requested ids.
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default)
    {
        EnsureCanAssignUsers();

        if (!await _trialRepository.ExistsAsync(trialId, ct))
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        var assignments = await _trialRepository.GetAssignmentsAsync(trialId, ct);

        var users = new List<UserResponse>(assignments.Count);
        foreach (var assignment in assignments)
        {
            // Defensive: skip an assignment whose user has since disappeared rather
            // than failing the whole read.
            var user = await _userRepository.GetByIdAsync(assignment.UserId, ct);
            if (user is not null)
                users.Add(UserMapper.ToResponse(user));
        }

        return users;
    }

    /// <summary>
    /// Shared actor gate for both assignment operations: only system administrators,
    /// researchers and trial managers may view or change a trial's crew. A null role
    /// (legacy/anonymous actor) is denied identically.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The caller's role may not manage assignments.</exception>
    private void EnsureCanAssignUsers()
    {
        if (_currentUser.Role is not (UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager))
        {
            throw new ForbiddenAccessException(
                "Only researchers, trial managers, or system administrators can assign users to trials.");
        }
    }

    /// <summary>
    /// True when the actor's role sits above the per-trial assignment matrix: a manager
    /// sees and may access every trial without an explicit assignment. A null role
    /// (legacy/anonymous actor) is never a manager.
    /// </summary>
    private static bool IsManager(UserRole? role)
        => role is UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager;

    /// <summary>
    /// Ensures the current actor may access <paramref name="trialId"/>: managers always may;
    /// every other actor must be explicitly assigned to the trial. A missing identity or a
    /// trial the caller is not assigned to is denied identically.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The actor is not permitted to access the trial.</exception>
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

    /// <summary>
    /// Loads the trial and resolves its current workbook export (path + file name + opaque
    /// version) — the ONE place path resolution happens, shared by
    /// <see cref="GetWorkbookExportAsync"/> and <see cref="GetSheetAsync"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The trial does not exist.</exception>
    /// <exception cref="ForbiddenAccessException">The actor is not permitted to access the trial.</exception>
    /// <exception cref="FileNotFoundException">No workbook file is left on disk for the trial.</exception>
    private async Task<ResolvedWorkbookExport> ResolveExportAsync(Guid trialId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trial = await _trialRepository.GetByIdAsync(trialId, ct);
        if (trial is null)
            throw new KeyNotFoundException($"Trial '{trialId}' was not found.");

        // Access gate — an existing trial still requires the caller to be a manager or to be
        // explicitly assigned to it. The existence check above deliberately runs first so an
        // unknown trial stays a 404 even for a field worker (no existence disclosure change).
        await EnsureCanAccessTrialAsync(trial.Id, ct);

        ct.ThrowIfCancellationRequested();

        var export = await _workbookStore.ResolveExportAsync(
            trial.Id, trial.SourceWorkbookPath ?? string.Empty, ct);
        if (export is null)
            throw new FileNotFoundException($"Workbook for trial '{trialId}' was not found on disk.");

        return export;
    }
}
