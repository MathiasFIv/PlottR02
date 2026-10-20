using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Mappers;

public static class TrialMapper
{
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

    public static IReadOnlyList<TrialSummary> ToSummaries(
        IEnumerable<Trial> entities, IReadOnlySet<Guid>? assignedTrialIds = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities
            .Select(entity => ToSummary(entity, assignedTrialIds?.Contains(entity.Id) ?? false))
            .ToList();
    }
}
