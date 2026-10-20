using PlottR0._2.Repository.Storage;

namespace PlottR0._2.Tests;

public sealed class NullObjectStoreTests
{
    private static readonly NullObjectStore Store = NullObjectStore.Instance;

    [Fact]
    public void Instance_IsSharedSingleton()
    {
        NullObjectStore.Instance.Should().BeSameAs(NullObjectStore.Instance);
    }

    [Fact]
    public async Task PutAsync_CompletesWithoutThrowing()
    {
        using var content = new MemoryStream("payload"u8.ToArray());

        var act = () => Store.PutAsync("Workbooks/trial/book.xlsx", content, "application/octet-stream");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_CompletesWithoutThrowing()
    {
        var act = () => Store.DeleteAsync("Workbooks/trial/book.xlsx");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExistsAsync_AlwaysReturnsFalse()
    {
        (await Store.ExistsAsync("Workbooks/trial/book.xlsx")).Should().BeFalse();
    }

    [Fact]
    public async Task ListKeysAsync_AlwaysReturnsEmpty()
    {
        var keys = await Store.ListKeysAsync("Workbooks/");

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task GetToFileAsync_ThrowsFileNotFoundExceptionAndWritesNothing()
    {
        var destination = Path.Combine(
            Path.GetTempPath(), "PlottrNullObjectStore_" + Guid.NewGuid().ToString("N") + ".xlsx");

        var act = () => Store.GetToFileAsync("Workbooks/trial/book.xlsx", destination);

        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*Workbooks/trial/book.xlsx*");
        File.Exists(destination).Should().BeFalse("the null store must not create the destination file");
    }
}
