using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RateLimitingDemo.Tests;

/// <summary>
/// Each test spins up its own app instance so limiter state never leaks
/// between tests. That is the whole point of a rate limiter — state — so
/// sharing one factory across tests would make every test order-dependent.
/// </summary>
public class RateLimitingTests
{
    private static WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>();

    private static async Task<HttpStatusCode[]> GetManyAsync(
        HttpClient client, string path, int count, string? apiKey = null)
    {
        var results = new HttpStatusCode[count];
        for (var i = 0; i < count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (apiKey is not null)
            {
                request.Headers.Add("X-Api-Key", apiKey);
            }
            using var response = await client.SendAsync(request);
            results[i] = response.StatusCode;
        }
        return results;
    }

    [Fact]
    public async Task FixedWindow_AllowsFiveThenReturns429()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var statuses = await GetManyAsync(client, "/fixed", 6);

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK,
                    HttpStatusCode.OK, HttpStatusCode.OK,
                    HttpStatusCode.TooManyRequests },
            statuses);
    }

    [Fact]
    public async Task FixedWindow_RejectionIncludesRetryAfterHeader()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        await GetManyAsync(client, "/fixed", 5);
        using var rejected = await client.GetAsync("/fixed");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"),
            "A 429 without Retry-After leaves the client guessing when to retry.");

        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("TooManyRequests", body);
    }

    [Fact]
    public async Task FixedWindow_ResetsAfterWindowElapses()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        await GetManyAsync(client, "/fixed", 5);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.GetAsync("/fixed")).StatusCode);

        // The window is 10 seconds; wait it out plus a safety margin.
        await Task.Delay(TimeSpan.FromSeconds(11));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/fixed")).StatusCode);
    }

    [Fact]
    public async Task SlidingWindow_AllowsTenThenReturns429()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var statuses = await GetManyAsync(client, "/sliding", 11);

        Assert.Equal(HttpStatusCode.OK, statuses[9]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task TokenBucket_AllowsBurstThenRefillsOverTime()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var statuses = await GetManyAsync(client, "/token-bucket", 11);
        Assert.Equal(HttpStatusCode.OK, statuses[9]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);

        // Refill is 1 token/second; after 3 seconds at least one request
        // must succeed again. This is the behaviour that makes a token
        // bucket different from a fixed window.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/token-bucket")).StatusCode);
    }

    [Fact]
    public async Task PerKey_BucketsAreIndependentPerApiKey()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Exhaust alice's bucket completely.
        var alice = await GetManyAsync(client, "/per-key", 6, apiKey: "alice");
        Assert.Equal(HttpStatusCode.OK, alice[4]);
        Assert.Equal(HttpStatusCode.TooManyRequests, alice[5]);

        // Bob has a fresh bucket — alice's traffic must not affect him.
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/per-key")
            {
                Headers = { { "X-Api-Key", "bob" } }
            })).StatusCode);

        // And anonymous requests share their own bucket, not alice's.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/per-key")).StatusCode);
    }

    [Fact]
    public async Task Concurrency_LimitsParallelRequests()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // 5 requests at once against 2 permits + 1 queued slot:
        // 3 succeed, 2 are rejected immediately.
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.GetAsync("/concurrency"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        var statuses = responses.Select(r => r.StatusCode).ToArray();

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Unlimited_NeverRateLimits()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var statuses = await GetManyAsync(client, "/unlimited", 10);
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Fact]
    public async Task Root_ListsEveryPolicy()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/fixed", body);
        Assert.Contains("/token-bucket", body);
        Assert.Contains("/per-key", body);
    }
}
