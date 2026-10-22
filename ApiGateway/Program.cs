using System.Text;
using System.Text.Json;
using ApiGateway.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────
// 1. YARP Reverse Proxy
//    Reads "ReverseProxy" section from appsettings.json
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
// Build the application
// ─────────────────────────────────────────────────────────────────
var app = builder.Build();

// ─────────────────────────────────────────────────────────────────
// 4. Global Exception Handler  – catches 502 / 504 from downstream
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

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// ─────────────────────────────────────────────────────────────────
// 5. YARP routing – must be last
// ─────────────────────────────────────────────────────────────────
app.MapReverseProxy()
    .RequireRateLimiting("fixed-by-ip");

app.Run();
