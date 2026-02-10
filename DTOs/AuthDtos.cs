namespace AuthService.DTOs;

public record AuthRequest(string Email, string Password, string Name);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);
public record UserDto(Guid Id, string Email, string Role, string Name, string Username, DateTime? DateOfBirth, DateTime CreatedAt);
