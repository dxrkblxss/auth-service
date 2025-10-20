using Microsoft.AspNetCore.Mvc;
using AuthService.Exceptions;

namespace AuthService.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private const string HeaderName = CorrelationIdMiddleware.HeaderNameConst;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AuthServiceException ex)
        {
            var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKeyConst, out var v) && v is string s && !string.IsNullOrEmpty(s)
                ? s
                : (context.Request.Headers.TryGetValue(HeaderName, out var h) ? h.ToString() : context.TraceIdentifier);

            using (_logger.BeginScope(new Dictionary<string, object> { [HeaderName] = correlationId }))
            {
                _logger.LogWarning(ex, "AuthServiceException thrown. CorrelationId={CorrelationId}", correlationId);
            }

            int statusCode = ex switch
            {
                MissingFieldsException => StatusCodes.Status400BadRequest,
                InvalidEmailException => StatusCodes.Status400BadRequest,
                UserAlreadyExistsException => StatusCodes.Status409Conflict,
                UserCreationFailedException => StatusCodes.Status409Conflict,
                InvalidCredentialsException => StatusCodes.Status401Unauthorized,
                MissingRefreshTokenException => StatusCodes.Status400BadRequest,
                InvalidRefreshTokenException => StatusCodes.Status401Unauthorized,
                RefreshTokenCreationFailedException => StatusCodes.Status409Conflict,
                RefreshTokenReplayDetectedException => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status400BadRequest
            };

            var problem = new ProblemDetails
            {
                Title = ex.Message,
                Status = statusCode,
                Detail = ex.InnerException?.Message ?? ex.Message
            };
            problem.Extensions["correlation_id"] = correlationId;

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers[HeaderName] = correlationId;

            await context.Response.WriteAsJsonAsync(problem);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKeyConst, out var v) && v is string s && !string.IsNullOrEmpty(s)
                ? s
                : (context.Request.Headers.TryGetValue(HeaderName, out var h) ? h.ToString() : context.TraceIdentifier);

            using (_logger.BeginScope(new Dictionary<string, object> { [HeaderName] = correlationId }))
            {
                _logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            }

            var problem = new ProblemDetails
            {
                Title = "An unexpected error occurred.",
                Status = 500,
                Detail = "Internal server error"
            };
            problem.Extensions["correlation_id"] = correlationId;

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            context.Response.Headers[HeaderName] = correlationId;

            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}
