# Payment Service (Go)

Production-style payment service written in **Go**, with message queues, idempotency, and audit logging. Drop-in replacement for `NavalArchive.PaymentSimulation` (same API surface).

## Why Go?

- **High throughput** – goroutines and efficient I/O
- **Single binary** – no runtime, easy deployment
- **Strong typing** – fewer runtime errors in financial flows
- **Common in fintech** – Stripe, many payment processors use Go

## Features

- **Message queue (RabbitMQ)** – async payment processing
- **Idempotency** – prevents double charges on retries
- **PostgreSQL / SQLite** – transaction storage
- **Retry with exponential backoff** – for transient failures
- **Health checks** – `/health` with DB ping
- **Status polling** – `GET /api/payment/status/:id` when using async mode

## Quick Start (sync mode, no queue)

```bash
cd payment-service
go build -o payment-service ./cmd/server
./payment-service
```

Runs on port 5001. Same API as PaymentSimulation:

```bash
curl -X POST http://localhost:5001/api/payment/simulate \
  -H "Content-Type: application/json" \
  -d '{"amount": 25.00, "currency": "USD", "cardId": "CARD123"}'
```

## Production mode (with RabbitMQ)

```bash
# Start RabbitMQ (Docker)
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3-management

# Run payment service with queue
USE_QUEUE=true RABBITMQ_URL=amqp://guest:guest@localhost:5672/ ./payment-service
```

When `USE_QUEUE=true`:

- `POST /api/payment/simulate` returns **202 Accepted** with `transactionId`
- Client polls `GET /api/payment/status/:transactionId` for result

## Environment

| Variable        | Default                    | Description                    |
|-----------------|----------------------------|--------------------------------|
| HTTP_PORT       | 5001                       | Server port                    |
| DATABASE_URL    | payment.db                 | SQLite path or Postgres DSN    |
| DATABASE_TYPE   | sqlite / postgres          | Database driver                |
| RABBITMQ_URL    | amqp://guest:guest@localhost:5672/ | RabbitMQ connection     |
| QUEUE_NAME      | payment_intents            | Queue name                     |
| USE_QUEUE       | false                      | Use async queue processing     |
| IDEMPOTENCY     | memory                     | memory, db, or redis           |

## Project layout

```
payment-service/
├── cmd/server/          # Main entry, HTTP server
├── config/              # Config from env
├── internal/
│   ├── handler/         # HTTP handlers (simulate, status, health)
│   ├── idempotency/     # Idempotency store (memory/db)
│   ├── processor/      # Payment processing logic
│   ├── queue/           # RabbitMQ publish/consume, retry
│   └── store/          # PostgreSQL/SQLite persistence
├── go.mod
└── README.md
```
