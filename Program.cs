using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using AuthService.Data;
using AuthService.Middleware;
using AuthService.Extensions;
using AuthService.Options;
using AuthService.Services;
using AuthService.Repositories;
using AuthService.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace AuthService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto;

            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = false;
            options.TimestampFormat = "[HH:mm:ss]";
        });
        builder.Logging.AddDebug();


        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not set");

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            }));

        builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "redis:6379"));

        builder.Services.Configure<RefreshTokenOptions>(builder.Configuration.GetSection("RefreshTokenSettings"));
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.Configure<HashingOptions>(builder.Configuration.GetSection("Hashing"));

        builder.Services.AddScoped<IUserRepository, UserRepository>();
        builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddScoped<IAuthService, Services.AuthService>();

        var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not set");
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "AuthService",
                    ValidateAudience = true,
                    ValidAudience = "Backend",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey)
                    )
                };
            });

        builder.Services.AddAuthorization();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Auth Service API", Version = "v1" });

            c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                Description = "Введите JWT токен"
            });

            c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
            {
                {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference = new Microsoft.OpenApi.Models.OpenApiReference
                        {
                            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        app.UseSwagger(c =>
        {
            c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
            {
                var fallbackPrefix = app.Configuration["Swagger:BasePath"]
                    ?? Environment.GetEnvironmentVariable("SWAGGER_BASEPATH")
                    ?? string.Empty;

                var prefix = httpReq.Headers["X-Forwarded-Prefix"].FirstOrDefault();
                if (string.IsNullOrEmpty(prefix))
                    prefix = fallbackPrefix;

                var scheme = httpReq.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? httpReq.Scheme;
                var host = httpReq.Headers["X-Forwarded-Host"].FirstOrDefault() ?? httpReq.Host.Value;

                var baseUrl = $"{scheme}://{host}{prefix}";
                swaggerDoc.Servers =
                [
                    new() { Url = baseUrl }
                ];
            });
        });

        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
            c.RoutePrefix = "";
        });

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Running in {Environment}", builder.Environment.EnvironmentName);

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
                    migrateLogger.LogWarning(ex, "Database not ready, retry {Attempt}/{MaxRetries}", i + 1, maxRetries);
                    if (i == maxRetries - 1)
                    {
                        migrateLogger.LogCritical("Could not apply database migrations after {MaxRetries} attempts", maxRetries);
                        app.Lifetime.StopApplication();
                    }
                    else Thread.Sleep(delay);
                }
            }
        }

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors("AllowAll");

        app.MapGet("/health", (HttpContext ctx) =>
        {
            return Results.Ok(new { correlation_id = ctx.GetCorrelationId() });
        });

        app.MapPost("/signup", async (AuthRequest request, [FromServices] IAuthService authService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            var user = await authService.SignUpAsync(request.Email, request.Password, request.Name, correlationId);
            return Results.Created($"/users/{user.Id}", new { user.Id, user.Email, correlation_id = correlationId });
        });

        app.MapPost("/login", async (AuthRequest request, [FromServices] IAuthService authService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            var (accessToken, refreshToken) = await authService.LoginAsync(request.Email, request.Password, correlationId);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
        });

        app.MapPost("/logout", async (LogoutRequest request, [FromServices] IAuthService authService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            await authService.LogoutAsync(request.RefreshToken, correlationId);
            return Results.Ok(new { message = "Logged out successfully", correlation_id = correlationId });
        });

        app.MapPost("/refresh", async (RefreshRequest request, [FromServices] ITokenService tokenService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
            var (accessToken, refreshToken) = await tokenService.RefreshTokenAsync(request.RefreshToken, correlationId);
            return Results.Ok(new { access_token = accessToken, refresh_token = refreshToken, correlation_id = correlationId });
        });

        app.MapGet("/me", async ([FromServices] IUserService userService, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var userIdStr = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (!Guid.TryParse(userIdStr, out var userId))
                return Results.Unauthorized();

            var userDto = await userService.GetCurrentUserByIdAsync(userId, correlationId);

            if (userDto == null)
                return Results.NotFound();

            return Results.Ok(new { user = userDto, correlation_id = correlationId });
        })
        .RequireAuthorization();

        app.UseAuthentication();
        app.UseAuthorization();

        app.Urls.Add("http://0.0.0.0:80");
        logger.LogInformation("Listening on {Urls}", string.Join(',', app.Urls));

        app.Run();
    }
}
