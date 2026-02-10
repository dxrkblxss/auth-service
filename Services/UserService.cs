using AuthService.DTOs;
using AuthService.Repositories;

namespace AuthService.Services;

public interface IUserService
{
    Task<UserDto?> GetCurrentUserByIdAsync(Guid userId, string correlationId);
}

public class UserService : IUserService
{
    private readonly IUserRepository _users;
    private readonly ILogger<AuthService> _logger;

    public UserService(IUserRepository users, ILogger<AuthService> logger)
    {
        _users = users;
        _logger = logger;
    }

    public async Task<UserDto?> GetCurrentUserByIdAsync(Guid userId, string correlationId)
    {
        var user = await _users.GetByIdAsync(userId);

        if (user == null)
        {
            _logger.LogWarning(
                "User not found for UserId: {UserId}, CorrelationId: {CorrelationId}",
                userId, correlationId
            );
            return null;
        }

        _logger.LogInformation(
            "User info retrieved for UserId: {UserId}, CorrelationId: {CorrelationId}",
            userId, correlationId
        );

        return new UserDto(
            user.Id,
            user.Email,
            user.Role,
            user.Name,
            user.Username,
            user.DateOfBirth,
            user.CreatedAt
        );
    }
}
