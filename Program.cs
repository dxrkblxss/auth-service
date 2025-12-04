// TODO: Добавить подтверждение email с помощью шестизначного кода

using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using AuthService.Data;
using AuthService.Middleware;
using AuthService.Extensions;
using AuthService.Options;
using AuthService.Services;
using AuthService.Repositories;
using AuthService.DTOs;

namespace AuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Load secrets from environment variables (if provided) ---
        var jwtEnv = Environment.GetEnvironmentVariable("JWT_KEY") ?? Environment.GetEnvironmentVariable("Jwt__Key");
        if (!string.IsNullOrEmpty(jwtEnv))
        {
            builder.Configuration["Jwt:Key"] = jwtEnv;
        }

        var connEnv = Environment.GetEnvironmentVariable("DEFAULT_CONNECTION") ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrEmpty(connEnv))
        {
            builder.Configuration["ConnectionStrings:DefaultConnection"] = connEnv;
        }

        var redisEnv = Environment.GetEnvironmentVariable("REDIS__CONNECTION") ?? Environment.GetEnvironmentVariable("Redis__Connection");
        if (!string.IsNullOrEmpty(redisEnv))
        {
            builder.Configuration["Redis:Connection"] = redisEnv;
        }

        // --- Logging ---
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = false;
            options.TimestampFormat = "[HH:mm:ss]";
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
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    return ConnectionMultiplexer.Connect(redisConnection);
                }
                catch (RedisConnectionException ex)
                {
                    logger.LogWarning(ex, "Retry {Attempt}/3: Failed to connect to Redis", i + 1);
                    Thread.Sleep(2000);
                }
            }
            logger.LogCritical("Could not connect to Redis after 3 attempts");
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Failed to connect after retries");
        });

        // --- Options ---
        builder.Services.Configure<RefreshTokenOptions>(
            builder.Configuration.GetSection("RefreshTokenSettings"));

        builder.Services.Configure<JwtOptions>(
            builder.Configuration.GetSection("Jwt"));

        builder.Services.Configure<HashingOptions>(
            builder.Configuration.GetSection("Hashing"));

        // --- Repositories ---
        builder.Services.AddScoped<IUserRepository, UserRepository>();
        builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        // --- Services ---
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddScoped<IAuthService, Services.AuthService>();

        var app = builder.Build();

        // --- Log environment ---
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Running in {Environment} environment", builder.Environment.EnvironmentName);

        // --- DB Migration ---
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var migrateLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            var maxRetries = 20;
            var delay = TimeSpan.FromSeconds(5);

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    db.Database.Migrate();
                    migrateLogger.LogInformation("Database migrations applied successfully");
                    break;
                }
                catch (Exception ex)
                {
                    migrateLogger.LogWarning(ex, "Database not ready, retrying {Attempt}/{MaxRetries}", i + 1, maxRetries);
                    if (i == maxRetries - 1)
                    {
                        migrateLogger.LogCritical("Could not apply database migrations after {MaxRetries} attempts", maxRetries);
                        app.Lifetime.StopApplication();
                    }
                    else
                    {
                        Thread.Sleep(delay);
                    }
                }
            }
        }

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        logger.LogInformation("Starting AuthService application");

        app.UseCors("AllowAll");

        // --- Health check ---
        app.MapGet("/ping", (HttpContext ctx, ILogger<Program> log) =>
        {
            log.LogInformation("Ping received, CorrelationId: {CorrelationId}", ctx.GetCorrelationId());
            return Results.Ok(new { pong = "pong", correlation_id = ctx.GetCorrelationId() });
        });

        // --- Signup ---
        app.MapPost("/signup", async (AuthRequest request, IAuthService authService, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            try
            {
                var user = await authService.SignUpAsync(request.Email, request.Password, correlationId);
                return Results.Created($"/users/{user.Id}", new { user.Id, user.Email, correlation_id = correlationId });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Signup failed for {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                throw;
            }
        });

        // --- Login ---
        app.MapPost("/login", async (AuthRequest request, IAuthService authService, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            try
            {
                var (accessToken, refreshToken) = await authService.LoginAsync(request.Email, request.Password, correlationId);
                return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Login failed for {Email}, CorrelationId: {CorrelationId}", request.Email, correlationId);
                throw;
            }
        });

        // --- Refresh ---
        app.MapPost("/refresh", async (RefreshRequest request, ITokenService tokenService, ILogger<Program> log, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            try
            {
                var (accessToken, refreshToken) = await tokenService.RefreshTokenAsync(request.RefreshToken, correlationId);
                return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Token refresh failed, CorrelationId: {CorrelationId}", correlationId);
                throw;
            }
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
            Environment.Exit(1);
        }
    }
}
