using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Data;
using AuthService.Models;

namespace AuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

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
            try
            {
                db.Database.Migrate();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to apply database migrations: " + ex.Message);
                Environment.Exit(1);
            }
        }

        app.UseExceptionHandler(errApp =>
        {
            errApp.Run(async context =>
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                var feat = context.Features.Get<IExceptionHandlerFeature>();
                var err = feat?.Error;
                if (err != null)
                    Console.Error.WriteLine("Unhandled exception: " + err.Message);
                await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
            });
        });

        app.UseCors("AllowAll");

        app.MapGet("/ping", () => "pong");

        // Signup
        app.MapPost("/signup", async (AuthRequest request, AppDbContext dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest("Email and password are required.");

            var emailValidator = new EmailAddressAttribute();
            if (!emailValidator.IsValid(request.Email))
                return Results.BadRequest("Invalid email address.");

            var exists = await dbContext.Users.AnyAsync(u => u.Email == request.Email);
            if (exists)
                return Results.Conflict("User with this email already exists.");

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
                // Could be a race condition / constraint violation — return conflict for duplicate-like errors
                Console.Error.WriteLine("DbUpdateException on signup: " + dbEx.Message);
                return Results.Conflict("Could not create user.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unexpected error on signup: " + ex.Message);
                return Results.Problem("Unexpected error during signup");
            }

            return Results.Created($"/users/{user.Id}", new { user.Id, user.Email });
        });

        // Login
        app.MapPost("/login", async (AuthRequest request, AppDbContext dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest("Email and password are required.");

            var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == request.Email);
            if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
                return Results.Unauthorized();

            var accessToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var refreshToken = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromDays(refreshTokenDays), isRefresh: true);

            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken });
        });

        // Refresh
        app.MapPost("/refresh", async (RefreshRequest request, AppDbContext dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Results.BadRequest("refresh_token required");

            var principal = ValidateJwtToken(request.RefreshToken, signingKey, issuer, audience, validateLifetime: true);
            if (principal == null)
                return Results.Unauthorized();

            var tokenType = principal.FindFirst("typ")?.Value;
            if (tokenType != "refresh")
                return Results.Unauthorized();

            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = principal.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            if (!Guid.TryParse(userIdClaim, out var userId))
                return Results.Unauthorized();

            var user = await dbContext.Users.FindAsync(userId);
            if (user == null)
                return Results.Unauthorized();

            var newAccess = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromMinutes(accessTokenMinutes));
            var newRefresh = CreateJwtToken(user, signingKey, issuer, audience, TimeSpan.FromDays(refreshTokenDays), isRefresh: true);

            return Results.Ok(new { access_token = newAccess, refresh_token = newRefresh });
        });

        app.Urls.Add("http://0.0.0.0:80");

        app.Run();
    }

    // --- DTOs ---
    public record AuthRequest(string Email, string Password);
    public record RefreshRequest(string RefreshToken);

    // --- Password hashing (PBKDF2) ---
    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        byte[] hash = pbkdf2.GetBytes(32);
        return $"100000.{Convert.ToHexString(salt)}.{Convert.ToHexString(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;
            int iterations = int.Parse(parts[0]);
            byte[] salt = Convert.FromHexString(parts[1]);
            byte[] expected = Convert.FromHexString(parts[2]);

            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
            byte[] actual = pbkdf2.GetBytes(expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
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
            new Claim("typ", isRefresh ? "refresh" : "access")
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
