using System.Text;
using System.Text.Json;
using ApiGateway.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Threading.RateLimiting;

// ─────────────────────────────────────────────────────────────────
// Bootstrap Serilog from appsettings.json BEFORE the host is built
// so that even startup errors are captured.
// ─────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("API Gateway starting...");

    var builder = WebApplication.CreateBuilder(args);

    // Replace the default .NET logger with Serilog
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext());

    // ─────────────────────────────────────────────────────────────────
    // 1. YARP Reverse Proxy
    // ─────────────────────────────────────────────────────────────────
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    // ─────────────────────────────────────────────────────────────────
    // 2. JWT Bearer Authentication
    // ─────────────────────────────────────────────────────────────────
    var jwtSettings = builder.Configuration.GetSection("JwtSettings");
    var secretKey = jwtSettings["SecretKey"]
        ?? throw new InvalidOperationException("JwtSettings:SecretKey is not configured.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = jwtSettings["Issuer"],
                ValidateAudience         = true,
                ValidAudience            = jwtSettings["Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                ValidateLifetime         = true,
                ClockSkew                = TimeSpan.Zero
            };

            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode  = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";

                    var body = JsonSerializer.Serialize(new
                    {
                        status  = 401,
                        error   = "Unauthorized",
                        message = "A valid Bearer token is required to access this resource."
                    });

                    await context.Response.WriteAsync(body);
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("authenticated", policy =>
            policy.RequireAuthenticatedUser());
    });

    // ─────────────────────────────────────────────────────────────────
    // 3. Rate Limiting  – Fixed Window, 100 req / 60 s per IP
    // ─────────────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("fixed-by-ip", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit          = 100,
                    Window               = TimeSpan.FromSeconds(60),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit           = 0
                }));

        options.OnRejected = async (rejectedContext, cancellationToken) =>
        {
            rejectedContext.HttpContext.Response.ContentType = "application/json";
            var body = JsonSerializer.Serialize(new
            {
                status  = 429,
                error   = "Too Many Requests",
                message = "You have exceeded the request limit. Please slow down and try again later."
            });
            await rejectedContext.HttpContext.Response.WriteAsync(body, cancellationToken);
        };
    });

    // ─────────────────────────────────────────────────────────────────
    // 4. Health Checks
    // ─────────────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy("Gateway is running."));

    // ─────────────────────────────────────────────────────────────────
    // Build the application
    // ─────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ─────────────────────────────────────────────────────────────────
    // Global Exception Handler – catches connection failures to downstream
    // ─────────────────────────────────────────────────────────────────
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode  = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";

            var body = JsonSerializer.Serialize(new
            {
                status  = 502,
                error   = "Bad Gateway",
                message = "An upstream service failed to respond. Please try again shortly."
            });

            await context.Response.WriteAsync(body);
        });
    });

    // ─────────────────────────────────────────────────────────────────
    // Middleware pipeline – ORDER MATTERS
    // ─────────────────────────────────────────────────────────────────
    app.UseRateLimiter();

    app.UseMiddleware<GatewayErrorMiddleware>();

    app.UseMiddleware<CorrelationIdMiddleware>();

    app.UseMiddleware<RequestLoggingMiddleware>();

    app.UseAuthentication();
    app.UseAuthorization();

    // ─────────────────────────────────────────────────────────────────
    // Health check endpoint – publicly accessible, no auth required
    // ─────────────────────────────────────────────────────────────────
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var result = JsonSerializer.Serialize(new
            {
                status    = report.Status.ToString(),
                duration  = report.TotalDuration.TotalMilliseconds,
                checks    = report.Entries.Select(e => new
                {
                    name     = e.Key,
                    status   = e.Value.Status.ToString(),
                    duration = e.Value.Duration.TotalMilliseconds,
                    description = e.Value.Description
                })
            });
            await context.Response.WriteAsync(result);
        }
    }).AllowAnonymous();

    // ─────────────────────────────────────────────────────────────────
    // YARP routing – must be last
    // ─────────────────────────────────────────────────────────────────
    app.MapReverseProxy()
        .RequireRateLimiting("fixed-by-ip");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Gateway terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
