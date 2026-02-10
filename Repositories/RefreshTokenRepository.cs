using AuthService.Models;
using AuthService.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetValidByTokenHashAsync(string tokenHash, bool forUpdate = false);
    Task AddAsync(RefreshToken refreshToken);
    void Delete(RefreshToken refreshToken);
    Task RevokeFamilyTokensAsync(Guid familyId);
}

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _dbContext;

    public RefreshTokenRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<RefreshToken?> GetValidByTokenHashAsync(string tokenHash, bool lockForUpdate = false)
    {
        if (lockForUpdate)
        {
            return await _dbContext.RefreshTokens
                .FromSqlRaw(@"
                    SELECT * FROM ""RefreshTokens"" 
                    WHERE ""TokenHash"" = {0} AND ""ExpiresAt"" >= NOW() 
                    ORDER BY ""CreatedAt"" DESC 
                    FOR UPDATE", tokenHash)
                .FirstOrDefaultAsync();
        }

        return await _dbContext.RefreshTokens
            .Where(rt => rt.TokenHash == tokenHash && rt.ExpiresAt >= DateTime.UtcNow)
            .OrderByDescending(rt => rt.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task AddAsync(RefreshToken refreshToken)
    {
        await _dbContext.RefreshTokens.AddAsync(refreshToken);
    }

    public void Delete(RefreshToken refreshToken)
    {
        _dbContext.RefreshTokens.Remove(refreshToken);
    }

    public async Task RevokeFamilyTokensAsync(Guid familyId)
    {
        await _dbContext.RefreshTokens
                    .Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, DateTime.UtcNow));
    }
}
