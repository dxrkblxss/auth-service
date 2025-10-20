using AuthService.Models;
using AuthService.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetValidByTokenHashAsync(string tokenHash);

    Task AddAsync(RefreshToken refreshToken);

    Task RevokeFamilyTokensAsync(Guid familyId);
}

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _dbContext;

    public RefreshTokenRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<RefreshToken?> GetValidByTokenHashAsync(string tokenHash)
    {
        return await _dbContext.RefreshTokens
                .Where(rt => rt.TokenHash == tokenHash && rt.ExpiresAt >= DateTime.UtcNow)
                .FirstOrDefaultAsync();
    }

    public async Task AddAsync(RefreshToken refreshToken)
    {
        await _dbContext.RefreshTokens.AddAsync(refreshToken);
    }

    public async Task RevokeFamilyTokensAsync(Guid familyId)
    {
        await _dbContext.RefreshTokens
                    .Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, DateTime.UtcNow));
    }
}
