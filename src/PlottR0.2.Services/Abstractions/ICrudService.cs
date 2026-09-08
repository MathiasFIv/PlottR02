namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Generic structural read/list CRUD contract.
/// Business rules do NOT ride the generic — they live in specific services
/// (design.md §10): a generic <c>Add</c>/<c>Update</c>/<c>Delete</c> is deliberately
/// absent so Services can never rewrite structural shape outside a domain operation.
/// </summary>
/// <typeparam name="TEntity">The domain entity type served by the contract.</typeparam>
/// <typeparam name="TKey">The type of the entity's identity key.</typeparam>
public interface ICrudService<TEntity, in TKey> where TEntity : class
{
    /// <summary>
    /// Gets a single entity by its identity key.
    /// </summary>
    /// <param name="id">The entity's identity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching entity, or <see langword="null"/> if none exists.</returns>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default);

    /// <summary>
    /// Gets all entities.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An immutable list of all entities.</returns>
    Task<IReadOnlyList<TEntity>> GetAllAsync(CancellationToken ct = default);
}
