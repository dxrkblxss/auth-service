// TODO: Добавить хранение Refresh токенов в БД с возможностью отзыва
// TODO: Добавить подтверждение email с помощью шестизначного кода

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

namespace AuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = false;
            options.TimestampFormat = "[HH:mm:ss] ";
        });

        builder.Logging.AddDebug();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("DefaultConnection is not configured. Set a valid connection string in configuration.");

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connStr));

        var redisConnection = builder.Configuration["Redis:Connection"] ?? "redis:6379";
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(redisConnection));

        var jwtCfg = builder.Configuration.GetSection("Jwt");
        var jwtKey = jwtCfg.GetValue<string>("Key") ?? throw new InvalidOperationException("Jwt:Key is not set");
        var issuer = jwtCfg.GetValue<string>("Issuer") ?? "AuthService";
        var audience = jwtCfg.GetValue<string>("Audience") ?? "AuthClient";
        var accessTokenMinutes = jwtCfg.GetValue<int>("AccessTokenMinutes", 15);
        var refreshTokenDays = jwtCfg.GetValue<int>("RefreshTokenDays", 7);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

        var app = builder.Build();

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

        //Health check
        app.MapGet("/ping", (HttpContext ctx, ILogger<Program> log) =>
        {
            log.LogInformation("Ping received");
            return Results.Ok(new { pong = "pong", correlation_id = ctx.GetCorrelationId() });
        });

        // Signup
        app.MapPost("/signup", async (AuthRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            log.LogInformation("Signup attempt for email {Email}", request.Email);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                log.LogWarning("Signup failed: missing email or password");
                return Results.BadRequest(new { error = "Email and password are required.", correlation_id = ctx.GetCorrelationId() });
            }

            var emailValidator = new EmailAddressAttribute();
            if (!emailValidator.IsValid(request.Email))
            {
                log.LogWarning("Signup failed: invalid email {Email}", request.Email);
                return Results.BadRequest(new { error = "Invalid email address.", correlation_id = ctx.GetCorrelationId() });
            }

            var exists = await dbContext.Users.AnyAsync(u => u.Email == request.Email);
            if (exists)
            {
                log.LogWarning("Signup failed: user already exists {Email}", request.Email);
                return Results.Conflict(new { error = "User with this email already exists.", correlation_id = ctx.GetCorrelationId() });
            }

            var passwordHash = HashPassword(request.Password);

            var user = new User
            {
                Email = request.Email,
                PasswordHash = passwordHash,
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
                log.LogWarning(dbEx, "DbUpdateException while creating user {Email}", request.Email);
                return Results.Conflict(new { error = "Could not create user.", correlation_id = ctx.GetCorrelationId() });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unexpected error while creating user {Email}", request.Email);
                return Results.Problem(new { error = "Unexpected error during signup", correlation_id = ctx.GetCorrelationId() }.ToString());
            }

            log.LogInformation("User created successfully {UserId} {Email}", user.Id, user.Email);
            return Results.Created($"/users/{user.Id}", new { user.Id, user.Email, correlation_id = ctx.GetCorrelationId() });
        });

        // Login
        app.MapPost("/login", async (AuthRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            log.LogInformation("Login attempt for email {Email}", request.Email);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                log.LogWarning("Login failed: missing email or password");
                return Results.BadRequest(new { error = "Email and password are required.", correlation_id = ctx.GetCorrelationId() });
            }

            var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == request.Email);
            if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
            {
                log.LogWarning("Invalid login attempt for email {Email}", request.Email);
                return Results.Unauthorized();
            }

            var accessToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var refreshToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromDays(refreshTokenDays), isRefresh: true);

            log.LogInformation("User {UserId} logged in successfully", user.Id);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = ctx.GetCorrelationId() });
        });

        // Refresh
        app.MapPost("/refresh", async (RefreshRequest request, AppDbContext dbContext, ILogger<Program> log, HttpContext ctx) =>
        {
            log.LogInformation("Refresh token request received");

            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                log.LogWarning("Refresh failed: missing token");
                return Results.BadRequest(new { error = "refresh_token required", correlation_id = ctx.GetCorrelationId() });
            }

            var principal = ValidateJwtToken(request.RefreshToken, signingKey, issuer, audience, validateLifetime: true);
            if (principal == null)
            {
                log.LogWarning("Refresh failed: invalid token");
                return Results.Unauthorized();
            }

            var tokenType = principal.FindFirst("typ")?.Value;
            if (tokenType != "refresh")
            {
                log.LogWarning("Refresh failed: token type is not refresh (typ={Typ})", tokenType);
                return Results.Unauthorized();
            }

            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = principal.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || string.IsNullOrEmpty(email))
            {
                log.LogWarning("Refresh failed: token missing expected claims");
                return Results.Unauthorized();
            }

            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                log.LogWarning("Refresh failed: invalid user id claim {UserIdClaim}", userIdClaim);
                return Results.Unauthorized();
            }

            var user = await dbContext.Users.FindAsync(userId);
            if (user == null)
            {
                log.LogWarning("Refresh failed: user not found {UserId}", userId);
                return Results.Unauthorized();
            }

            var newAccess = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var newRefresh = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromDays(refreshTokenDays), isRefresh: true);

            log.LogInformation("Issued new tokens for user {UserId}", user.Id);
            return Results.Ok(new { access_token = newAccess, refresh_token = newRefresh, correlation_id = ctx.GetCorrelationId() });
        });

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

    // --- Password hashing (Argon2) ---
    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));

        argon2.DegreeOfParallelism = 4;
        argon2.Iterations = 3;
        argon2.MemorySize = 65536;

        argon2.Salt = salt;

        byte[] hash = argon2.GetBytes(32);

        string encoded = $"$argon2id$v=19$m=65536,t=3,p=4${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        return encoded;
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

    // --- JWT helpers ---
    private static string CreateJwtToken(User user, SymmetricSecurityKey key, string issuer, string audience, TimeSpan validFor, bool isRefresh = false)
    {
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("typ", isRefresh ? "refresh" : "access") // "typ" claim distinguishes access and refresh tokens
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

    private static ClaimsPrincipal? ValidateJwtToken(string token, SymmetricSecurityKey key, string issuer, string audience, bool validateLifetime)
    {
        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out var validatedToken);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
