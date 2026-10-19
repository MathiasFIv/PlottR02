using System.Reflection;
using PlottR0._2.Repository.Repositories;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for the internal <c>ObjectKeys</c> key-builder
/// (<c>PlottR0._2.Repository.Storage.ObjectKeys</c>).
/// <para>
/// The type is <c>internal</c> to the Repository assembly and the project declares no
/// <c>InternalsVisibleTo</c> for the test project, so it is reached through reflection.
/// This keeps production code (and its project files) untouched while still asserting
/// the exact key shapes. The store-level tests independently assert the same key
/// strings end-to-end through the public stores, so this coverage is never the only
/// guard against a mapping regression.
/// </para>
/// </summary>
public sealed class ObjectKeysTests
{
    private const BindingFlags StaticPublic = BindingFlags.Public | BindingFlags.Static;

    private static readonly Type ObjectKeysType =
        typeof(TrialWorkbookStore).Assembly.GetType(
            "PlottR0._2.Repository.Storage.ObjectKeys", throwOnError: true)!;

    private static readonly Guid TrialId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void WorkbookSourceKey_PlacesFileUnderWorkbooksNamespace()
    {
        InvokeString("WorkbookSourceKey", TrialId, "book.xlsx")
            .Should().Be($"Workbooks/{TrialId}/book.xlsx");
    }

    [Fact]
    public void WorkbookOutputKey_PlacesFileUnderWorkingNamespace()
    {
        InvokeString("WorkbookOutputKey", TrialId, "book_out_20260101_000000.xlsx")
            .Should().Be($"Working/{TrialId}/book_out_20260101_000000.xlsx");
    }

    [Fact]
    public void RecordingKey_UsesLowercaseRecordingsNamespace()
    {
        InvokeString("RecordingKey", TrialId, "rec-1.webm")
            .Should().Be($"recordings/{TrialId}/rec-1.webm");
    }

    [Fact]
    public void WorkbookOutputPrefix_EndsWithSlashAndMatchesOutputNamespace()
    {
        InvokeString("WorkbookOutputPrefix", TrialId).Should().Be($"Working/{TrialId}/");
    }

    [Theory]
    [InlineData("foo_out_20240101.xlsx", true)]
    [InlineData("foo_out_20240101_120000.xlsx", true)]
    [InlineData("foo_out_.xlsm", true)]
    [InlineData("foo.xlsx", false)]
    [InlineData("foo.xlsm", false)]
    [InlineData("output.xlsx", false)]
    public void IsOutputCopy_TrueOnlyWhenStemContainsMarker(string fileName, bool expected)
    {
        InvokeBool("IsOutputCopy", fileName).Should().Be(expected);
    }

    private static string InvokeString(string method, params object[] args)
        => (string)ObjectKeysType.GetMethod(method, StaticPublic)!.Invoke(null, args)!;

    private static bool InvokeBool(string method, params object[] args)
        => (bool)ObjectKeysType.GetMethod(method, StaticPublic)!.Invoke(null, args)!;
}
