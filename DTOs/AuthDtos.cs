namespace AuthService.DTOs;

public record AuthRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
