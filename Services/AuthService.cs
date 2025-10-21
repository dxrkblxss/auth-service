using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Konscious.Security.Cryptography;
using System.Text;
using AuthService.Models;
using AuthService.Repositories;
using AuthService.Exceptions;
using AuthService.Options;
using AuthService.Data;
using Microsoft.Extensions.Options;

namespace AuthService.Services;

public interface IAuthService
{
    Task<User> SignUpAsync(string email, string password, string correlationId);
    Task<(string accessToken, string refreshToken)> LoginAsync(string email, string password, string correlationId);
}

public class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ITokenService _tokens;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly HashingOptions _hashingOptions;

    private readonly AppDbContext _dbContext;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUserRepository users, IRefreshTokenRepository refreshTokens, ITokenService tokens, IOptions<RefreshTokenOptions> refreshOptions, IOptions<HashingOptions> hashingOptions, AppDbContext dbContext, ILogger<AuthService> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _tokens = tokens;
        _refreshOptions = refreshOptions.Value;
        _hashingOptions = hashingOptions.Value;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<User> SignUpAsync(string email, string password, string correlationId)
    {
        _logger.LogInformation("Signup attempt for email {Email}, CorrelationId: {CorrelationId}", email, correlationId);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning("Signup failed: missing email or password, CorrelationId: {CorrelationId}", correlationId);
            throw new MissingFieldsException();
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            _logger.LogWarning("Signup failed: invalid email {Email}, CorrelationId: {CorrelationId}", email, correlationId);
            throw new InvalidEmailException();
        }

        if (await _users.ExistsAsync(email))
        {
            _logger.LogWarning("Signup failed: user exists {Email}, CorrelationId: {CorrelationId}", email, correlationId);
            throw new UserAlreadyExistsException();
        }

        string passwordHash;

        try
        {
            passwordHash = HashPassword(password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password hashing failed for email {Email}, CorrelationId: {CorrelationId}", email, correlationId);
            throw new PasswordHashingFailedException();
        }

        var user = new User
        {
            Email = email,
            PasswordHash = passwordHash,
            Role = "User",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _users.AddAsync(user);
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogWarning(dbEx, "Failed to create user {Email}, CorrelationId: {CorrelationId}", email, correlationId);
            throw new UserCreationFailedException();
        }

        _logger.LogInformation("User created successfully {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);

        return user;
    }

    public async Task<(string accessToken, string refreshToken)> LoginAsync(string email, string password, string correlationId)
    {
        _logger.LogInformation("Login attempt for email {Email}, CorrelationId: {CorrelationId}", email, correlationId);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning("Login failed: missing email or password, CorrelationId: {CorrelationId}", correlationId);
            throw new MissingFieldsException();
        }

        var user = await _users.GetByEmailAsync(email);
        if (user == null || !VerifyPassword(password, user.PasswordHash))
        {
            _logger.LogWarning("Login failed: invalid credentials for email {Email}, CorrelationId: {CorrelationId}", email, correlationId);
            throw new InvalidCredentialsException();
        }

        string accessToken;
        string refreshToken;

        try
        {
            accessToken = _tokens.GenerateAccessToken(user);
            refreshToken = _tokens.GenerateRefreshToken();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate tokens for user {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
            throw;
        }

        var refreshTokenEntity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokens.HashRefreshToken(refreshToken),
            FamilyId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_refreshOptions.DaysValid)
        };

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            await _refreshTokens.AddAsync(refreshTokenEntity);
            await _dbContext.SaveChangesAsync();

            await transaction.CommitAsync();
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogWarning(dbEx, "Failed to create refresh token for user {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
            await transaction.RollbackAsync();
            throw new RefreshTokenCreationFailedException(dbEx);
        }

        _logger.LogInformation("User {UserId} logged in, refresh token {TokenId} created, CorrelationId: {CorrelationId}", user.Id, refreshTokenEntity.Id, correlationId);

        return (accessToken, refreshToken);
    }

    private string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.DegreeOfParallelism = _hashingOptions.DegreeOfParallelism;
        argon2.Iterations = 3;
        argon2.MemorySize = 65536;
        argon2.Salt = salt;
        byte[] hash = argon2.GetBytes(32);
        return $"$argon2id$v=19$m=65536,t=3,p=4${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encodedFromDb)
    {
        try
        {
            var parts = encodedFromDb.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "argon2id")
                throw new FormatException("Invalid password hash format.");

            var paramsPart = parts[2];
            var salt = Convert.FromBase64String(parts[3]);
            var expectedHash = Convert.FromBase64String(parts[4]);

            int memory = 65536, iterations = 3, parallelism = 4;
            foreach (var kv in paramsPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (kv.StartsWith("m=")) memory = int.Parse(kv.Substring(2));
                if (kv.StartsWith("t=")) iterations = int.Parse(kv.Substring(2));
                if (kv.StartsWith("p=")) parallelism = int.Parse(kv.Substring(2));
            }

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
            argon2.Salt = salt;
            argon2.MemorySize = memory;
            argon2.Iterations = iterations;
            argon2.DegreeOfParallelism = parallelism;

            var actualHash = argon2.GetBytes(expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (Exception ex)
        {
            throw new PasswordVerificationFailedException("Password verification failed due to internal error.", ex);
        }
    }
}
