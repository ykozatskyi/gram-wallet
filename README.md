# GRAM Wallet

Personal [TON](https://ton.org) wallet for one Wallet V5R1 account. You keep the mnemonic. The service signs transfers locally, exposes a small HTTP API, and gives the admin a Telegram UI.

Use it to pay people straight from your own wallet: queue many payouts over the API, or send one withdrawal from Telegram. You pay the TON network fee. There is no exchange withdrawal fee, because the coins never sit on an exchange account.

Balances and amounts in this service are the native coin, labeled **GRAM**. `1 GRAM = 1_000_000_000` nano. The API accepts a payout immediately. A background worker signs and broadcasts the next one within a couple of seconds, then waits for the chain to confirm it.

The HTTP API listens on `http://127.0.0.1:5100` only.

## What it does

- Shows the wallet address, balance, and recent transactions.
- Queues GRAM payouts from your own services through `POST /api/gram/payouts`.
- Sends one transfer at a time, in order, and confirms it on-chain before taking the next one.
- Retries a broadcast and re-signs when the message expires. A payout that cannot be matched safely goes to `NeedsReview` instead of being sent twice.
- Notifies the Telegram admin about queued payouts, failures, and incoming deposits.
- Lets that same admin check the wallet, withdraw, pause payouts, and toggle success alerts from a private chat.

`requestId` is an idempotency key. Sending the same payout twice returns the existing record. Sending the same id with different details is rejected.

## Stack

| Piece | Used for |
| --- | --- |
| .NET 10, ASP.NET Core | HTTP API, hosted workers, configuration |
| PostgreSQL, EF Core 10, Npgsql | Payout queue, settings, EF migrations applied on startup |
| [Ton.Contracts](https://www.nuget.org/packages/Ton.Contracts) 0.2.7 | Wallet V5R1 key derivation and signing |
| [TON Center API v3](https://toncenter.com/api/v3/) | Balance, transactions, seqno, broadcast |
| [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) | Admin UI and payout alerts |
| [TON Viewer](https://tonviewer.com) | Explorer links in API responses and Telegram |

Signing checks that the mnemonic derives the configured address and that the on-chain account is an active V5R1 wallet with the expected code hash. Only one process holds the wallet lock, so two copies cannot sign at the same time.

## Requirements

- .NET 10 SDK
- PostgreSQL database named `GRAMWalletDB`
- A Wallet V5R1 address on mainnet or testnet
- The 24-word TON mnemonic for that wallet, if you want signing. Without it the API can still read address, balance, and transactions. Payout creation returns `503`.
- A Telegram bot token and your numeric Telegram user id, if you want the bot. The user id is required even when the bot token is empty.

## Configure

Copy `.env.example` to `.env` in the project directory, or export the same variables in the environment. Real environment variables win over `.env`. Do not commit `.env`.

| Variable | Required | Meaning |
| --- | --- | --- |
| `TON_NETWORK` | yes | `mainnet` or `testnet` |
| `TON_WALLET_ADDRESS` | yes | Friendly or raw address of the V5R1 wallet. Must match `TON_NETWORK`. |
| `TON_MNEMONIC` | for payouts | 24-word TON phrase. Must derive `TON_WALLET_ADDRESS`. Leave empty to disable signing. |
| `TON_API_KEY` | no | TON Center key, sent as `X-API-Key`. Without it you sit on the public rate limit. |
| `API_KEY` | yes | 16–256 characters, no spaces. Clients send it as `X-Api-Key`. |
| `TELEGRAM_BOT_TOKEN` | no | Bot token from [@BotFather](https://t.me/BotFather). Empty disables the bot. Alerts then go to the log. |
| `TELEGRAM_ADMIN_ID` | yes | Numeric Telegram user id of the only person allowed to use the bot. |
| `GRAM_PAYOUT_ENABLED` | no | `true` or `false`. Default `false`, so the first start keeps payouts paused until the admin resumes them. Ignored after settings already exist in Postgres. |
| `GRAM_MAX_PAYOUT` | no | Largest single payout in GRAM. Default `10`. Up to 9 decimal places. |
| `GRAM_MIN_RESERVE` | no | GRAM that must stay on the wallet. Default `0.1`. Queued payouts also reserve `0.05` GRAM each for the network fee. |
| `DATABASE_CONNECTION_STRING` | yes | Npgsql connection string. `Database` must be `GRAMWalletDB`. |

Example:

```env
TELEGRAM_BOT_TOKEN=
TELEGRAM_ADMIN_ID=123456789

TON_NETWORK=mainnet
TON_API_KEY=
TON_WALLET_ADDRESS=

API_KEY=replace-with-at-least-16-chars

TON_MNEMONIC=

GRAM_PAYOUT_ENABLED=false
GRAM_MAX_PAYOUT=10
GRAM_MIN_RESERVE=0.1

DATABASE_CONNECTION_STRING=Host=localhost;Port=5432;Database=GRAMWalletDB;Username=postgres;Password=postgres;Pooling=true
```

Create the database, then run:

```bash
dotnet run
```

Startup applies EF migrations. A ready process logs that it is listening on `http://127.0.0.1:5100`.

## API

`/health` is open. Everything under `/api` needs the header `X-Api-Key`. Comparison is constant-time. A bad or missing key is `401`:

```json
{ "error": "Unauthorized." }
```

Errors use that same `{ "error": "..." }` body. JSON is camelCase. Null fields are omitted.

### `GET /health`

No API key.

```json
{ "status": "ok" }
```

### `GET /api/gram/address`

```json
{
  "address": "UQ...",
  "bounceableAddress": "EQ...",
  "rawAddress": "0:...",
  "network": "mainnet",
  "explorerUrl": "https://tonviewer.com/UQ..."
}
```

`address` is the non-bounceable form. Use that when sending GRAM to this wallet.

### `GET /api/gram/balance`

```json
{
  "address": "UQ...",
  "network": "mainnet",
  "balance": "12.5",
  "balanceNano": "12500000000",
  "status": "active",
  "initialized": true,
  "retrievedAtUtc": "2026-11-19T18:00:00+00:00"
}
```

`status` comes from TON Center (`active`, `uninit`, `nonexist`, and so on).

### `GET /api/gram/transactions?limit=10&offset=0`

`limit` is 1–50, default 10. `offset` is zero or greater.

```json
{
  "limit": 10,
  "offset": 0,
  "hasMore": false,
  "transactions": [
    {
      "hash": "...",
      "logicalTime": "117755981000010",
      "timestampUtc": "2026-11-19T18:00:00+00:00",
      "fee": "0.0005",
      "feeNano": "500000",
      "aborted": false,
      "origStatus": "active",
      "endStatus": "active",
      "explorerUrl": "https://tonviewer.com/transaction/...",
      "messages": [
        {
          "direction": "incoming",
          "amount": "1.5",
          "amountNano": "1500000000",
          "counterparty": "UQ...",
          "opcode": "text_comment",
          "bounced": false
        }
      ]
    }
  ]
}
```

`direction` is `incoming`, `outgoing`, or `other`.

### `POST /api/gram/payouts`

Body limit is 4 KB. Amount is a decimal string or a JSON number, up to 9 decimal places. `comment` is optional plain text, at most 123 UTF-8 bytes. It is stored on the transfer as a TON text comment.

```bash
curl -sS -X POST http://127.0.0.1:5100/api/gram/payouts \
  -H "X-Api-Key: $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"requestId\":\"8f1c2a10-6c3e-4a7b-9d2e-1b0c4f5a6e7d\",\"address\":\"UQ...\",\"amount\":\"1.25\",\"comment\":\"payout 42\"}"
```

`202 Accepted`:

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Queued"
}
```

`Location` is `/api/gram/payouts/{id}`.

The worker will not sign while payouts are paused, while another payout is still in flight, or while the on-chain wallet check fails. Creation itself only queues the row when the spendable balance covers the amount, the `0.05` GRAM fee reserve, and `GRAM_MIN_RESERVE`.

| Status | When |
| --- | --- |
| `400` | Bad JSON, `requestId` is not a UUID, address or amount is invalid, amount is above `GRAM_MAX_PAYOUT`, comment is too long, or the destination is this same wallet. |
| `401` | Missing or wrong `X-Api-Key`. |
| `409` | `requestId` already belongs to a different payout, or the wallet cannot cover this payout. |
| `429` | TON Center rate limit. |
| `502` | TON Center rejected the balance read. |
| `503` | Signing is not configured, or the wallet failed its local or on-chain check. |
| `504` | TON Center timed out. |

### `GET /api/gram/payouts/{id}`

`id` is the payout UUID, not `requestId`.

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "requestId": "8f1c2a10-6c3e-4a7b-9d2e-1b0c4f5a6e7d",
  "address": "UQ...",
  "amount": "1.25",
  "comment": "payout 42",
  "status": "Confirmed",
  "transactionHash": "...",
  "explorerUrl": "https://tonviewer.com/transaction/...",
  "attempts": 1,
  "lastError": null,
  "createdAtUtc": "2026-11-19T18:00:00+00:00",
  "updatedAtUtc": "2026-11-19T18:00:04+00:00",
  "confirmedAtUtc": "2026-11-19T18:00:04+00:00"
}
```

Unknown id is `404`. A non-UUID id is `400`.

### `GET /api/gram/payouts?limit=20&offset=0`

`limit` is 1–50, default 20. The body is `{ limit, offset, hasMore, payouts }` and each item matches the single-payout object.

### Payout statuses

| Status | Meaning |
| --- | --- |
| `Queued` | Stored. Waiting for the worker. |
| `Signed` | External message is signed. |
| `Broadcasted` | Sent to TON Center. Waiting for the account transaction. |
| `Confirmed` | The outgoing transfer was found and matched, including the comment. |
| `Failed` | Terminal failure. `lastError` says why. |
| `NeedsReview` | The chain state does not safely match this payout. Nothing else is signed until this is resolved. |

A message is valid for 180 seconds. The worker can re-sign up to 3 times. It does not start a second payout while one is `Signed`, `Broadcasted`, or otherwise in flight.

## Telegram

Set `TELEGRAM_BOT_TOKEN` and open a private chat with the bot. Anyone else gets "This bot is private." Group chats are ignored except for a pointer to the private chat.

The menu shows network, address, and balance.

| Action | What it does |
| --- | --- |
| Address | Non-bounceable and bounceable forms, plus a TON Viewer link. |
| Balance | GRAM balance and account status. |
| Transactions | Pages of 10, with counterparties and amounts. |
| Withdraw | Address, amount, optional memo, then an explicit confirm. Uses the same payout queue and limits as the API. |
| Payouts | Recent payouts and their statuses. |
| Payout settings | Pause, resume, and toggle success alerts. |
| Refresh | Reloads the menu. |

Commands: `/start`, `/address`, `/balance`, `/transactions`, `/payouts`, `/settings`.

Incoming deposits are watched about every 15 seconds. New ones are pushed to the admin when the bot is configured.

## Safety

- The mnemonic, bot token, API key, and database password stay in the environment. They are not in `appsettings.json`.
- Logs redact configured secrets.
- The API is bound to localhost. Reach it from another machine with an [SSH tunnel](#ssh-tunnel), or put a reverse proxy in front of it. Keep `X-Api-Key` on that hop.
- `GRAM_PAYOUT_ENABLED=false` on a fresh database leaves payouts paused. Resume them from Telegram after you have checked the address and the max payout.
- Treat `NeedsReview` as a stop. Do not send that payout again until you have looked at the wallet on TON Viewer.

## SSH tunnel

The API listens on `127.0.0.1:5100` of the machine that runs it. A local port forward over SSH lets you call that port from your own computer. The connection stays inside SSH. You still send `X-Api-Key`.

OpenSSH is enough. The same command works on Linux, macOS, and Windows once the OpenSSH client is installed.

```bash
ssh -N -L 5100:127.0.0.1:5100 user@host
```

| Piece | Meaning |
| --- | --- |
| `user` | SSH account on the machine that runs the wallet. |
| `host` | That machine's hostname or IP. |
| `-N` | Keep the tunnel only. Do not open a remote shell. |
| `-L 5100:127.0.0.1:5100` | Your local port `5100` goes to `127.0.0.1:5100` on the remote machine, where the API is bound. |

Leave the command running, then call the API as if it were local:

```bash
curl -sS http://127.0.0.1:5100/health
curl -sS http://127.0.0.1:5100/api/gram/balance -H "X-Api-Key: $API_KEY"
```

If local port `5100` is already in use, pick another local port. The remote side stays `5100`:

```bash
ssh -N -L 15100:127.0.0.1:5100 user@host
curl -sS http://127.0.0.1:15100/health
```

SSH on a port other than 22:

```bash
ssh -N -p 2222 -L 5100:127.0.0.1:5100 user@host
```

A specific private key:

```bash
ssh -N -i ~/.ssh/id_ed25519 -L 5100:127.0.0.1:5100 user@host
```

Add `-f` to send the tunnel to the background after it connects. Stop it later by ending that `ssh` process. `Ctrl+C` stops a tunnel that is still in the foreground.

## Production

GitHub Actions on `main` publishes the app and restarts it on a host over SSH. `EC2_HOST`, `EC2_PORT`, `EC2_USER`, and `EC2_SSH_KEY` are Actions secrets. The process runs as `gram-wallet` from `/opt/gram-wallet` and reads `/env/gram-wallet.env`. The health check is `GET http://127.0.0.1:5100/health` on that machine.
