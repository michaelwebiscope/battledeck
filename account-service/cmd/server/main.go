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
	"github.com/navalarchive/account-service/config"
	"github.com/navalarchive/account-service/internal/handler"
	"github.com/navalarchive/account-service/internal/store"
)

func main() {
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))

	cfg := config.Load()

	db, err := store.NewSQLiteStore(cfg.DatabaseURL)
	if err != nil {
		slog.Error("failed to init store", "err", err)
		os.Exit(1)
	}
	defer db.Close()

	r := chi.NewRouter()
	r.Use(middleware.RequestID)
	r.Use(middleware.RealIP)
	r.Use(middleware.Logger)
	r.Use(middleware.Recoverer)

	// Public
	r.Get("/health", handler.HandleHealth(db))
	r.Post("/api/accounts/register", handler.HandleRegister(db))
	r.Post("/api/auth/verify", handler.HandleVerify(db)) // internal — called by payment-service

	// Authenticated
	r.Group(func(r chi.Router) {
		r.Use(handler.AuthMiddleware(db))
		r.Get("/api/accounts/me", handler.HandleGetMe(db))
	})

	addr := fmt.Sprintf(":%d", cfg.HTTPPort)
	srv := &http.Server{
		Addr:         addr,
		Handler:      r,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 10 * time.Second,
	}

	slog.Info("account-service starting", "port", cfg.HTTPPort, "db", cfg.DatabaseURL)

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
