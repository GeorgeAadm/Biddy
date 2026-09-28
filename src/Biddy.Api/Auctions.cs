using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public sealed class AuctionState(Guid id, string title)
{
    // Touched only by BidProcessor
    private readonly Dictionary<string, ProcessedBid> _processed = new(StringComparer.Ordinal);
    private WinningBid? _highest;

    public Guid Id { get; } = id;
    public string Title { get; } = title;

    public WinningBid? Highest => Volatile.Read(ref _highest);
    public void SetHighest(WinningBid bid) => Volatile.Write(ref _highest, bid);
    
    internal bool TryGetProcessed(string key, [NotNullWhen(true)] out ProcessedBid? processed) =>
        _processed.TryGetValue(key, out processed);
    internal void RecordProcessed(string key, ProcessedBid processed) =>
        _processed[key] = processed;

}

public sealed class AuctionStore(IEnumerable<AuctionState> auctions)
{
    private readonly ConcurrentDictionary<Guid, AuctionState> _auctions = 
        new(auctions.Select(a => KeyValuePair.Create(a.Id, a)));
 
    public IEnumerable<AuctionState> All => _auctions.Values;
 
    public bool TryGet(Guid id, [NotNullWhen(true)] out AuctionState? auction) =>
        _auctions.TryGetValue(id, out auction);
}

// ---  Fixed IDs so the README's curl commands work as-is.
public static class SeedAuctions
{
    public static readonly Guid Bicycle = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Guitar = Guid.Parse("22222222-2222-2222-2222-222222222222");
 
    public static IEnumerable<AuctionState> Create() =>
    [
        new AuctionState(Bicycle, "Cannondale racing bicycle"),
        new AuctionState(Guitar, "Gibson acoustic guitar"),
    ];
}