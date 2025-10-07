using AuthService.Middleware;

public static class HttpContextExtensions
{
    private const string HeaderName = CorrelationIdMiddleware.HeaderNameConst;
    private const string ItemsKey = CorrelationIdMiddleware.ItemsKeyConst;

    public static string GetCorrelationId(this HttpContext ctx)
    {
        if (ctx == null) return string.Empty;
        if (ctx.Items.TryGetValue(ItemsKey, out var v) && v is string s && !string.IsNullOrEmpty(s))
            return s;

        if (ctx.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrEmpty(header))
            return header.ToString();

        return ctx.TraceIdentifier ?? string.Empty;
    }
}
