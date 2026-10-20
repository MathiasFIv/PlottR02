using PlottR0._2.Contracts.Dtos.User;

namespace PlottR0._2.Services.Abstractions;

public interface ITokenService
{
    string GenerateAccessToken(UserResponse user);

    string GenerateRefreshToken();

    int RefreshTokenLifetimeDays { get; }
}