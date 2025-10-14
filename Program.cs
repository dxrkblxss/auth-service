// TODO: Реализовать ревокацию всей семьи токенов при обнаружении повторного использования
// TODO: Добавить подтверждение email с помощью шестизначного кода
// TODO: Логировать correlation_id

using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using StackExchange.Redis;
using AuthService.Data;
using AuthService.Models;
using AuthService.Middleware;
using AuthService.Extensions;
using Microsoft.AspNetCore.WebUtilities;

namespace AuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Logging ---
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = false;
            options.TimestampFormat = "[HH:mm:ss] ";
        });
        builder.Logging.AddDebug();

        // --- CORS ---
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        // --- DB ---
        var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is not configured.");
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connStr));

        // --- Redis ---
        var redisConnection = builder.Configuration["Redis:Connection"] ?? "redis:6379";
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnection));

        // --- JWT & Refresh token settings ---
        var jwtCfg = builder.Configuration.GetSection("Jwt");
        var jwtKey = jwtCfg.GetValue<string>("Key") ?? throw new InvalidOperationException("Jwt:Key is not set");
        var issuer = jwtCfg.GetValue<string>("Issuer") ?? "AuthService";
        var audience = jwtCfg.GetValue<string>("Audience") ?? "AuthClient";
        var accessTokenMinutes = jwtCfg.GetValue<int>("AccessTokenMinutes", 15);

        var refreshCfg = builder.Configuration.GetSection("RefreshTokenSettings");
        var refreshTokenDays = refreshCfg.GetValue<int>("DaysValid", 7);
        var refreshTokenLength = refreshCfg.GetValue<int>("TokenLengthBytes", 64);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

        var app = builder.Build();

        // --- DB Migration ---
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var migrateLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                db.Database.Migrate();
                migrateLogger.LogInformation("Database migrations applied successfully");
            }
            catch (Exception ex)
            {
                migrateLogger.LogCritical(ex, "Failed to apply database migrations");
                Environment.Exit(1);
            }
        }

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting AuthService application");

        app.UseCors("AllowAll");

        // --- Health check ---
        app.MapGet("/ping", (HttpContext ctx, ILogger<Program> log) =>
        {
            log.LogInformation("Ping received, CorrelationId: {CorrelationId}", ctx.GetCorrelationId());
            return Results.Ok(new { pong = "pong", correlation_id = ctx.GetCorrelationId() });
        });

        // --- Signup ---
        app.MapPost("/signup", async (AuthRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            log.LogInformation("Signup attempt for email {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                log.LogWarning("Signup failed: missing email or password, CorrelationId: {CorrelationId}", correlationId);
                return Results.BadRequest(new { error = "missing_fields", message = "Email and password are required.", correlation_id = correlationId });
            }

            if (!new EmailAddressAttribute().IsValid(request.Email))
            {
                log.LogWarning("Signup failed: invalid email {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                return Results.BadRequest(new { error = "invalid_email", message = "Invalid email address.", correlation_id = correlationId });
            }

            if (await dbContext.Users.AnyAsync(u => u.Email == request.Email))
            {
                log.LogWarning("Signup failed: user exists {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                return Results.Conflict(new { error = "user_exists", message = "User with this email already exists.", correlation_id = correlationId });
            }

            var user = new User
            {
                Email = request.Email,
                PasswordHash = HashPassword(request.Password),
                Role = "User",
                CreatedAt = DateTime.UtcNow
            };

            dbContext.Users.Add(user);
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx)
            {
                log.LogWarning(dbEx, "Failed to create user {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                return Results.Conflict(new { error = "user_creation_failed", message = "Could not create user.", correlation_id = correlationId });
            }

            log.LogInformation("User created successfully {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
            return Results.Created($"/users/{user.Id}", new { user.Id, user.Email, correlation_id = correlationId });
        });

        // --- Login ---
        app.MapPost("/login", async (AuthRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            log.LogInformation("Login attempt for email {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                log.LogWarning("Login failed: missing email or password, CorrelationId: {CorrelationId}", correlationId);
                return Results.BadRequest(new { error = "missing_fields", message = "Email and password are required.", correlation_id = correlationId });
            }

            var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == request.Email);
            if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
            {
                log.LogWarning("Login failed: invalid credentials {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                return Results.Json(
                    new { error = "invalid_token", message = "The token is invalid.", correlation_id = ctx.GetCorrelationId() },
                    statusCode: StatusCodes.Status401Unauthorized
                );
            }

            var accessToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var refreshToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(refreshTokenLength));

            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = HashRefreshToken(refreshToken),
                FamilyId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenDays)
            };

            dbContext.RefreshTokens.Add(refreshTokenEntity);
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx)
            {
                log.LogWarning(dbEx, "Failed to create refresh token for user {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
                return Results.Conflict(new { error = "refresh_token_creation_failed", message = "Could not create refresh token.", correlation_id = correlationId });
            }

            log.LogInformation("User {UserId} logged in, refresh token {TokenId} created, CorrelationId: {CorrelationId}", user.Id, refreshTokenEntity.Id, correlationId);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
        });

        // --- Refresh ---
        app.MapPost("/refresh", async (RefreshRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            log.LogInformation("Refresh token request received, CorrelationId: {CorrelationId}", correlationId);

            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                log.LogWarning("Refresh failed: missing token, CorrelationId: {CorrelationId}", correlationId);
                return Results.BadRequest(new { error = "missing_refresh_token", message = "refresh_token is required.", correlation_id = correlationId });
            }

            var tokenHash = HashRefreshToken(request.RefreshToken);

            var refreshToken = await dbContext.RefreshTokens
                .Where(rt => rt.TokenHash == tokenHash && rt.ExpiresAt >= DateTime.UtcNow)
                .FirstOrDefaultAsync();

            if (refreshToken == null)
            {
                log.LogWarning("Refresh failed: invalid or expired token, CorrelationId: {CorrelationId}", correlationId);
                return Results.Json(
                    new { error = "invalid_token", message = "The token is invalid.", correlation_id = ctx.GetCorrelationId() },
                    statusCode: StatusCodes.Status401Unauthorized
                );
            }

            if (refreshToken.RevokedAt.HasValue)
            {
                log.LogWarning("Replay detected: token already revoked, RefreshTokenId: {TokenId}, UserId: {UserId}, CorrelationId: {CorrelationId}",
                    refreshToken.Id, refreshToken.UserId, correlationId);

                var familyId = refreshToken.FamilyId;
                await dbContext.RefreshTokens
                    .Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, DateTime.UtcNow));

                return Results.Json(
                    new { error = "invalid_token", message = "The token is invalid.", correlation_id = ctx.GetCorrelationId() },
                    statusCode: StatusCodes.Status401Unauthorized
                );
            }

            var user = await dbContext.Users.FindAsync(refreshToken.UserId);
            if (user == null)
            {
                log.LogWarning("Refresh failed: user not found {UserId}, CorrelationId: {CorrelationId}", refreshToken.UserId, correlationId);
                return Results.Json(
                    new { error = "invalid_token", message = "The token is invalid.", correlation_id = ctx.GetCorrelationId() },
                    statusCode: StatusCodes.Status401Unauthorized
                );
            }

            var newAccessToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var newRefreshToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(refreshTokenLength));

            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = HashRefreshToken(newRefreshToken),
                FamilyId = refreshToken.FamilyId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenDays)
            };

            using var tx = await dbContext.Database.BeginTransactionAsync();

            refreshToken.RevokedAt = DateTime.UtcNow;
            dbContext.RefreshTokens.Add(refreshTokenEntity);

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx)
            {
                log.LogWarning(dbEx, "Failed to create new refresh token for user {UserId}, CorrelationId: {CorrelationId}", user.Id, correlationId);
                return Results.Conflict(new { error = "refresh_token_creation_failed", message = "Could not create new refresh token.", correlation_id = correlationId });
            }

            refreshToken.ReplacedBy = refreshTokenEntity.Id;

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx)
            {
                log.LogWarning(dbEx, "Failed to update old refresh token {OldTokenId} for user {UserId}, CorrelationId: {CorrelationId}", refreshToken.Id, user.Id, correlationId);
                return Results.Conflict(new { error = "refresh_token_update_failed", message = "Could not update old refresh token.", correlation_id = correlationId });
            }

            await tx.CommitAsync();

            log.LogInformation("Issued new tokens for user {UserId}, NewRefreshTokenId: {TokenId}, CorrelationId: {CorrelationId}", user.Id, refreshTokenEntity.Id, correlationId);

            return Results.Ok(new { access_token = newAccessToken, refresh_token = newRefreshToken, correlation_id = correlationId });
        });

        // --- Run app ---
        app.Urls.Add("http://0.0.0.0:80");
        logger.LogInformation("Listening on {Urls}", string.Join(',', app.Urls));

        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Host terminated unexpectedly");
            throw;
        }
    }

    // --- DTOs ---
    public record AuthRequest(string Email, string Password);
    public record RefreshRequest(string RefreshToken);

    // --- Password hashing and verification ---
    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.DegreeOfParallelism = 4;
        argon2.Iterations = 3;
        argon2.MemorySize = 65536;
        argon2.Salt = salt;
        byte[] hash = argon2.GetBytes(32);
        return $"$argon2id$v=19$m=65536,t=3,p=4${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string encodedFromDb)
    {
        var parts = encodedFromDb.Split('$', StringSplitOptions.RemoveEmptyEntries);
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

    public static string HashRefreshToken(string token)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    // --- JWT helpers ---
    private static string CreateJwtToken(User user, SymmetricSecurityKey key, string issuer, string audience, TimeSpan validFor)
    {
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(validFor),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}