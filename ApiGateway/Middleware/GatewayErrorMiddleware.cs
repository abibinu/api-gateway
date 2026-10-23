using System.Text.Json;

namespace ApiGateway.Middleware;

public class GatewayErrorMiddleware
{
    private static readonly HashSet<int> _handledStatusCodes = [502, 503, 504];

    private readonly RequestDelegate _next;

    public GatewayErrorMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (_handledStatusCodes.Contains(context.Response.StatusCode)
            && !context.Response.HasStarted)
        {
            context.Response.ContentType = "application/json";

            var (error, message) = context.Response.StatusCode switch
            {
                502 => ("Bad Gateway",       "A downstream service returned an invalid response."),
                503 => ("Service Unavailable","A downstream service is temporarily unavailable."),
                504 => ("Gateway Timeout",   "A downstream service did not respond in time."),
                _   => ("Gateway Error",     "An unexpected gateway error occurred.")
            };

            var body = JsonSerializer.Serialize(new
            {
                status  = context.Response.StatusCode,
                error,
                message
            });

            await context.Response.WriteAsync(body);
        }
    }
}
