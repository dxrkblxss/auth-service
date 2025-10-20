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
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnection));

        // --- Options ---
        builder.Services.Configure<RefreshTokenOptions>(
            builder.Configuration.GetSection("RefreshTokenSettings"));

        builder.Services.Configure<JwtOptions>(
            builder.Configuration.GetSection("Jwt"));

        // --- Repositories ---
        builder.Services.AddScoped<IUserRepository, UserRepository>();
        builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        // --- Services ---
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddScoped<IAuthService, Services.AuthService>();

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
        app.MapPost("/signup", async (AuthRequest request, IAuthService authService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var user = await authService.SignUpAsync(request.Email, request.Password, correlationId);
            return Results.Created($"/users/{user.Id}", new { user.Id, user.Email, correlation_id = correlationId });
        });

        // --- Login ---
        app.MapPost("/login", async (AuthRequest request, IAuthService authService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var (accessToken, refreshToken) = await authService.LoginAsync(request.Email, request.Password, correlationId);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
        });

        // --- Refresh ---
        app.MapPost("/refresh", async (RefreshRequest request, ITokenService tokenService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var (accessToken, refreshToken) = await tokenService.RefreshTokenAsync(request.RefreshToken, correlationId);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
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
}
