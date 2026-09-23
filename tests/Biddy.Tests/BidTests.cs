using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Biddy.Tests;

// Each test builds its own factory, so each starts with fresh in-memory auctions.
public class BidTests
{
    private static readonly string BicycleUrl = $"/auctions/{SeedAuctions.Bicycle}";
    private static readonly string BicycleBidsUrl = $"{BicycleUrl}/bids";

    // Every bid needs an Idempotency-Key; a fresh one is generated unless the test supplies one.
    private static Task<HttpResponseMessage> PostBid(
        HttpClient client, string url, BidRequest bid, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(bid) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    // --- Ordering and concurrency ---

    [Fact]
    public async Task Equal_concurrent_bids_accept_exactly_one()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            PostBid(client, BicycleBidsUrl, new BidRequest(5000, $"bidder{i}@test.local"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(49, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Concurrent_random_bids_end_on_the_maximum()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var amounts = Enumerable.Range(0, 500).Select(_ => Random.Shared.Next(1, 1_000_000)).ToArray();

        var results = await Task.WhenAll(amounts.Select(async (amount, i) =>
        {
            var response = await PostBid(client, BicycleBidsUrl, new BidRequest(amount, $"bidder{i}@test.local"));
            var body = await response.Content.ReadFromJsonAsync<BidResult>();
            return (Amount: amount, response.StatusCode, Body: body!);
        }));

        Assert.All(results, r =>
            Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));

        // Accepted bids form a strictly increasing sequence, so no amount is accepted twice.
        var accepted = results.Where(r => r.StatusCode == HttpStatusCode.OK).Select(r => r.Amount).ToList();
        Assert.NotEmpty(accepted);
        Assert.Equal(accepted.Count, accepted.Distinct().Count());

        // A rejected bid must have been rejected by something at least as high.
        Assert.All(results.Where(r => r.StatusCode == HttpStatusCode.Conflict), r =>
            Assert.True(r.Body.HighestAmountInCents >= r.Amount));

        var auction = await client.GetFromJsonAsync<AuctionView>(BicycleUrl);
        Assert.Equal(amounts.Max(), auction!.HighestAmountInCents);
    }

    // --- Idempotency ---

    [Fact]
    public async Task Retry_with_same_key_replays_original_result()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var bid = new BidRequest(5000, "a@test.local");

        var first = await PostBid(client, BicycleBidsUrl, bid, key: "retry-1");
        var second = await PostBid(client, BicycleBidsUrl, bid, key: "retry-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // not 409: the winner is still told they won
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var firstBody = await first.Content.ReadFromJsonAsync<BidResult>();
        var secondBody = await second.Content.ReadFromJsonAsync<BidResult>();
        Assert.Equal(firstBody!.BidId, secondBody!.BidId);
    }

    [Fact]
    public async Task Replay_returns_the_stored_outcome_even_after_being_outbid()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        await PostBid(client, BicycleBidsUrl, new BidRequest(5000, "a@test.local"), key: "a-1");
        await PostBid(client, BicycleBidsUrl, new BidRequest(6000, "b@test.local"), key: "b-1");

        // A's retry reports what happened to A's bid, not a fresh decision against B's.
        var replay = await PostBid(client, BicycleBidsUrl, new BidRequest(5000, "a@test.local"), key: "a-1");
        var body = await replay.Content.ReadFromJsonAsync<BidResult>();

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(5000, body!.HighestAmountInCents);

        var auction = await client.GetFromJsonAsync<AuctionView>(BicycleUrl);
        Assert.Equal(6000, auction!.HighestAmountInCents);
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_produce_one_bid()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var bid = new BidRequest(5000, "a@test.local");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            PostBid(client, BicycleBidsUrl, bid, key: "same-key")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(19, responses.Count(r => r.Headers.Contains("Idempotent-Replayed")));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<BidResult>()));
        Assert.Single(bodies.Select(b => b!.BidId).Distinct());
    }

    [Fact]
    public async Task Same_key_with_different_bid_returns_422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        await PostBid(client, BicycleBidsUrl, new BidRequest(5000, "a@test.local"), key: "reused");
        var response = await PostBid(client, BicycleBidsUrl, new BidRequest(9000, "a@test.local"), key: "reused");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // The conflicting bid must not have been applied.
        var auction = await client.GetFromJsonAsync<AuctionView>(BicycleUrl);
        Assert.Equal(5000, auction!.HighestAmountInCents);
    }

    [Fact]
    public async Task Missing_key_returns_400()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(BicycleBidsUrl, new BidRequest(5000, "a@test.local"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_key_returns_400()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await PostBid(
            client, BicycleBidsUrl, new BidRequest(5000, "a@test.local"), key: new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Validation and routing ---

    [Theory]
    [InlineData(0, "a@test.local")]
    [InlineData(-500, "a@test.local")]
    [InlineData(5000, "")]
    [InlineData(5000, "not-an-email")]
    public async Task Invalid_bids_are_rejected(int amount, string email)
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await PostBid(client, BicycleBidsUrl, new BidRequest(amount, email));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_auction_returns_404()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await PostBid(client, $"/auctions/{Guid.NewGuid()}/bids", new BidRequest(5000, "a@test.local"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
