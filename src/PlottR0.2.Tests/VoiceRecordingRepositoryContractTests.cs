using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

[Collection(PostgresCollection.Name)]
public sealed class VoiceRecordingRepositoryContractTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _createdTrialIds = [];

    public VoiceRecordingRepositoryContractTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var trialId in _createdTrialIds)
        {
            try
            {
                var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
                var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
                if (Directory.Exists(workingDir))
                    Directory.Delete(workingDir, recursive: true);
            }
            catch
            {
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    private static VoiceRecording CreateRecording(Guid trialId, string? clientRecordingId = null)
    {
        var clientId = clientRecordingId ?? Guid.NewGuid().ToString();
        return new VoiceRecording
        {
            Id = Guid.NewGuid(),
            ClientRecordingId = clientId,
            TrialId = trialId,
            BlobPath = Path.Combine("Recordings", trialId.ToString(), $"{clientId}.webm"),
            MimeType = "audio/webm",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task<Guid> CreateTrialAsync()
    {
        var trialId = Guid.NewGuid();
        var fileName = "voice test.xlsx";

        using (var scope = _fixture.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ITrialWorkbookStore>();
            using var stream = TrialWorkbookFactory.CreateStream();
            var relativePath = await store.SaveAsync(trialId, fileName, stream);

            var trial = new Trial
            {
                Id = trialId,
                ExternalId = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                Status = TrialStatus.Created,
                SourceWorkbookPath = relativePath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            var trialRepo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            await trialRepo.CreateAsync(trial);
        }

        _createdTrialIds.Add(trialId);
        return trialId;
    }

    [Fact]
    public async Task CreateAsync_ValidRecording_PersistsWithExpectedValues()
    {
        var trialId = await CreateTrialAsync();
        var recording = CreateRecording(trialId);

        VoiceRecording created;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            created = await repo.CreateAsync(recording);
        }

        created.Id.Should().NotBe(Guid.Empty);
        created.ClientRecordingId.Should().Be(recording.ClientRecordingId);
        created.TrialId.Should().Be(trialId);
        created.BlobPath.Should().Be(recording.BlobPath);
        created.MimeType.Should().Be(recording.MimeType);
        created.Status.Should().Be(VoiceRecordingStatus.Received);
        created.ConfirmedPlot.Should().BeNull();
        created.Transcript.Should().BeNull();
        created.Confidence.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ExistingRecording_ReturnsPersistedRow()
    {
        var trialId = await CreateTrialAsync();
        var recording = CreateRecording(trialId);
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            await repo.CreateAsync(recording);
        }

        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByIdAsync(recording.Id);
        }

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(recording.Id);
        fetched.ClientRecordingId.Should().Be(recording.ClientRecordingId);
        fetched.TrialId.Should().Be(trialId);
        fetched.BlobPath.Should().Be(recording.BlobPath);
        fetched.MimeType.Should().Be(recording.MimeType);
        fetched.Status.Should().Be(VoiceRecordingStatus.Received);
    }

    [Fact]
    public async Task GetByIdAsync_MissingId_ReturnsNull()
    {
        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByIdAsync(Guid.NewGuid());
        }

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DuplicateClientRecordingId_ThrowsClientRecordingIdConflictException()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            await repo.CreateAsync(CreateRecording(trialId, clientId));
        }

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            var act = () => repo.CreateAsync(CreateRecording(trialId, clientId));
            var conflict = await act.Should().ThrowAsync<ClientRecordingIdConflictException>();
            conflict.And.InnerException.Should().BeOfType<DbUpdateException>(
                "the original persistence failure is preserved for diagnostics");
        }
    }

    [Fact]
    public async Task CreateAsync_UnclassifiedDbUpdateException_PropagatesUnchanged()
    {
        var recording = CreateRecording(Guid.NewGuid());

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            var act = () => repo.CreateAsync(recording);
            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task GetByClientRecordingIdAsync_Existing_ReturnsRow()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            await repo.CreateAsync(CreateRecording(trialId, clientId));
        }

        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByClientRecordingIdAsync(clientId);
        }

        fetched.Should().NotBeNull();
        fetched!.ClientRecordingId.Should().Be(clientId);
    }

    [Fact]
    public async Task GetByClientRecordingIdAsync_Missing_ReturnsNull()
    {
        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByClientRecordingIdAsync("absent-client-id");
        }

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_StatusAndParseFields_Persisted()
    {
        var trialId = await CreateTrialAsync();
        var recording = CreateRecording(trialId);
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            await repo.CreateAsync(recording);
        }

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            recording.Status = VoiceRecordingStatus.Written;
            recording.Transcript = "plot 3 subsample 2 value 7";
            recording.ParsedPlot = 3;
            recording.ParsedSubsample = 2;
            recording.ParsedValue = 7;
            recording.Confidence = 0.92;
            recording.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(recording);
        }

        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByIdAsync(recording.Id);
        }

        fetched.Should().NotBeNull();
        fetched!.Status.Should().Be(VoiceRecordingStatus.Written);
        fetched.Transcript.Should().Be("plot 3 subsample 2 value 7");
        fetched.ParsedPlot.Should().Be(3);
        fetched.ParsedSubsample.Should().Be(2);
        fetched.ParsedValue.Should().Be(7);
        fetched.Confidence.Should().Be(0.92);
    }

    [Fact]
    public async Task UpdateAsync_NullRecording_ThrowsArgumentNull()
    {
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            var act = () => repo.UpdateAsync(null!);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
    }

    [Fact]
    public async Task UpdateAsync_MissingId_ThrowsDbUpdateConcurrencyException()
    {
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            var ghost = CreateRecording(Guid.NewGuid());
            var act = () => repo.UpdateAsync(ghost);
            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }
    }

    [Fact]
    public async Task StatusColumn_WhenOmittedFromInsert_DefaultsToReceived()
    {
        var trialId = await CreateTrialAsync();
        var recording = CreateRecording(trialId);

        using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlottR0DbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"VoiceRecordings\" (\"Id\", \"ClientRecordingId\", \"TrialId\", \"BlobPath\", \"MimeType\") VALUES ({0}, {1}, {2}, {3}, {4})",
                new object[]
                {
                    recording.Id,
                    recording.ClientRecordingId,
                    recording.TrialId is null ? DBNull.Value : recording.TrialId.Value,
                    recording.BlobPath,
                    recording.MimeType,
                });
        }

        VoiceRecording? fetched;
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            fetched = await repo.GetByIdAsync(recording.Id);
        }

        fetched.Should().NotBeNull();
        fetched!.Status.Should().Be(VoiceRecordingStatus.Received);
    }
}