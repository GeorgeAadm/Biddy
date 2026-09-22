var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new AuctionStore(SeedAuctions.Create()));
builder.Services.AddSingleton(_ => new BidQueue(capacity: 10_000));
builder.Services.AddHostedService<BidProcessor>();


var app = builder.Build();

var resultTimeout = TimeSpan.FromSeconds(5);

app.MapGet("/auctions", (AuctionStore store) =>
    store.All.Select(AuctionView.From));
 
app.MapGet("/auctions/{auctionId:guid}", (Guid auctionId, AuctionStore store) =>
    store.TryGet(auctionId, out var auction)
        ? Results.Ok(AuctionView.From(auction))
        : Results.NotFound());

app.MapPost("/auctions/{auctionId:guid}/bids", async (
    Guid auctionId,
    [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    BidRequest request,
    AuctionStore store,
    BidQueue queue,
    TimeProvider time,
    CancellationToken ct) =>
{
    if (!store.TryGet(auctionId, out var auction))
        return Results.NotFound();

    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 255)
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "An Idempotency-Key header (max 255 characters) is required.");
 
    if (request.Validate() is { } errors)
        return Results.ValidationProblem(errors);
 
    var bid = PlaceBid.Create(auction, idempotencyKey, request.AmountInCents, request.UserEmail!.Trim());
 
    // Queue full or shutting down -> not growing memory without limit.
    if (!queue.TryEnqueue(bid))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
 
    // Once queued, if client disconnects, it is still processed.
    try
    {
        var decision = await bid.Completion.Task.WaitAsync(resultTimeout, time, ct);

        switch (decision)
        {
            case IdempotencyKeyConflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "This Idempotency-Key was already used for a different bid.");

            case BidProcessed { Result: var result, Replayed: var replayed }:
                IResult response = result.Outcome == BidOutcome.Accepted
                    ? Results.Ok(result)
                    : Results.Conflict(result);
                return (replayed) ? new ReplayedResult(response) : response;

            default:
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
    catch (TimeoutException)
    {
        // Still queued + will be processed. client can check the auction's highest bid.
        return Results.Accepted($"/auctions/{auctionId}", new { bid.BidId, Status = "pending" });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // processor cancelled this bid / shutdown
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});


app.Run();

sealed class ReplayedResult(IResult inner) : IResult
{
    public Task ExecuteAsync(HttpContext http)
    {
        http.Response.Headers.Append("Idempotent-Replayed", "true");
        return inner.ExecuteAsync(http);
    }
}