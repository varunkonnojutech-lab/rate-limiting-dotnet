# Choosing a rate limiter — a decision guide

The demo API shows five limiters. They are not interchangeable. Pick by answering one question: **what are you actually protecting?**

## The limiters at a glance

| Limiter | What it caps | Best for | Watch out for |
|---|---|---|---|
| **Fixed window** | Requests per time window | Simple quotas: "100 calls per minute" on a pricing page | A burst of 2× the limit is possible at a window boundary (last second of window N + first second of window N+1) |
| **Sliding window** | Requests per rolling window | User-facing APIs where boundary bursts matter | Slightly more memory (one counter per segment); still per-process |
| **Token bucket** | Sustained rate, with bursts allowed | Public APIs, webhooks, anything where a short burst is legitimate but a sustained flood is not | Clients learn the burst size and ride it; pair with a fixed window if bursts themselves are the problem |
| **Concurrency** | Requests in flight right now | Protecting a slow database, a downstream service, or a thread pool | Says nothing about total volume — 2-at-a-time forever is still a flood |
| **Per-key partition** | Requests per caller | Any multi-tenant API — this is the one that stops one bad client starving the rest | Only as good as your key: a shared or missing key collapses everyone into one bucket |

## Decision tree

1. **Are you protecting a slow downstream?** → Concurrency limiter. Nothing else caps in-flight work.
2. **Are you enforcing a plan/quota per customer?** → Fixed window per API key (or per tenant/user id). Simple, explainable, auditable.
3. **Do legitimate clients burst?** (page load fires 8 requests at once, then nothing for a minute) → Token bucket. It absorbs the burst and still caps the average.
4. **Is a boundary burst a real problem?** (a client doubling its quota by straddling two windows) → Sliding window.
5. **Are you just stopping runaway clients from taking the API down?** → A generous global limiter, plus per-key limits on the expensive endpoints.

Most production APIs end up with two layers: a generous global limiter as a safety net, and per-key limits on the endpoints that cost real money or real compute.

## The 429 contract

Whatever limiter you pick, a rejection must answer three questions:

1. **What happened?** — status `429`, and a body that says so in plain words.
2. **When can I retry?** — a `Retry-After` header (seconds, or an HTTP date). The demo's `OnRejected` handler sets this from the limiter's own `RetryAfter` metadata. A 429 without it leaves clients guessing — and guessing clients retry immediately, which is the opposite of what you want.
3. **Am I close to the limit?** — optionally, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, and `X-RateLimit-Reset` headers on *successful* responses too, so well-behaved clients can back off before they ever see a 429. ASP.NET Core's built-in middleware does not emit these; add them yourself with a small middleware that reads the limiter's lease metadata.

Also: **never queue unlimited requests**. A `QueueLimit` of 0 (reject immediately) is usually right for an API. Queuing just moves the overload from your endpoint to your memory.

## What changes at scale

The built-in limiters are **in-memory, per process**. That is fine for one server and for learning. It breaks in two ways when you scale out:

- **Buckets don't sync across servers.** Two instances each allow 100/minute, so a client behind a load balancer effectively gets 200/minute. Usually acceptable; if it isn't, move the counters to Redis (fixed/sliding windows map cleanly onto Redis sorted sets or the `INCR` + `EXPIRE` pattern).
- **Partition keys must identify the caller, not the machine.** Behind a load balancer or API gateway, `RemoteIpAddress` is the gateway's IP — every client shares one bucket. Partition by API key, user id, or tenant id, and if you do need the real IP, read it from `X-Forwarded-For` with `ForwardedHeadersMiddleware` configured to trust your proxy.

## Common mistakes

- **Limiting by IP behind a proxy** — see above; one bucket for the whole internet.
- **A shared bucket for all clients** — one greedy client eats everyone's quota. Partition per caller.
- **429 with no Retry-After** — clients retry immediately and hammer harder.
- **QueueLimit set high** — a queue of 1,000 turns a 2-second spike into a 30-second latency tail.
- **Limiting the health-check endpoint** — your load balancer's health probe gets 429'd and the instance gets killed. Exclude `/healthz` (or keep a global limiter generous enough that probes never trip it).
- **Testing only the happy path** — a limiter you never triggered in tests is a limiter you don't understand. The tests in this repo assert exact allow counts, rejection, refill, and reset; steal that pattern.
