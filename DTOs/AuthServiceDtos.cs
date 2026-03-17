using System.ComponentModel.DataAnnotations;

namespace AuthService.DTOs;

public record SignupRequest(
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    string Email, 
    
    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters")]
    string Password, 
    
    [Required(ErrorMessage = "Name is required")]
    string Name
);

public record LoginRequest(
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    string Email, 

    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters")]
    string Password
);

public record RefreshRequest(
    [Required(ErrorMessage = "Refresh token is required")]
    string RefreshToken
);

public record LogoutRequest(
    [Required(ErrorMessage = "Refresh token is required")]
    string RefreshToken
);
public record UserDto(Guid Id, string Email, string Role, string Name, string Username, DateTime? DateOfBirth, DateTime CreatedAt);
