using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Contracts.Dtos.Auth;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

public sealed class AuthServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private sealed class FakeUserService : IUserService
    {
        public Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<UserResponse?>(new UserResponse
            {
                Id = id,
                Name = "Alice",
                Email = "alice@example.com",
                Role = UserRole.Researcher,
            });

        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeTokenService : ITokenService
    {
        private int _mintCount;

        public string GenerateAccessToken(UserResponse user)
            => $"access-token-{++_mintCount}";

        public string GenerateRefreshToken()
            => $"plain-refresh-{++_mintCount}";

        public int RefreshTokenLifetimeDays => 7;
    }

    private sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly Dictionary<string, RefreshToken> _byHash = new();
        private readonly List<RefreshToken> _byId = new();

        public int FamilyRevocationCount { get; private set; }
        public List<Guid> RevokedIds { get; } = new();

        public void Seed(RefreshToken token)
        {
            _byHash[token.Token] = token;
            _byId.Add(token);
        }

        public RefreshToken? FindById(Guid id) => _byId.FirstOrDefault(t => t.Id == id);

        public Task AddAsync(RefreshToken token, CancellationToken ct = default)
        {
            _byHash[token.Token] = token;
            _byId.Add(token);
            return Task.CompletedTask;
        }

        public Task<RefreshToken?> GetByHashedTokenAsync(string hashedToken, CancellationToken ct = default)
            => Task.FromResult(_byHash.GetValueOrDefault(hashedToken));

        public Task<RefreshToken?> GetLatestActiveAsync(Guid userId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult(_byId
                .Where(t => t.UserId == userId && !t.IsRevoked && t.ExpiresAt > now)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefault());
        }

        public Task<int> RevokeAsync(Guid tokenId, CancellationToken ct = default)
        {
            var token = _byId.FirstOrDefault(t => t.Id == tokenId);
            if (token is null || token.IsRevoked)
            {
                return Task.FromResult(0);
            }

            token.IsRevoked = true;
            token.RevokedAt = DateTime.UtcNow;
            RevokedIds.Add(tokenId);
            return Task.FromResult(1);
        }

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            FamilyRevocationCount++;
            var now = DateTime.UtcNow;
            var revoked = _byId.Count(t => t.UserId == userId && !t.IsRevoked);
            foreach (var token in _byId.Where(t => t.UserId == userId && !t.IsRevoked))
            {
                token.IsRevoked = true;
                token.RevokedAt = now;
                RevokedIds.Add(token.Id);
            }

            return Task.FromResult(revoked);
        }
    }

    private static AuthService CreateService(FakeRefreshTokenRepository repo)
        => new(
            new FakeUserService(),
            new FakeTokenService(),
            repo,
            NullLogger<AuthService>.Instance);

    private static string Hash(string plain)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

    private static RefreshToken Token(string plain, DateTime? revokedAt = null, bool revoked = false)
        => new()
        {
            Id = Guid.NewGuid(),
            Token = Hash(plain),
            UserId = UserId,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = revoked,
            RevokedAt = revokedAt,
        };

    private static RefreshRequest Request(string plain) => new() { RefreshToken = plain };

    [Fact]
    public async Task RefreshAsync_ActiveToken_RotatesAndIssuesFreshPair()
    {
        var repo = new FakeRefreshTokenRepository();
        var service = CreateService(repo);
        const string plain = "rt-active";
        repo.Seed(Token(plain));

        var response = await service.RefreshAsync(Request(plain));

        response.AccessToken.Should().NotBeNullOrEmpty();
        response.RefreshToken.Should().NotBeNullOrEmpty();
        response.RefreshToken.Should().NotBe(plain);
        repo.RevokedIds.Should().ContainSingle();
        repo.FamilyRevocationCount.Should().Be(0);

        var successor = await repo.GetByHashedTokenAsync(Hash(response.RefreshToken));
        successor.Should().NotBeNull();
        successor!.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ReplayWithinGraceWindow_IsTreatedAsConcurrentRefreshAndPreservesFamily()
    {
        var repo = new FakeRefreshTokenRepository();
        var service = CreateService(repo);
        const string reused = "rt-reused";
        const string successorPlain = "rt-active-successor";

        var reusedEntity = Token(reused, revokedAt: DateTime.UtcNow.AddSeconds(-2), revoked: true);
        var successor = Token(successorPlain);
        repo.Seed(reusedEntity);
        repo.Seed(successor);

        var response = await service.RefreshAsync(Request(reused));

        response.AccessToken.Should().NotBeNullOrEmpty();
        response.RefreshToken.Should().NotBeNullOrEmpty();
        repo.FamilyRevocationCount.Should().Be(0);
        successor.IsRevoked.Should().BeTrue("the live successor is rotated to keep single-use semantics");
        repo.RevokedIds.Should().Contain(successor.Id);
        var minted = await repo.GetByHashedTokenAsync(Hash(response.RefreshToken));
        minted.Should().NotBeNull();
        minted!.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ReplayWithinGraceWindowWithoutSuccessor_RevokesFamilyAndDenies()
    {
        var repo = new FakeRefreshTokenRepository();
        var service = CreateService(repo);
        const string reused = "rt-reused-no-successor";
        repo.Seed(Token(reused, revokedAt: DateTime.UtcNow.AddSeconds(-2), revoked: true));

        var act = () => service.RefreshAsync(Request(reused));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        repo.FamilyRevocationCount.Should().Be(1, "a live successor is required for the grace path to apply");
    }

    [Fact]
    public async Task RefreshAsync_ReplayBeyondGraceWindow_RevokesFamilyAndDenies()
    {
        var repo = new FakeRefreshTokenRepository();
        var service = CreateService(repo);
        const string oldReused = "rt-old-replay";
        const string successorPlain = "rt-some-successor";

        repo.Seed(Token(oldReused, revokedAt: DateTime.UtcNow.AddMinutes(-10), revoked: true));
        repo.Seed(Token(successorPlain));

        var act = () => service.RefreshAsync(Request(oldReused));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        repo.FamilyRevocationCount.Should().Be(1, "replay beyond the grace window is treated as theft");
    }

    [Fact]
    public async Task RefreshAsync_UnknownToken_DeniesWithoutRevokingFamily()
    {
        var repo = new FakeRefreshTokenRepository();
        var service = CreateService(repo);

        var act = () => service.RefreshAsync(Request("rt-never-issued"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        repo.FamilyRevocationCount.Should().Be(0);
    }
}