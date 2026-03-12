package processor

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"log/slog"
	randv2 "math/rand/v2"
	"time"

	"github.com/navalarchive/payment-service/internal/queue"
	"github.com/navalarchive/payment-service/internal/store"
)

type Processor struct {
	store store.Store
}

func New(store store.Store) *Processor {
	return &Processor{store: store}
}

func (p *Processor) Process(ctx context.Context, intent *queue.PaymentIntent) (*store.Transaction, error) {
	txID := intent.TransactionID
	if txID == "" {
		txID = genTransactionID()
	}

	t := &store.Transaction{
		TransactionID:  txID,
		AccountID:      intent.AccountID,
		PaymentToken:   intent.PaymentToken,
		CardID:         intent.CardID,
		Amount:         intent.Amount,
		Currency:       intent.Currency,
		Description:    intent.Description,
		Status:         "pending",
		IdempotencyKey: intent.IdempotencyKey,
		RetryCount:     intent.RetryCount,
		CreatedAt:      time.Now(),
		UpdatedAt:      time.Now(),
	}

	if err := p.store.CreateTransaction(ctx, t); err != nil {
		return nil, fmt.Errorf("create transaction: %w", err)
	}

	approved := randv2.IntN(100) < 95
	t.Approved = approved
	t.Status = "completed"
	t.UpdatedAt = time.Now()

	if err := p.store.UpdateTransaction(ctx, t); err != nil {
		return nil, fmt.Errorf("update transaction: %w", err)
	}

	slog.Info("payment processed",
		"transaction_id", txID,
		"account_id", intent.AccountID,
		"approved", approved,
		"amount", intent.Amount,
		"retry_count", intent.RetryCount,
	)

	return t, nil
}

func genTransactionID() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)[:8]
}
