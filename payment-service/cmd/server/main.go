package main

import (
	"context"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/go-chi/chi/v5"
	"github.com/go-chi/chi/v5/middleware"
	"github.com/navalarchive/payment-service/config"
	"github.com/navalarchive/payment-service/internal/handler"
	"github.com/navalarchive/payment-service/internal/idempotency"
	"github.com/navalarchive/payment-service/internal/processor"
	"github.com/navalarchive/payment-service/internal/queue"
	"github.com/navalarchive/payment-service/internal/store"
)

func main() {
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))

	cfg := config.Load()

	db, err := store.NewSQLStore(cfg.DatabaseType, cfg.DatabaseURL)
	if err != nil {
		slog.Error("failed to init store", "err", err)
		os.Exit(1)
	}
	defer db.Close()

	var idem idempotency.IdempotencyStore
	switch cfg.Idempotency {
	case "db":
		idem = idempotency.NewDBStore(db, 24*time.Hour)
	default:
		idem = idempotency.NewMemoryStore()
	}

	proc := processor.New(db)

	// Queue: try RabbitMQ first, fall back to in-memory channel queue.
	var q queue.Queue
	if cfg.UseQueue {
		rmq, err := queue.NewRabbitMQ(cfg.RabbitMQURL, cfg.QueueName)
		if err != nil {
			slog.Warn("RabbitMQ unavailable, falling back to in-memory queue", "err", err)
			q = queue.NewChanQueue(1000)
		} else {
			q = rmq
			defer q.Close()
			slog.Info("connected to RabbitMQ", "url", cfg.RabbitMQURL)
		}

		workerCtx, workerCancel := context.WithCancel(context.Background())
		defer workerCancel()
		go func() {
			if err := q.Consume(workerCtx, func(intent *queue.PaymentIntent) error {
				return queue.RetryWithBackoff(workerCtx, func() error {
					_, err := proc.Process(workerCtx, intent)
					return err
				})
			}); err != nil && err != context.Canceled {
				slog.Error("queue consumer error", "err", err)
			}
		}()
	}

	r := chi.NewRouter()
	r.Use(middleware.RequestID)
	r.Use(middleware.RealIP)
	r.Use(middleware.Logger)
	r.Use(middleware.Recoverer)

	// Public
	r.Get("/health", handler.HandleHealth(db))

	// Authenticated routes
	r.Group(func(r chi.Router) {
		r.Use(handler.AuthMiddleware(cfg.AccountServiceURL))

		// Payment methods (tokenized cards)
		r.Post("/api/payment/methods", handler.HandleAddPaymentMethod(db))
		r.Get("/api/payment/methods", handler.HandleListPaymentMethods(db))
		r.Delete("/api/payment/methods/{token}", handler.HandleDeletePaymentMethod(db))

		// Payments
		r.Post("/api/payment/simulate", handler.HandleSimulate(proc, idem, db, q, cfg.UseQueue))
		r.Get("/api/payment/status/{transactionId}", handler.HandleStatus(db))
		r.Get("/api/payment/history", handler.HandleHistory(db))
	})

	addr := fmt.Sprintf(":%d", cfg.HTTPPort)
	srv := &http.Server{
		Addr:         addr,
		Handler:      r,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 10 * time.Second,
	}

	slog.Info("payment service starting",
		"port", cfg.HTTPPort,
		"use_queue", cfg.UseQueue,
		"account_service", cfg.AccountServiceURL,
	)

	go func() {
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			slog.Error("server error", "err", err)
			os.Exit(1)
		}
	}()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := srv.Shutdown(ctx); err != nil {
		slog.Error("shutdown error", "err", err)
	}
}
