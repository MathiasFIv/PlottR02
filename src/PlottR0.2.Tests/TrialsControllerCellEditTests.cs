using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for <c>TrialsController.EditCell</c> — the
/// <c>POST /api/Trials/{trialId}/cells</c> action. As with the sheet tests, the controller is
/// invoked directly with a hand-rolled fake <see cref="ITrialService"/> (no
/// WebApplicationFactory); the route/authorization/Swagger contract is asserted via reflection.
/// A filled-cell refusal is the one skipped outcome that maps to 409; every other skip stays a
/// 200 so the client can distinguish "confirm to overwrite" from "no such row".
/// </summary>
public sealed class TrialsControllerCellEditTests
{
    private static readonly Guid TrialId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static EditCellRequest Request() => new()
    {
        Plot = 101,
        Subsample = 1,
        TraitOrdinal = 1,
        Value = 5,
    };

    private static CellEditResult Result(bool written, bool skipped, string? reason) => new(
        TrialId.ToString(), 101, 1, 1, 5, written, skipped, reason,
        PriorValue: null, CellAddress: "C22", Version: "abcd1234-0", FileName: "f_out_1.xlsx",
        GeneratedAt: DateTime.UtcNow);

    [Fact]
    public async Task EditCell_Written_Returns200WithPayload()
    {
        // Arrange
        var fake = new FakeTrialService { ResultToReturn = Result(written: true, skipped: false, reason: null) };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert — 200 with the payload; args forwarded
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fake.ResultToReturn);
        fake.CalledWithTrialId.Should().Be(TrialId);
        fake.CalledWithRequest.Should().NotBeNull();
        fake.CalledWithRequest!.Plot.Should().Be(101);
    }

    [Fact]
    public async Task EditCell_CellAlreadyFilled_Returns409WithPayload()
    {
        // Arrange — a filled-cell refusal is not an error: 409 signals "confirm to overwrite".
        var fake = new FakeTrialService
        {
            ResultToReturn = Result(written: false, skipped: true, reason: SingleCellWriteResult.CellAlreadyFilled),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert — 409 with the body carrying the prior value for the UI
        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeSameAs(fake.ResultToReturn);
    }

    [Fact]
    public async Task EditCell_SkippedRowNotFound_Returns200WithPayload()
    {
        // Arrange — a missing row is NOT a conflict; it stays 200 so the caller can tell the
        // two skip outcomes apart by SkippedReason.
        var fake = new FakeTrialService
        {
            ResultToReturn = Result(written: false, skipped: true, reason: SingleCellWriteResult.RowNotFound),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fake.ResultToReturn);
    }

    [Fact]
    public async Task EditCell_ServiceThrowsKeyNotFound_Returns404WithMessage()
    {
        // Arrange — unknown trial id
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new KeyNotFoundException("Trial '33333333-3333-3333-3333-333333333333' was not found."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert
        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Trial '33333333-3333-3333-3333-333333333333' was not found.");
    }

    [Fact]
    public async Task EditCell_ServiceThrowsFileNotFound_Returns404WithMessage()
    {
        // Arrange — trial exists but its workbook is gone from disk
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new FileNotFoundException($"Workbook for trial '{TrialId}' was not found on disk."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert
        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be($"Workbook for trial '{TrialId}' was not found on disk.");
    }

    [Fact]
    public async Task EditCell_ServiceThrowsForbidden_Returns403()
    {
        // Arrange — the caller may not access the trial
        var fake = new FakeTrialService { ExceptionToThrow = new ForbiddenAccessException("no") };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert — Forbid() produces a 403 with no body
        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task EditCell_ServiceThrowsArgument_Returns400WithMessage()
    {
        // Arrange — invalid payload (out-of-range field)
        var fake = new FakeTrialService { ExceptionToThrow = new ArgumentException("Value must be present and within 0..10.") };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert
        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Value must be present and within 0..10.");
    }

    [Fact]
    public async Task EditCell_ServiceThrowsInvalidData_Returns400WithMessage()
    {
        // Arrange — repository defense-in-depth rejection
        var fake = new FakeTrialService
        {
            ExceptionToThrow = new InvalidDataException("traitOrdinal must be within 1..9 (one column per write), got 0."),
        };
        var controller = new TrialsController(fake, new FakeVoiceCellEditService());

        // Act
        var result = await controller.EditCell(TrialId, Request());

        // Assert
        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("traitOrdinal must be within 1..9 (one column per write), got 0.");
    }

    [Fact]
    public void EditCell_HasPostRouteConsumesAuthorizeAndSwaggerResponseTypes()
    {
        // Arrange — the action and its declaring controller
        var method = typeof(TrialsController).GetMethod(nameof(TrialsController.EditCell))!;
        method.Should().NotBeNull();

        // Assert — route template binds {trialId:guid} + /cells
        var httpPost = method.GetCustomAttribute<HttpPostAttribute>();
        httpPost.Should().NotBeNull();
        httpPost!.Template.Should().Be("{trialId:guid}/cells");

        // Assert — consumes JSON
        method.GetCustomAttribute<ConsumesAttribute>()!.ContentTypes.Should().Contain("application/json");

        // Assert — [Authorize] on the controller is the 401 gate
        typeof(TrialsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "the whole controller is behind the auth pipeline");

        // Assert — Swagger documents 200 (CellEditResult), 400, 403, 404 and 409 (CellEditResult)
        var codes = method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Select(a => a.StatusCode)
            .ToArray();
        codes.Should().BeEquivalentTo(new[]
        {
            StatusCodes.Status200OK,
            StatusCodes.Status400BadRequest,
            StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict,
        });

        var attributes = method.GetCustomAttributes<ProducesResponseTypeAttribute>().ToList();
        attributes.Single(a => a.StatusCode == StatusCodes.Status200OK).Type.Should().Be(typeof(CellEditResult));
        attributes.Single(a => a.StatusCode == StatusCodes.Status409Conflict).Type.Should().Be(typeof(CellEditResult));
    }

    /// <summary>
    /// Minimal <see cref="ITrialService"/> double: records the last <c>EditCellAsync</c> call's
    /// arguments and either returns <see cref="ResultToReturn"/> or throws <see cref="ExceptionToThrow"/>.
    /// </summary>
    private sealed class FakeTrialService : ITrialService
    {
        public CellEditResult ResultToReturn { get; set; } = new(
            TrialId.ToString(), 101, 1, 1, 5, true, false, null,
            PriorValue: null, CellAddress: "C22", Version: "abcd1234-0", FileName: "f_out_1.xlsx",
            GeneratedAt: DateTime.UtcNow);

        public Exception? ExceptionToThrow { get; set; }

        public Guid? CalledWithTrialId { get; private set; }

        public EditCellRequest? CalledWithRequest { get; private set; }

        public Task<CellEditResult> EditCellAsync(Guid trialId, EditCellRequest request, CancellationToken ct = default)
        {
            CalledWithTrialId = trialId;
            CalledWithRequest = request;
            return ExceptionToThrow is not null
                ? Task.FromException<CellEditResult>(ExceptionToThrow)
                : Task.FromResult(ResultToReturn);
        }

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

        public Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
