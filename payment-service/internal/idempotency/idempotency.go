package idempotency

import (
	"context"
	"sync"
	"time"

	"github.com/navalarchive/payment-service/internal/store"
)

type Result struct {
	Approved      bool    `json:"approved"`
	TransactionID string  `json:"transaction_id"`
	Amount        float64 `json:"amount"`
	Currency      string  `json:"currency"`
}

type IdempotencyStore interface {
	Get(ctx context.Context, key string) (*Result, bool)
	Set(ctx context.Context, key string, result *Result, ttl time.Duration) error
}

type memoryStore struct {
	mu      sync.RWMutex
	entries map[string]*entry
}

type entry struct {
	result Result
	expiry time.Time
}

func NewMemoryStore() IdempotencyStore {
	s := &memoryStore{entries: make(map[string]*entry)}
	go s.cleanup()
	return s
}

func (s *memoryStore) Get(ctx context.Context, key string) (*Result, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	e, ok := s.entries[key]
	if !ok || time.Now().After(e.expiry) {
		return nil, false
	}
	return &e.result, true
}

func (s *memoryStore) Set(ctx context.Context, key string, result *Result, ttl time.Duration) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.entries[key] = &entry{result: *result, expiry: time.Now().Add(ttl)}
	return nil
}

func (s *memoryStore) cleanup() {
	ticker := time.NewTicker(time.Minute)
	defer ticker.Stop()
	for range ticker.C {
		s.mu.Lock()
		now := time.Now()
		for k, v := range s.entries {
			if now.After(v.expiry) {
				delete(s.entries, k)
			}
		}
		s.mu.Unlock()
	}
}

type dbStore struct {
	store store.Store
	ttl   time.Duration
}

func NewDBStore(s store.Store, ttl time.Duration) IdempotencyStore {
	return &dbStore{store: s, ttl: ttl}
}

func (s *dbStore) Get(ctx context.Context, key string) (*Result, bool) {
	t, err := s.store.GetByIdempotencyKey(ctx, key)
	if err != nil || t == nil || t.Status != "completed" {
		return nil, false
	}
	return &Result{
		Approved:      t.Approved,
		TransactionID: t.TransactionID,
		Amount:        t.Amount,
		Currency:      t.Currency,
	}, true
}

func (s *dbStore) Set(ctx context.Context, key string, result *Result, ttl time.Duration) error {
	return nil
}

func NewRedisStore(url string) (IdempotencyStore, error) {
	return NewMemoryStore(), nil
}
