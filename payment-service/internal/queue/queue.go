package queue

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

type PaymentIntent struct {
	TransactionID  string  `json:"transaction_id"`
	AccountID      string  `json:"account_id,omitempty"`
	PaymentToken   string  `json:"payment_token,omitempty"`
	CardID         string  `json:"card_id,omitempty"`
	Amount         float64 `json:"amount"`
	Currency       string  `json:"currency"`
	Description    string  `json:"description"`
	IdempotencyKey string  `json:"idempotency_key,omitempty"`
	RetryCount     int     `json:"retry_count"`
}

type Queue interface {
	Publish(ctx context.Context, intent *PaymentIntent) error
	Consume(ctx context.Context, handler func(*PaymentIntent) error) error
	Close() error
}

// ── RabbitMQ ──────────────────────────────────────────────────────────────────

type rabbitQueue struct {
	conn    *amqp.Connection
	channel *amqp.Channel
	queue   string
}

func NewRabbitMQ(url, queueName string) (Queue, error) {
	conn, err := amqp.Dial(url)
	if err != nil {
		return nil, fmt.Errorf("rabbitmq dial: %w", err)
	}

	ch, err := conn.Channel()
	if err != nil {
		conn.Close()
		return nil, fmt.Errorf("rabbitmq channel: %w", err)
	}

	_, err = ch.QueueDeclare(queueName, true, false, false, false, nil)
	if err != nil {
		ch.Close()
		conn.Close()
		return nil, fmt.Errorf("queue declare: %w", err)
	}

	return &rabbitQueue{conn: conn, channel: ch, queue: queueName}, nil
}

func (q *rabbitQueue) Publish(ctx context.Context, intent *PaymentIntent) error {
	body, err := json.Marshal(intent)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	return q.channel.PublishWithContext(ctx, "", q.queue, false, false, amqp.Publishing{
		DeliveryMode: amqp.Persistent,
		ContentType:  "application/json",
		Body:         body,
	})
}

func (q *rabbitQueue) Consume(ctx context.Context, handler func(*PaymentIntent) error) error {
	msgs, err := q.channel.Consume(q.queue, "payment-worker", false, false, false, false, nil)
	if err != nil {
		return err
	}
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case d, ok := <-msgs:
			if !ok {
				return nil
			}
			var intent PaymentIntent
			if err := json.Unmarshal(d.Body, &intent); err != nil {
				_ = d.Nack(false, true)
				continue
			}
			if err := handler(&intent); err != nil {
				_ = d.Nack(false, true)
				continue
			}
			_ = d.Ack(false)
		}
	}
}

func (q *rabbitQueue) Close() error {
	if q.channel != nil {
		_ = q.channel.Close()
	}
	if q.conn != nil {
		return q.conn.Close()
	}
	return nil
}

// ── In-memory channel queue (fallback when RabbitMQ is unavailable) ───────────
// Messages are lost on process restart — use RabbitMQ for durability.

type chanQueue struct {
	ch chan *PaymentIntent
}

// NewChanQueue returns an in-process queue backed by a buffered Go channel.
func NewChanQueue(bufSize int) Queue {
	if bufSize <= 0 {
		bufSize = 1000
	}
	slog.Info("using in-memory channel queue (RabbitMQ not available)")
	return &chanQueue{ch: make(chan *PaymentIntent, bufSize)}
}

func (q *chanQueue) Publish(_ context.Context, intent *PaymentIntent) error {
	select {
	case q.ch <- intent:
		return nil
	default:
		return fmt.Errorf("queue full (%d pending)", len(q.ch))
	}
}

func (q *chanQueue) Consume(ctx context.Context, handler func(*PaymentIntent) error) error {
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case intent, ok := <-q.ch:
			if !ok {
				return nil
			}
			if err := handler(intent); err != nil {
				// Re-queue on failure (best-effort)
				select {
				case q.ch <- intent:
				default:
					slog.Error("requeue failed, dropping intent", "transaction_id", intent.TransactionID)
				}
			}
		}
	}
}

func (q *chanQueue) Close() error {
	close(q.ch)
	return nil
}
