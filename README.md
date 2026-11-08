# Rate Limiting in .NET 8 — fixed, sliding, token bucket, concurrency, per-key

[![Build](https://github.com/varunkonnojutech-lab/rate-limiting-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/rate-limiting-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/rate-limiting-dotnet)

Every rate limiter that ships built into ASP.NET Core, running side by side in one API — so you can feel the difference instead of reading about it. Hit an endpoint too fast and you get a real `429 Too Many Requests` with a `Retry-After` header. The tests prove each limiter's exact behaviour: how many requests it allows, when it starts rejecting, and when it lets you back in.

No NuGet packages, no Redis, no external services. The middleware is part of the framework since .NET 7.

## Who is this for

.NET developers who ship APIs and have ever wondered what to do when one client hammers an endpoint, or what "100 requests per minute" should actually mean in code. If you have copy-pasted a rate-limit snippet without knowing whether a fixed window or a token bucket fits your case, start here.

## What you'll learn

- **Fixed window** — 5 requests per 10 seconds. The simplest quota, and why it lets a burst through at every window boundary
- **Sliding window** — 10 requests per 30 seconds across 3 segments. Same idea, smoother edges
- **Token bucket** — burst of 10, then 1 token per second. The shape most public APIs actually mean by "100 requests per minute"
- **Concurrency limiter** — at most 2 requests in flight, 1 queued. This protects a slow downstream, not a quota
- **Per-API-key partitions** — each client gets its own bucket, so one greedy caller can't starve everyone else (the one that matters most in production)
- **Global limiter** — a 200-requests-per-minute safety net per IP across the whole API
- **Good 429s** — a JSON error body plus a `Retry-After` header, so clients know when to come back
- **How to choose** — the decision guide in [`docs/rate-limiting-guide.md`](docs/rate-limiting-guide.md)

## Quick start

You need .NET 8. Nothing else.

```bash
dotnet test   # 10 tests: every limiter's exact behaviour, including refill and window reset
dotnet run --project src/RateLimitingDemo.Api   # http://localhost:5090
```

## Try it

```bash
# Fixed window: the 6th request in 10 seconds is rejected
for i in 1 2 3 4 5 6; do curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5090/fixed; done
# 200 200 200 200 200 429

# The 429 tells you when to retry
curl -si http://localhost:5090/fixed | grep -i retry-after
# Retry-After: 9

# Token bucket: burst of 10, then it refills at 1 token per second
for i in $(seq 1 11); do curl -s -o /dev/null -w "%{http_code} " http://localhost:5090/token-bucket; done
# 200 x10, then 429 — wait 3 seconds and one more succeeds

# Per-key: alice and bob get independent buckets
curl -s -H "X-Api-Key: alice" http://localhost:5090/per-key   # 200
curl -s -H "X-Api-Key: bob"   http://localhost:5090/per-key   # 200

# Concurrency: 5 parallel requests, 2 in flight + 1 queued = 2 rejected
seq 1 5 | xargs -P5 -I{} curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5090/concurrency

# Or run the whole demo at once:
./scripts/load-test.sh
```

## Endpoints

| Endpoint | Policy | Limit |
|---|---|---|
| `GET /fixed` | Fixed window | 5 requests / 10s |
| `GET /sliding` | Sliding window | 10 requests / 30s |
| `GET /token-bucket` | Token bucket | Burst 10, refill 1/s |
| `GET /concurrency` | Concurrency | 2 in flight, 1 queued |
| `GET /per-key` | Per API key (`X-Api-Key` header) | 5 requests / 10s per key |
| `GET /unlimited` | None | Control endpoint — never limited |
| `GET /` | — | Lists every policy |

Every endpoint is also behind a global limiter of 200 requests/minute per IP, so a runaway client can't take the whole API down.

## How it works

Everything lives in [`src/RateLimitingDemo.Api/Program.cs`](src/RateLimitingDemo.Api/Program.cs):

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = /* 429 JSON body + Retry-After header */;

    options.AddFixedWindowLimiter("fixed", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromSeconds(10);
    });

    // Per-key: one bucket per client, partitioned by header
    options.AddPolicy("per-key", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromSeconds(10)
            }));
});

// Then attach a policy to an endpoint:
app.MapGet("/fixed", () => ...).RequireRateLimiting("fixed");
```

The tests ([`tests/RateLimitingDemo.Tests/RateLimitingTests.cs`](tests/RateLimitingDemo.Tests/RateLimitingTests.cs)) spin up the real app with `WebApplicationFactory` and assert the behaviour you can feel with curl: exact allow counts, `Retry-After` on rejection, token refill after a wait, window reset after 10 seconds, and per-key isolation — each test gets a fresh app instance so limiter state never leaks between tests.

## The one mistake to avoid

Rate limiting by client IP looks right until your API sits behind a load balancer or a gateway — then every client shares one IP and one bucket. In production, partition by something that identifies the caller (API key, user id, tenant id) and read the real client IP from `X-Forwarded-For` via `ForwardedHeadersMiddleware`. See [`docs/rate-limiting-guide.md`](docs/rate-limiting-guide.md) for the full checklist, including what changes when you scale to multiple servers.

## Next steps

- Read the [decision guide](docs/rate-limiting-guide.md) — which limiter for which scenario, and what breaks at scale
- Add a Redis-backed limiter (e.g. via `AspNetCoreRateLimit` or a custom `PartitionedRateLimiter`) when one server's in-memory buckets are no longer enough
- Pair this with the resilience patterns in [resilience-with-polly](https://github.com/varunkonnojutech-lab/resilience-with-polly) — rate limiting protects your server, retries and circuit breakers protect your clients
