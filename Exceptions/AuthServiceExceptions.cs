namespace AuthService.Exceptions;

public class AuthServiceException : Exception
{
    public AuthServiceException() { }
    public AuthServiceException(string message) : base(message) { }
    public AuthServiceException(string message, Exception inner) : base(message, inner) { }
}

public class UserAlreadyExistsException : AuthServiceException
{
    public UserAlreadyExistsException() : base("User with this email already exists.") { }
}

public class InvalidEmailException : AuthServiceException
{
    public InvalidEmailException() : base("Invalid email address.") { }
}

public class MissingFieldsException : AuthServiceException
{
    public MissingFieldsException() : base("Email and password are required.") { }
}

public class MissingRefreshTokenException : AuthServiceException
{
    public MissingRefreshTokenException() : base("Refresh token is required.") { }
}

public class UserCreationFailedException : AuthServiceException
{
    public UserCreationFailedException() : base("Failed to create user.") { }
    public UserCreationFailedException(Exception inner) : base("Failed to create user.", inner) { }
}

public class InvalidCredentialsException : AuthServiceException
{
    public InvalidCredentialsException() : base("Invalid email or password.") { }
}

public class InvalidRefreshTokenException : AuthServiceException
{
    public InvalidRefreshTokenException() : base("Invalid refresh token.") { }
}

public class RefreshTokenCreationFailedException : AuthServiceException
{
    public RefreshTokenCreationFailedException() : base("Failed to create refresh token.") { }
    public RefreshTokenCreationFailedException(Exception inner) : base("Failed to create refresh token.", inner) { }
}

public class RefreshTokenReplayDetectedException : AuthServiceException
{
    public RefreshTokenReplayDetectedException() : base("Refresh token replay detected.") {}
}
