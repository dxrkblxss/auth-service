using System.Diagnostics;

namespace AuthService.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderNameConst = "X-Correlation-ID";
    public const string ItemsKeyConst = "X-Correlation-ID";

    private readonly RequestDelegate _next = next;
    private readonly ILogger<CorrelationIdMiddleware> _logger = logger;
    private const string HeaderName = HeaderNameConst;
    private const string ItemsKey = ItemsKeyConst;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var provided) && !string.IsNullOrWhiteSpace(provided)
            ? provided.ToString()
            : Guid.NewGuid().ToString();

        context.Items[ItemsKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        context.TraceIdentifier = correlationId;
        if (Activity.Current != null)
        {
            try
            {
                Activity.Current.SetTag("correlation_id", correlationId);
            }
            catch
            {
                // ignore any activity exceptions
            }
        }

        using (_logger.BeginScope(new Dictionary<string, object> { [HeaderName] = correlationId }))
        {
            _logger.LogDebug("Assigned correlation id {CorrelationId} to request {Method} {Path}", correlationId, context.Request.Method, context.Request.Path);
            await _next(context);
        }
    }
}
