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
	"github.com/newrelic/go-agent/v3/newrelic"
	"github.com/navalarchive/account-service/config"
	"github.com/navalarchive/account-service/internal/handler"
	"github.com/navalarchive/account-service/internal/store"
)

func main() {
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))

	cfg := config.Load()

	// New Relic APM — optional; skipped if no license key.
	var nrApp *newrelic.Application
	if cfg.NRLicenseKey != "" {
		app, err := newrelic.NewApplication(
			newrelic.ConfigAppName(cfg.NRAppName),
			newrelic.ConfigLicense(cfg.NRLicenseKey),
			newrelic.ConfigDistributedTracerEnabled(true),
			newrelic.ConfigAppLogForwardingEnabled(true),
		)
		if err != nil {
			slog.Warn("New Relic init failed", "err", err)
		} else {
			nrApp = app
			defer nrApp.Shutdown(5 * time.Second)
			slog.Info("New Relic APM enabled", "app", cfg.NRAppName)
		}
	}

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
	r.Use(nrMiddleware(nrApp)) // New Relic transaction per request

	// Public
	r.Get("/health", handler.HandleHealth(db))
	r.Post("/api/accounts/register", handler.HandleRegister(db))
	r.Post("/api/auth/verify", handler.HandleVerify(db))

	// Authenticated
	r.Group(func(r chi.Router) {
		r.Use(handler.AuthMiddleware(db))
		r.Get("/api/accounts/me", handler.HandleGetMe(db))
		r.Post("/api/accounts/funds", handler.HandleAddFunds(db))
		r.Post("/api/accounts/funds/deduct", handler.HandleDeductFunds(db))
	})

	addr := fmt.Sprintf(":%d", cfg.HTTPPort)
	srv := &http.Server{
		Addr:         addr,
		Handler:      r,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 10 * time.Second,
	}

	slog.Info("account-service starting", "port", cfg.HTTPPort, "db", cfg.DatabaseURL, "newrelic", cfg.NRLicenseKey != "")

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

// nrMiddleware creates a New Relic transaction for each request, named by HTTP method + chi route pattern.
func nrMiddleware(app *newrelic.Application) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if app == nil {
				next.ServeHTTP(w, r)
				return
			}
			txn := app.StartTransaction(r.Method + " " + r.URL.Path)
			txn.SetWebRequestHTTP(r)
			w = txn.SetWebResponse(w)
			r = newrelic.RequestWithTransactionContext(r, txn)
			next.ServeHTTP(w, r)
			// Rename with chi's matched route pattern (e.g. /api/accounts/{id} instead of raw URL)
			if rc := chi.RouteContext(r.Context()); rc != nil {
				if p := rc.RoutePattern(); p != "" {
					txn.SetName(r.Method + " " + p)
				}
			}
			txn.End()
		})
	}
}
