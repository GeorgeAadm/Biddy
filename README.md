# Biddy

A small .NET example of processing auction bids through a `Channel`, kept deliberately small so it reads in a few minutes. There is no auth, persistence, or auction lifecycle. See [What's missing](#whats-missing).

Every `POST /auctions/{id}/bids` is put on a bounded channel. A single background `BidProcessor` reads bids one at a time, keeps the highest bid per auction, deduplicates retries by idempotency key, and reports the outcome back to the waiting request.

| Response | Meaning |
|---|---|
| `200 OK` | Bid accepted: it's the new highest bid |
| `409 Conflict` | Bid rejected: equal to or lower than the current highest bid |
| `202 Accepted` | Still queued after 5 seconds. It will be processed; retry with the same `Idempotency-Key` to get the outcome |
| `400 Bad Request` | Missing or overlong `Idempotency-Key`, amount not positive, or email invalid |
| `404 Not Found` | No such auction |
| `422 Unprocessable Entity` | The `Idempotency-Key` was already used for a different bid |
| `503 Service Unavailable` | Queue full, or the app is shutting down |

A replayed response has the original status and body, plus an `Idempotent-Replayed: true` header.

## Endpoints

| Method | Route | |
|---|---|---|
| `GET` | `/auctions` | List auctions with their current highest bid |
| `GET` | `/auctions/{id}` | One auction with its current highest bid |
| `POST` | `/auctions/{id}/bids` | Place a bid. Requires an `Idempotency-Key` header (max 255 characters). Body: `{"amountInCents": 5000, "userEmail": "you@test.local"}` |

Two auctions are seeded at startup:

- `11111111-1111-1111-1111-111111111111`: Cannondale racing bicycle
- `22222222-2222-2222-2222-222222222222`: Gibson acoustic guitar

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `curl`, `xargs`, `sort` and `uniq` for the manual load test (included in Git Bash on Windows)

## Run the app

```bash
dotnet run --project src/Biddy.Api --launch-profile http
```

The API listens on `http://localhost:5138`.

Place a bid. The key can be any unique string; use a new one for each new bid:

```bash
curl -i -H "Content-Type: application/json" \
  -H "Idempotency-Key: bid-001" \
  -d '{"amountInCents":5000,"userEmail":"bidder@test.local"}' \
  http://localhost:5138/auctions/11111111-1111-1111-1111-111111111111/bids
```

Run the same command again and you get the same `200` with `Idempotent-Replayed: true`, not a `409`. Change the amount but keep the key and you get `422`.

See the current highest bid:

```bash
curl http://localhost:5138/auctions/11111111-1111-1111-1111-111111111111
```

## Tests

```bash
dotnet test
```

The tests start the app in-process with `WebApplicationFactory`, each with fresh state. They check that:

- 50 concurrent equal bids produce exactly one `200` and 49 `409`s.
- 500 concurrent random bids never accept the same amount twice, every rejection was against a bid at least as high, and the auction ends on the maximum amount.
- A retry with the same key replays the original result, including after the bidder has since been outbid.
- 20 concurrent requests with the same key produce one bid and 19 replays.
- Reusing a key for a different bid returns `422` and doesn't change the auction.
- Missing or overlong keys and invalid bids get `400`; unknown auctions get `404`.

### Manual load test

With the app freshly started:

```bash
seq 50 | xargs -P 50 -I{} curl -s -o /dev/null -w "%{http_code}\n" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: load-{}" \
  -d '{"amountInCents":5000,"userEmail":"bidder{}@test.local"}' \
  http://localhost:5138/auctions/11111111-1111-1111-1111-111111111111/bids | sort | uniq -c
```

Expected:

```
      1 200
     49 409
```

Run it a second time without restarting and you get the same counts, because every request is a replay of its original outcome. Change the key prefix (`load2-{}`) and all 50 get `409`, since the first run's winning bid is still there.

## Design decisions

**Why a channel and not a lock?** For one in-memory number, a `lock` or a compare-and-swap would be simpler. The channel earns its place once processing does real work: writing the bid to a database, publishing an event, notifying the outbid bidder. That work can be async and slow, and the single reader still guarantees bids are applied one at a time, in arrival order, with no locks held across I/O. In this demo the loop is where that work would go.

**Single writer.** Only `BidProcessor` changes auction state. HTTP handlers read the highest bid through `Volatile.Read`, so they always see a complete `WinningBid`, never a half-written one.

**Idempotency in the processor.** Checking whether a key has been seen and recording the outcome happen in the single-writer loop, so there's no race between concurrent retries and no locking. Both accepted and rejected outcomes are stored, so a retry gets what actually happened to its bid, not a fresh decision. Without this, a winner whose `200` was lost in transit would retry and get `409` from their own bid. Keys are stored per auction and freed with it. Reusing a key for a different amount or email returns `422`, following the IETF Idempotency-Key draft.

**Bounded queue.** When the queue is full, the API returns `503` immediately instead of letting memory grow without limit under a flood.

**A queued bid is a commitment.** If the client disconnects after its bid is queued, the bid is still processed. Retrying with the same key returns the outcome.

**Auction ID in the route.** The auction is the resource being bid on. Auth, when added, would identify the bidder and replace `userEmail` in the body. It doesn't change the route.

## What changes at scale

- **Persistence.** State is in memory, so a restart loses everything. The simplest durable version pushes the concurrency into the database with a conditional update: `UPDATE auctions SET high_bid = @amount ... WHERE id = @id AND high_bid < @amount`, then check the rows affected. That works across any number of app instances.
- **Durable idempotency.** A unique constraint on `(auction_id, idempotency_key)`, written in the same transaction as the bid, so a key and its outcome are stored together or not at all.
- **One lane per auction.** Contention only matters within an auction. Partitioning by auction ID (a channel per auction, sharded consumers, or actors such as Orleans grains) keeps one busy auction from delaying the others.
- **Multiple instances.** With in-memory state, two instances would mean two separate auctions. Either the database handles concurrency (above) or bids are routed by auction ID to the instance that owns that auction.

## What's missing

Left out on purpose to keep this small: authentication, auction start and end times, reserve prices, minimum bid increments, and rules about outbidding yourself.