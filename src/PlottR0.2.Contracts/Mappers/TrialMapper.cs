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
    public static TrialSummary ToSummary(Trial entity)
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
        };
    }

    /// <summary>
    /// Batch-maps a collection of domain entities to summary DTOs.
    /// </summary>
    public static IReadOnlyList<TrialSummary> ToSummaries(IEnumerable<Trial> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities.Select(ToSummary).ToList();
    }
}
