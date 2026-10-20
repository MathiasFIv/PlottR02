using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

public sealed class TrialsControllerSheetTests
{
    private static readonly Guid TrialId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task GetSheet_PassesSinceThroughAndReturns200()
    {
        var fake = new FakeTrialService();
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        var result = await controller.GetSheet(TrialId, since: "abc12345-1f2e3d");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fake.SheetToReturn);
        fake.CalledWithTrialId.Should().Be(TrialId);
        fake.CalledWithSince.Should().Be("abc12345-1f2e3d");
    }

    [Fact]
    public async Task GetSheet_SinceOmitted_PassesNullThrough()
    {
        var fake = new FakeTrialService();
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        await controller.GetSheet(TrialId, since: null);

        fake.CalledWithSince.Should().BeNull();
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsKeyNotFound_Returns404WithMessage()
    {
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new KeyNotFoundException("Trial '33333333-3333-3333-3333-333333333333' was not found."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        var result = await controller.GetSheet(TrialId, since: null);

        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Trial '33333333-3333-3333-3333-333333333333' was not found.");
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsFileNotFound_Returns404WithMessage()
    {
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new FileNotFoundException($"Workbook for trial '{TrialId}' was not found on disk."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        var result = await controller.GetSheet(TrialId, since: null);

        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be($"Workbook for trial '{TrialId}' was not found on disk.");
    }

    [Fact]
    public async Task GetSheet_ServiceThrowsInvalidData_Returns400WithMessage()
    {
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new InvalidDataException(
                "Sheet 'Subsample Plot Data' was not found in workbook 'upload.xlsx'."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        var result = await controller.GetSheet(TrialId, since: null);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Sheet 'Subsample Plot Data' was not found in workbook 'upload.xlsx'.");
    }

    [Fact]
    public void GetSheet_HasSheetRouteAuthorizeAndSwaggerResponseTypes()
    {
        var method = typeof(TrialsController).GetMethod(nameof(TrialsController.GetSheet));
        method.Should().NotBeNull();

        var httpGet = method!.GetCustomAttribute<HttpGetAttribute>();
        httpGet.Should().NotBeNull();
        httpGet!.Template.Should().Be("{trialId:guid}/sheet");
        method!.GetParameters()[1].GetCustomAttribute<FromQueryAttribute>().Should().NotBeNull(
            "`since` must bind from the query string");

        typeof(TrialsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "the whole controller is behind the auth pipeline");

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

        public Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
