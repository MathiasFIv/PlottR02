using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Mappers;

/// <summary>
/// Stateless static mapper between <see cref="Trial"/> and its DTOs.
/// No DI registration needed — call directly from services.
/// </summary>
public static class TrialMapper
{
    /// <summary>
    /// Maps a domain <see cref="Trial"/> to a <see cref="TrialSummary"/> DTO.
    /// </summary>
    /// <param name="entity">The trial to map.</param>
    /// <param name="isAssignedToCurrentUser">
    /// Whether the acting user is personally assigned to the trial; surfaced to the client
    /// as <see cref="TrialSummary.IsAssignedToCurrentUser"/>. Defaults to <c>false</c>.
    /// </param>
    /// <param name="createdByName">
    /// Explicit creator display name to surface as <see cref="TrialSummary.CreatedByName"/>.
    /// When <c>null</c>, falls back to the loaded <see cref="Trial.CreatedByUser"/> navigation's
    /// <see cref="User.Name"/> — itself null when the creator row was deleted (the FK is
    /// SetNull, so the navigation is absent). Pass it explicitly for a freshly created entity
    /// whose navigation is not loaded.
    /// </param>
    /// <remarks>
    /// Omitting <paramref name="isAssignedToCurrentUser"/> yields <c>false</c> for the row.
    /// That default is only correct for "unknown actor / no-assignments" call sites — pass
    /// the real flag whenever the acting user's assignment is known, otherwise the returned
    /// badge silently misreports the actor as unassigned. Omitting
    /// <paramref name="createdByName"/> falls back to the navigation; when that navigation is
    /// also absent the summary carries a null name, which the UI renders by omitting the
    /// "by" line.
    /// </remarks>
    public static TrialSummary ToSummary(
        Trial entity, bool isAssignedToCurrentUser = false, string? createdByName = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new TrialSummary
        {
            TrialId = entity.Id.ToString(),
            ExternalId = entity.ExternalId,
            FileName = entity.FileName,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            CreatedBy = entity.CreatedBy,
            CreatedByName = createdByName ?? entity.CreatedByUser?.Name,
            IsAssignedToCurrentUser = isAssignedToCurrentUser,
        };
    }

    /// <summary>
    /// Batch-maps a collection of domain entities to summary DTOs.
    /// </summary>
    /// <param name="entities">The trials to map.</param>
    /// <param name="assignedTrialIds">
    /// The acting user's assigned trial ids. When supplied, each summary's
    /// <see cref="TrialSummary.IsAssignedToCurrentUser"/> is computed as membership of the
    /// trial id in this set; when <c>null</c> every summary reports <c>false</c>.
    /// </param>
    /// <remarks>
    /// Omitting <paramref name="assignedTrialIds"/> yields <c>false</c> for EVERY row. That
    /// default is only correct for "unknown actor / no-assignments" call sites — pass the
    /// acting user's assigned ids whenever they are known, otherwise every returned badge
    /// silently misreports the actor as unassigned.
    /// </remarks>
    public static IReadOnlyList<TrialSummary> ToSummaries(
        IEnumerable<Trial> entities, IReadOnlySet<Guid>? assignedTrialIds = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities
            .Select(entity => ToSummary(entity, assignedTrialIds?.Contains(entity.Id) ?? false))
            .ToList();
    }
}
