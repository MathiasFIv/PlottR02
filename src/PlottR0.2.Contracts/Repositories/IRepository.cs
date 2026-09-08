namespace PlottR0._2.Contracts.Repositories;

/// <summary>
/// Generic read-only repository contract.
/// The workbook store can only ever legally write rating cells,
/// so generic mutation would advertise operations it cannot honour.
/// </summary>
public interface IRepository<TEntity, in TKey> where TEntity : class
{
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> GetAllAsync(int? skip = null, int? take = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(TKey id, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}