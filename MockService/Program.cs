var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Auth endpoints (/api/auth/*) ──────────────────────────────────
app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    if (req.Username == "demo" && req.Password == "password")
    {
        return Results.Ok(new
        {
            message = "Login successful (mock token — use a real JWT in production)",
            token   = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.mock_payload.mock_signature",
            user    = req.Username
        });
    }
    return Results.Unauthorized();
});

app.MapPost("/api/auth/register", (RegisterRequest req) =>
    Results.Ok(new { message = $"User '{req.Username}' registered successfully.", userId = Guid.NewGuid() }));

// ── Users endpoints (/api/users/*) ────────────────────────────────
app.MapGet("/api/users", () => Results.Ok(new[]
{
    new { id = 1, name = "Alice",   email = "alice@example.com", role = "Admin" },
    new { id = 2, name = "Bob",     email = "bob@example.com",   role = "User"  },
    new { id = 3, name = "Charlie", email = "charlie@example.com",role = "User"  },
}));

app.MapGet("/api/users/{id:int}", (int id) =>
    id is 1 or 2 or 3
        ? Results.Ok(new { id, name = $"User #{id}", email = $"user{id}@example.com" })
        : Results.NotFound(new { error = $"User with id {id} not found." }));

// ── Inventory endpoints (/api/inventory/*) ────────────────────────
app.MapGet("/api/inventory/items", () => Results.Ok(new[]
{
    new { id = "SKU-001", name = "Wireless Keyboard", stock = 42,  price = 49.99 },
    new { id = "SKU-002", name = "USB-C Hub",         stock = 115, price = 29.99 },
    new { id = "SKU-003", name = "Mechanical Mouse",  stock = 8,   price = 79.99 },
}));

app.MapGet("/api/inventory/items/{sku}", (string sku) =>
    Results.Ok(new { sku, name = $"Product {sku}", stock = 10, price = 19.99 }));

// ── Health check ──────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new
{
    status    = "Healthy",
    service   = "MockService",
    timestamp = DateTime.UtcNow
}));

// Diagnostic: echo every request's headers (useful for verifying
// that the gateway correctly injects X-Correlation-ID)
app.MapGet("/api/debug/headers", (HttpContext ctx) =>
    Results.Ok(ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));

app.Run();

record LoginRequest(string Username, string Password);
record RegisterRequest(string Username, string Email, string Password);
