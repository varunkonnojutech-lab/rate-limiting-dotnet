using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Rate limiting — everything here uses the built-in middleware that ships
// with ASP.NET Core 7+. No NuGet packages, no external services.
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    // Every rejection becomes a 429 with a JSON body and a Retry-After header.
    // A bare 429 with no hint when to retry is a bug, not a feature.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";

        int? retryAfterSeconds = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            context.HttpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = "TooManyRequests",
            message = "You hit the rate limit for this endpoint. " +
                      "Back off and retry after the delay in the Retry-After header.",
            retryAfterSeconds
        });
        await context.HttpContext.Response.WriteAsync(payload, cancellationToken);
    };

    // Global safety net: 200 requests/minute per client IP across the API.
    // Generous on purpose — it catches runaway clients, not normal use.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Fixed window: 5 requests per 10 seconds. Simple, predictable, bursty
    // at window boundaries — fine for "N calls per minute" style quotas.
    options.AddFixedWindowLimiter("fixed", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromSeconds(10);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });

    // Sliding window: 10 requests per 30 seconds, split into 3 segments.
    // Smooths out the boundary burst a fixed window allows.
    options.AddSlidingWindowLimiter("sliding", o =>
    {
        o.PermitLimit = 10;
        o.Window = TimeSpan.FromSeconds(30);
        o.SegmentsPerWindow = 3;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });

    // Token bucket: burst of 10, then 1 token per second. The classic
    // "allow bursts, but cap the sustained rate" shape — what most public
    // APIs actually mean when they say "100 requests per minute".
    options.AddTokenBucketLimiter("token-bucket", o =>
    {
        o.TokenLimit = 10;
        o.TokensPerPeriod = 1;
        o.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        o.AutoReplenishment = true;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });

    // Concurrency: at most 2 requests in flight, 1 queued, the rest 429.
    // This protects a slow downstream, not a quota — it limits parallelism.
    options.AddConcurrencyLimiter("concurrency", o =>
    {
        o.PermitLimit = 2;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 1;
    });

    // Per-API-key: each key gets its own fixed window of 5 requests / 10s.
    // This is the one that matters in production — per-client quotas, not
    // one shared bucket that lets a single client starve everyone else.
    // Requests without a key share the "anonymous" bucket.
    options.AddPolicy("per-key", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseRateLimiter();

static IResult Ok(string policy, string note) => Results.Json(new
{
    policy,
    note,
    utcNow = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
});

app.MapGet("/", () => Results.Json(new
{
    name = "Rate Limiting in .NET 8 — live demo",
    policies = new[]
    {
        new { endpoint = "GET /fixed",        policy = "fixed window",    limit = "5 requests / 10s" },
        new { endpoint = "GET /sliding",      policy = "sliding window",  limit = "10 requests / 30s" },
        new { endpoint = "GET /token-bucket", policy = "token bucket",    limit = "burst 10, refill 1/s" },
        new { endpoint = "GET /concurrency", policy = "concurrency",     limit = "2 in flight, 1 queued" },
        new { endpoint = "GET /per-key",      policy = "per API key",     limit = "5 requests / 10s per X-Api-Key" },
        new { endpoint = "GET /unlimited",    policy = "none",            limit = "no policy — control endpoint" },
    },
    tip = "Hit /fixed 6 times fast and watch the 6th come back 429 with a Retry-After header. " +
          "Or run ./scripts/load-test.sh"
}));

// One endpoint per policy, so you can feel each limiter in isolation.
app.MapGet("/fixed", () => Ok("fixed", "5 requests per 10-second window."))
   .RequireRateLimiting("fixed");

app.MapGet("/sliding", () => Ok("sliding", "10 requests per 30-second window, 3 segments."))
   .RequireRateLimiting("sliding");

app.MapGet("/token-bucket", () => Ok("token-bucket", "Burst of 10, then 1 token per second."))
   .RequireRateLimiting("token-bucket");

app.MapGet("/concurrency", async () =>
{
    // Holds a permit for half a second so parallel requests actually collide.
    await Task.Delay(500);
    return Ok("concurrency", "At most 2 in flight, 1 queued — the rest get 429 immediately.");
}).RequireRateLimiting("concurrency");

app.MapGet("/per-key", (HttpContext context) => Ok("per-key",
        $"Your key is '{context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? "anonymous"}' — " +
        "each key gets its own 5-requests-per-10s bucket."))
   .RequireRateLimiting("per-key");

app.MapGet("/unlimited", () => Ok("none", "No policy here — use this as the control in your load tests."));

app.Run();

// Lets WebApplicationFactory<Program> start the app from the test project.
public partial class Program { }
