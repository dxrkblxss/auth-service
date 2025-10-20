using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Models;
using AuthService.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using AuthService.Repositories;
using AuthService.Data;
using Microsoft.EntityFrameworkCore;
using AuthService.Exceptions;

namespace AuthService.Services;

public interface ITokenService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
    string HashRefreshToken(string refreshToken);
    Task<(string accessToken, string refreshToken)> RefreshTokenAsync(string refreshToken, string correlationId);
}

public class TokenService : ITokenService
{
    private readonly JwtOptions _jwtOptions;
    private readonly RefreshTokenOptions _refreshTokenOptions;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly ILogger<AuthService> _logger;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly AppDbContext _dbContext;
    private readonly IUserRepository _users;

    public TokenService(IOptions<JwtOptions> jwtOptions, IOptions<RefreshTokenOptions> refreshTokenOptions, IOptions<RefreshTokenOptions> refreshOptions, ILogger<AuthService> logger, AppDbContext dbContext, IUserRepository users, IRefreshTokenRepository refreshTokens)
    {
        _jwtOptions = jwtOptions.Value;
        _refreshTokenOptions = refreshTokenOptions.Value;

        if (_jwtOptions.Key.Length < 32)
            throw new InvalidOperationException("JWT key must be at least 32 characters long for security reasons.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));

        _logger = logger;
        _dbContext = dbContext;
        _users = users;
        _refreshOptions = refreshOptions.Value;
        _refreshTokens = refreshTokens;
    }

    public string GenerateAccessToken(User user)
    {
        return CreateJwtToken(user);
    }

    public string GenerateRefreshToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(_refreshTokenOptions.TokenLengthBytes));
    }

    public string HashRefreshToken(string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    public async Task<(string accessToken, string refreshToken)> RefreshTokenAsync(string refreshToken, string correlationId)
    {
        _logger.LogInformation("Refresh token request received, CorrelationId: {CorrelationId}", correlationId);

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("Refresh failed: missing token, CorrelationId: {CorrelationId}", correlationId);
            throw new MissingRefreshTokenException();
        }

        var tokenHash = HashRefreshToken(refreshToken);

        using var tx = await _dbContext.Database.BeginTransactionAsync();

        var refreshTokenEntity = await _refreshTokens.GetValidByTokenHashAsync(tokenHash, forUpdate: true);

        if (refreshTokenEntity == null)
        {
            _logger.LogWarning("Refresh failed: invalid or expired token, CorrelationId: {CorrelationId}", correlationId);
            throw new InvalidRefreshTokenException();
        }

        if (refreshTokenEntity.RevokedAt.HasValue)
        {
            _logger.LogWarning("Replay detected: token already revoked, RefreshTokenId: {TokenId}, UserId: {UserId}, CorrelationId: {CorrelationId}",
                refreshTokenEntity.Id, refreshTokenEntity.UserId, correlationId);

            var familyId = refreshTokenEntity.FamilyId;
            await _refreshTokens.RevokeFamilyTokensAsync(familyId);
            throw new RefreshTokenReplayDetectedException();
        }

        var user = await _users.GetByIdAsync(refreshTokenEntity.UserId);

        if (user == null)
        {
            _logger.LogError("Inconsistent state: user not found for valid refresh token, UserId: {UserId}, CorrelationId: {CorrelationId}",
                refreshTokenEntity.UserId, correlationId);
            throw new InvalidRefreshTokenException();
        }

        var newAccessToken = GenerateAccessToken(user);
        var newRefreshToken = GenerateRefreshToken();

        var newRefreshTokenEntity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(newRefreshToken),
            FamilyId = refreshTokenEntity.FamilyId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_refreshOptions.DaysValid)
        };

        try
        {
            refreshTokenEntity.RevokedAt = DateTime.UtcNow;
            refreshTokenEntity.ReplacedBy = newRefreshTokenEntity.Id;
            await _refreshTokens.AddAsync(newRefreshTokenEntity);
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogWarning(dbEx, "Failed to create or update refresh tokens for user {UserId}, OldTokenId: {OldTokenId}, CorrelationId: {CorrelationId}",
                user.Id, refreshTokenEntity.Id, correlationId);

            throw new RefreshTokenCreationFailedException(dbEx);
        }

        await tx.CommitAsync();

        _logger.LogInformation("Issued new tokens for user {UserId}, NewRefreshTokenId: {TokenId}, CorrelationId: {CorrelationId}", user.Id, newRefreshTokenEntity.Id, correlationId);

        return (newAccessToken, newRefreshToken);
    }

    private string CreateJwtToken(User user)
    {
        var creds = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_jwtOptions.AccessTokenLifetimeMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
