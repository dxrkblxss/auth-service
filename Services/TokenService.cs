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
    private readonly SymmetricSecurityKey _signingKey;
    private readonly ILogger<AuthService> _logger;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly AppDbContext _dbContext;
    private readonly IUserRepository _users;

    public TokenService(IOptions<JwtOptions> jwtOptions, IOptions<RefreshTokenOptions> refreshOptions, ILogger<AuthService> logger, AppDbContext dbContext, IUserRepository users, IRefreshTokenRepository refreshTokens)
    {
        _jwtOptions = jwtOptions.Value;

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
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(_refreshOptions.TokenLengthBytes));
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

        var strategy = _dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var tx = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var refreshTokenEntity = await _refreshTokens.GetValidByTokenHashAsync(tokenHash, forUpdate: true);

                if (refreshTokenEntity == null)
                {
                    _logger.LogWarning("Refresh failed: invalid or expired token, CorrelationId: {CorrelationId}", correlationId);
                    throw new InvalidRefreshTokenException();
                }

                if (refreshTokenEntity.RevokedAt.HasValue)
                {
                    _logger.LogWarning("Replay detected: token already revoked, CorrelationId: {CorrelationId}", correlationId);
                    await _refreshTokens.RevokeFamilyTokensAsync(refreshTokenEntity.FamilyId);
                    await _dbContext.SaveChangesAsync();
                    await tx.CommitAsync();
                    throw new RefreshTokenReplayDetectedException();
                }

                var user = await _users.GetByIdAsync(refreshTokenEntity.UserId) ?? throw new InvalidRefreshTokenException();
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

                refreshTokenEntity.RevokedAt = DateTime.UtcNow;
                refreshTokenEntity.ReplacedBy = newRefreshTokenEntity.Id;

                await _refreshTokens.AddAsync(newRefreshTokenEntity);
                await _dbContext.SaveChangesAsync();
                await tx.CommitAsync();

                _logger.LogInformation("Issued new tokens for user {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
                return (newAccessToken, newRefreshToken);
            }
            catch (Exception ex)
            {
                if (ex is MissingRefreshTokenException || ex is InvalidRefreshTokenException || ex is RefreshTokenReplayDetectedException)
                    throw;

                _logger.LogError(ex, "Unexpected error during refresh, CorrelationId: {CorrelationId}", correlationId);
                throw;
            }
        });
    }

    private string CreateJwtToken(User user)
    {
        var creds = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
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
