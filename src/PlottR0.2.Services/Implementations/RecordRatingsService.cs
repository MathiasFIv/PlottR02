using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IRecordRatingsService"/> backed by <see cref="IRatingRepository"/>.
/// Enforces the domain rules (value 0..10, trait ordinal 1..9, plot existence, duplicate-rejected
/// subsamples, one-trait-per-command) and records the audit trail. It talks only to the
/// repository contract — never to ClosedXML or any concrete persistence class — so the
/// Excel/DB truth decision stays an implementation detail (design.md §7).
/// The inherited <see cref="ICrudService{Rating,Guid}"/> entity-by-id/member reads are
/// delegated to the workbook-backed repository, which legally throws
/// <see cref="NotSupportedException"/>: a pure cell store cannot address Rating entities
/// by identity (design.md §7, Rule 2 empty ≠ 0, Rule 3 metadata read-only).
/// </summary>
public class RecordRatingsService : IRecordRatingsService
{
    private readonly IRatingRepository _ratingRepository;
    private readonly ILogger<RecordRatingsService> _logger;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;

    public const int MinRating = 0;
    public const int MaxRating = 10;
    public const int MinTraitOrdinal = 1;
    public const int MaxTraitOrdinal = 9;

    /// <summary>
    /// Creates a new <see cref="RecordRatingsService"/>.
    /// </summary>
    /// <param name="ratingRepository">The rating repository contract to write cells against.</param>
    /// <param name="logger">Logger for scoring diagnostics.</param>
    /// <param name="auditService">The audit service contract used to record the audit trail.</param>
    /// <param name="currentUser">The current user identity attributed to audit entries.</param>
    public RecordRatingsService(
        IRatingRepository ratingRepository,
        ILogger<RecordRatingsService> logger,
        IAuditService auditService,
        ICurrentUser currentUser)
    {
        _ratingRepository = ratingRepository;
        _logger = logger;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Entity-by-id reads are not supported by the workbook store: Rating identities are
    /// structural metadata only, and the backend never resolves a rating by <c>Guid</c>
    /// (design.md §7, Rule 3 metadata read-only). The underlying repository throws
    /// <see cref="NotSupportedException"/> and that is intentional — use the rating
    /// operations (<see cref="ReadPlotColumnAsync"/>) instead.
    /// </remarks>
    public Task<Rating?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _ratingRepository.GetByIdAsync(id, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Listing all Rating entities is not supported by the workbook store: ratings only
    /// exist as cells within a plot/trait column, never as a standalone entity set
    /// (design.md §7, Rule 2 empty ≠ 0). The underlying repository throws
    /// <see cref="NotSupportedException"/> and that is intentional — use the rating
    /// operations (<see cref="ReadPlotColumnAsync"/>) instead.
    /// </remarks>
    public Task<IReadOnlyList<Rating>> GetAllAsync(CancellationToken ct = default)
        => _ratingRepository.GetAllAsync(null, null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RatingCell>> ReadPlotColumnAsync(int plot, int traitOrdinal, CancellationToken ct = default)
    {
        ValidateTraitOrdinal(traitOrdinal);
        var cells = _ratingRepository.ReadPlotColumn(plot, traitOrdinal);
        return Task.FromResult(cells);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="command"/> or <c>command.SubsampleIndices</c> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <c>command.SubsampleIndices</c> contains duplicate indices.
    /// Concurrent/duplicate subsample indices in one request are rejected — set semantics
    /// (design.md §6).
    /// </exception>
    public async Task<SubsampleRatingResult> RecordSubsampleRatingsAsync(
        RecordSubsampleRatingsCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.SubsampleIndices, nameof(command.SubsampleIndices));

        ValidateTraitOrdinal(command.TraitOrdinal);
        ValidateValue(command.Value);

        // Set semantics: duplicate subsample indices are rejected, not silently deduped
        // (design.md §6 — validation layer enforces set semantics).
        var duplicateIndices = command.SubsampleIndices
            .GroupBy(i => i)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateIndices.Count > 0)
        {
            throw new ArgumentException(
                $"Duplicate subsample indices detected: [{string.Join(", ", duplicateIndices)}]. " +
                "Each subsample index must appear at most once per command (set semantics).",
                nameof(command.SubsampleIndices));
        }

        var subsamples = command.SubsampleIndices.OrderBy(i => i).ToHashSet();
        if (subsamples.Count == 0)
        {
            throw new ArgumentException(
                "At least one subsample index must be provided.", nameof(command));
        }

        var written = _ratingRepository.WritePlotColumn(
            command.Plot, command.TraitOrdinal, command.Value, subsamples);

        if (written.Count > 0)
        {
            _logger.LogInformation(
                "Scored plot {Plot}, trait {Trait} = {Value} on subsamples [{Subsamples}]; wrote {Count} cell(s): {Cells}",
                command.Plot, command.TraitOrdinal, command.Value,
                string.Join(",", subsamples), written.Count,
                string.Join(",", written.Select(c => c.CellAddress)));
        }
        else
        {
            _logger.LogWarning(
                "Scoring plot {Plot}, trait {Trait} addressed subsamples [{Subsamples}] but none matched; no cells written.",
                command.Plot, command.TraitOrdinal, string.Join(",", subsamples));
        }

        // Audit the mutation (log-and-continue: LogAsync never throws, so a failed
        // audit write cannot break the scoring response).
        await _auditService.LogAsync(
            "Rating.Recorded",
            "Rating",
            entityId: null,
            _currentUser.UserId,
            _currentUser.Name,
            JsonSerializer.Serialize(new
            {
                plot = command.Plot,
                traitOrdinal = command.TraitOrdinal,
                value = command.Value,
                subsampleIndices = command.SubsampleIndices.OrderBy(i => i).ToList(),
                cellsWritten = written.Count,
            }),
            ct);

        var result = new SubsampleRatingResult
        {
            Plot = command.Plot,
            TraitOrdinal = command.TraitOrdinal,
            Value = command.Value,
            WrittenCells = written
                .Select(c => new WrittenCellDto
                {
                    Subsample = c.Subsample,
                    CellAddress = c.CellAddress,
                    Value = c.Value ?? command.Value,
                })
                .ToList(),
            // A write is fully preserved only when every requested subsample existed and was
            // written; any unmatched request index makes EmptyPreserved false — honest
            // reporting per the DTO contract (always true unless an indexed subsample does not
            // exist). The untouched set above reports exactly which indices went unmatched.
            UntouchedSubsamples = subsamples.Except(written.Select(c => c.Subsample)).ToList(),
            EmptyPreserved = written.Count == subsamples.Count,
        };

        return result;
    }

    /// <summary>
    /// Ensures the trait ordinal is a valid 1..9 column index. A command must never span
    /// traits/columns (design.md §6).
    /// </summary>
    private static void ValidateTraitOrdinal(int traitOrdinal)
    {
        if (traitOrdinal is < MinTraitOrdinal or > MaxTraitOrdinal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traitOrdinal), traitOrdinal,
                $"TraitOrdinal must be between {MinTraitOrdinal} and {MaxTraitOrdinal} (one column per command).");
        }
    }

    /// <summary>
    /// Ensures the value is a valid ordinal rating 0..10 (design.md §9).
    /// </summary>
    private static void ValidateValue(int value)
    {
        if (value is < MinRating or > MaxRating)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"Value must be between {MinRating} and {MaxRating}.");
        }
    }
}
