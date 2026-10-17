using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for <c>TrialsController.GetSheet</c> — the read-only
/// <c>GET /api/Trials/{trialId}/sheet?since=</c> action. The controller is invoked
/// directly with a hand-rolled fake <see cref="ITrialService"/> (plan A14 forbids
/// WebApplicationFactory, so pipeline-level 401/JSON behaviour is out of scope here;
/// authorization itself is asserted via the metadata test below — deviation noted
/// in the session report).
/// </summary>
public sealed class TrialsControllerSheetTests
{
    private static readonly Guid TrialId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task GetSheet_PassesSinceThroughAndReturns200()
    {
        // Arrange — a fake that records what the controller handed it.
        var fake = new FakeTrialService();
        var controller = new TrialsController(fake);

        // Act
        var result = await controller.GetSheet(TrialId, since: "abc12345-1f2e3d");

        // Assert — 200 with the payload, `since` forwarded verbatim (string, never parsed)
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fake.SheetToReturn);
        fake.CalledWithTrialId.Should().Be(TrialId);
        fake.CalledWithSince.Should().Be("abc12345-1f2e3d");
    }

    [Fact]
    public async Task GetSheet_SinceOmitted_PassesNullThrough()
    {
        // Arrange — `since` is optional in the query string; null means "full payload".
        var fake = new FakeTrialService();
        var controller = new TrialsController(fake);

        // Act
        await controller.GetSheet(TrialId, since: null);

        // Assert — null flows through untouched (no defaulting inside the controller)
        fake.CalledWithSince.Should().BeNull();
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsKeyNotFound_Returns404WithMessage()
    {
        // Arrange — unknown trial id
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new KeyNotFoundException("Trial '33333333-3333-3333-3333-333333333333' was not found."),
        };
        var controller = new TrialsController(fake);

        // Act
        var result = await controller.GetSheet(TrialId, since: null);

        // Assert — 404 carries the message body
        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Trial '33333333-3333-3333-3333-333333333333' was not found.");
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsFileNotFound_Returns404WithMessage()
    {
        // Arrange — trial exists but its workbook is gone from disk
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new FileNotFoundException($"Workbook for trial '{TrialId}' was not found on disk."),
        };
        var controller = new TrialsController(fake);

        // Act
        var result = await controller.GetSheet(TrialId, since: null);

        // Assert — 404 carries the message body
        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be($"Workbook for trial '{TrialId}' was not found on disk.");
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsInvalidData_Returns400WithMessage()
    {
        // Arrange — workbook lacks the "Subsample Plot Data" sheet (non-ARM upload)
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new InvalidDataException(
                "Sheet 'Subsample Plot Data' was not found in workbook 'upload.xlsx'."),
        };
        var controller = new TrialsController(fake);

        // Act
        var result = await controller.GetSheet(TrialId, since: null);

        // Assert — 400 carries the frozen body
        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Sheet 'Subsample Plot Data' was not found in workbook 'upload.xlsx'.");
    }

    [Fact]
    public void GetSheet_HasSheetRouteAuthorizeAndSwaggerResponseTypes()
    {
        // Arrange — the action and its declaring controller
        var method = typeof(TrialsController).GetMethod(nameof(TrialsController.GetSheet));
        method.Should().NotBeNull();

        // Assert — route template binds {trialId:guid} + /sheet and reads since from the query
        var httpGet = method!.GetCustomAttribute<HttpGetAttribute>();
        httpGet.Should().NotBeNull();
        httpGet!.Template.Should().Be("{trialId:guid}/sheet");
        method!.GetParameters()[1].GetCustomAttribute<FromQueryAttribute>().Should().NotBeNull(
            "`since` must bind from the query string");

        // Assert — [Authorize] on the controller is the 401 gate (plan A14: no pipeline test,
        // so the metadata is the observable contract — deviation documented in the report).
        typeof(TrialsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "the whole controller is behind the auth pipeline");

        // Assert — Swagger documents 200 (with TrialSheet), 400, 403 (not assigned) and 404
        var codes = method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Select(a => a.StatusCode)
            .ToArray();
        codes.Should().BeEquivalentTo(new[]
        {
            StatusCodes.Status200OK,
            StatusCodes.Status400BadRequest,
            StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound,
        });
        method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(a => a.StatusCode == StatusCodes.Status200OK)
            .Type.Should().Be(typeof(TrialSheet));
    }

    /// <summary>
    /// Minimal <see cref="ITrialService"/> double: records the last call's arguments and
    /// either returns <see cref="SheetToReturn"/> or throws <see cref="ExceptionToThrow"/>.
    /// </summary>
    private sealed class FakeTrialService : ITrialService
    {
        public TrialSheet SheetToReturn { get; set; } = new(
            TrialId.ToString(), "abcd1234-0", Changed: true,
            FileName: "f.xlsx", GeneratedAt: DateTime.UtcNow,
            Columns: [], Rows: [], Metadata: null);

        public Exception? ExceptionToThrow { get; set; }

        public Guid? CalledWithTrialId { get; private set; }

        public string? CalledWithSince { get; private set; }

        public Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default)
        {
            CalledWithTrialId = trialId;
            CalledWithSince = since;
            return ExceptionToThrow is not null
                ? Task.FromException<TrialSheet>(ExceptionToThrow)
                : Task.FromResult(SheetToReturn);
        }

        public Task<CellEditResult> EditCellAsync(Guid trialId, EditCellRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
