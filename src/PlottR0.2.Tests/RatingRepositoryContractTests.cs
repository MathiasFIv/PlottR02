using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Repository.Repositories;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure contract tests for the workbook-backed rating repository's generic CRUD seam.
/// The store does not address Rating entities by identity (design.md §7, Rule 3 metadata
/// read-only), so the entity-style methods throw <see cref="NotSupportedException"/> BEFORE
/// any workbook/DB I/O — these tests therefore need no workbook file, no temp dirs, and no
/// Postgres container (plain xUnit class, deliberately outside the Postgres collection).
/// </summary>
public sealed class RatingRepositoryContractTests
{
    private static ClosedXmlRatingRepository CreateRepository() =>
        new(NullLogger<ClosedXmlRatingRepository>.Instance);

    [Fact]
    public async Task GetByIdAsync_Always_ThrowsNotSupported()
    {
        var repo = CreateRepository();

        var act = () => repo.GetByIdAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task GetAllAsync_Always_ThrowsNotSupported()
    {
        var repo = CreateRepository();

        var act = () => repo.GetAllAsync();
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ExistsAsync_Always_ThrowsNotSupported()
    {
        var repo = CreateRepository();

        var act = () => repo.ExistsAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task CountAsync_Always_ThrowsNotSupported()
    {
        var repo = CreateRepository();

        var act = () => repo.CountAsync();
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}