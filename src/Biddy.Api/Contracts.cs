using System.Net.Mail;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<BidOutcome>))]
public enum BidOutcome
{
    Accepted,
    Outbid
}
public sealed record PlaceBidRequest(
    Guid id,
    int amountInCents,
    string userEmail,
    string message,
    TaskCompletionSource<BidDecision> complete);
    
public sealed record BidRequest(int AmountInCents, string? UserEmail)
{
    public Dictionary<string, string[]>? Validate()
    {
        var errors = new Dictionary<string, string[]>();
 
        if (AmountInCents <= 0)
            errors["amountInCents"] = ["Must be greater than zero."];
 
        if (string.IsNullOrWhiteSpace(UserEmail) || !MailAddress.TryCreate(UserEmail.Trim(), out _))
            errors["userEmail"] = ["Must be a valid email address."];
 
        return errors.Count == 0 ? null : errors;
    }
}

public sealed record WinningBid(
    Guid Id, 
    int AmountInCents, 
    string UserEmail, 
    DateTime AcceptedAtUtc);

public sealed record BidResult(
    Guid BidId,
    Guid AuctionId,
    BidOutcome Outcome,
    int HighestAmountInCents,
    DateTime ProcessedAtUtc);

public abstract record BidDecision;
public sealed record BidProcessed(BidResult Result, bool Replayed) : BidDecision;
public sealed record IdempotencyKeyConflict : BidDecision;

internal sealed record ProcessedBid(int AmountInCents, string UserEmail, BidResult Result)
{
    public bool Matches(PlaceBid bid) =>
        AmountInCents == bid.AmountInCents &&
        string.Equals(UserEmail, bid.UserEmail, StringComparison.OrdinalIgnoreCase);
}

// Public View of auction - no winner email
public sealed record AuctionView(
    Guid Id,
    string Title,
    Guid? HighestBidId,
    int? HighestAmountInCents,
    DateTime? HighestAcceptedAtUtc)
{
    public static AuctionView From(AuctionState auction)
    {
        var highest = auction.Highest; // read once 
        return new AuctionView(
            auction.Id,
            auction.Title,
            highest?.Id,
            highest?.AmountInCents,
            highest?.AcceptedAtUtc);
    }
}