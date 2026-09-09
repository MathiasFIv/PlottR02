using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface ITrialRepository : IRepository<Trial, Guid>
{
    Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default);
}
