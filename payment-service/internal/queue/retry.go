package queue

import (
	"context"
	"log/slog"
	"math"
	"time"
)

// RetryWithBackoff executes fn with exponential backoff. Max 5 retries.
func RetryWithBackoff(ctx context.Context, fn func() error) error {
	var lastErr error
	for attempt := 0; attempt < 5; attempt++ {
		if err := fn(); err != nil {
			lastErr = err
			if attempt < 4 {
				backoff := time.Duration(math.Pow(2, float64(attempt))) * time.Second
				slog.Warn("retry after error", "attempt", attempt+1, "backoff", backoff, "err", err)
				select {
				case <-ctx.Done():
					return ctx.Err()
				case <-time.After(backoff):
					continue
				}
			}
			return lastErr
		}
		return nil
	}
	return lastErr
}
