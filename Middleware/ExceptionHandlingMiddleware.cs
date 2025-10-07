using System.Text.Json;

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
        catch (Exception ex)
        {
            var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKeyConst, out var v) && v is string s && !string.IsNullOrEmpty(s)
                ? s
                : (context.Request.Headers.TryGetValue(HeaderName, out var h) ? h.ToString() : context.TraceIdentifier);

            using (_logger.BeginScope(new Dictionary<string, object> { [HeaderName] = correlationId }))
            {
                _logger.LogError(ex, "Unhandled exception while processing request. CorrelationId={CorrelationId}", correlationId);
            }

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            context.Response.Headers[HeaderName] = correlationId;

            var payload = new
            {
                error = "internal_server_error",
                message = "An unexpected error occurred.",
                correlation_id = correlationId
            };

            var json = JsonSerializer.Serialize(payload);
            await context.Response.WriteAsync(json);
        }
    }
}
