package handler

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/navalarchive/account-service/internal/auth"
	"github.com/navalarchive/account-service/internal/store"
)

type contextKey string

const accountCtxKey contextKey = "account"

// AuthMiddleware validates X-API-Key or Authorization: Bearer and injects the account into context.
func AuthMiddleware(s store.Store) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			key := extractKey(r)
			if key == "" {
				writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "API key required (X-API-Key header)"})
				return
			}

			ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
			defer cancel()

			acct, err := s.GetAccountByAPIKey(ctx, auth.HashAPIKey(key))
			if err != nil || acct == nil {
				writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "Invalid API key"})
				return
			}

			next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), accountCtxKey, acct)))
		})
	}
}

func AccountFromContext(ctx context.Context) *store.Account {
	a, _ := ctx.Value(accountCtxKey).(*store.Account)
	return a
}

// HandleRegister creates a new account and returns the API key (shown once).
func HandleRegister(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			Name  string `json:"name"`
			Email string `json:"email"`
			Tier  string `json:"tier"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid JSON"})
			return
		}
		if strings.TrimSpace(req.Name) == "" || strings.TrimSpace(req.Email) == "" {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "name and ID are required"})
			return
		}

		tier := req.Tier
		if tier != "production" {
			tier = "sandbox"
		}

		rawKey, keyHash, keyPrefix, err := auth.GenerateAPIKey()
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "key generation failed"})
			return
		}

		accountID, err := auth.GenerateID("acct")
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "id generation failed"})
			return
		}

		acct := &store.Account{
			AccountID:    accountID,
			Name:         strings.TrimSpace(req.Name),
			Email:        strings.TrimSpace(req.Email),
			APIKeyHash:   keyHash,
			APIKeyPrefix: keyPrefix,
			Tier:         tier,
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		if err := s.CreateAccount(ctx, acct); err != nil {
			if strings.Contains(err.Error(), "UNIQUE") {
				writeJSON(w, http.StatusConflict, map[string]string{"error": "ID already registered"})
				return
			}
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "failed to create account"})
			return
		}

		writeJSON(w, http.StatusCreated, map[string]interface{}{
			"accountId": acct.AccountID,
			"name":      acct.Name,
			"email":     acct.Email,
			"tier":      acct.Tier,
			"balance":   0.0,
			"apiKey":    rawKey,
			"message":   "Account created. Store your API key — it will not be shown again.",
		})
	}
}

// HandleGetMe returns the authenticated account's details including balance.
func HandleGetMe(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := AccountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}
		writeJSON(w, http.StatusOK, map[string]interface{}{
			"accountId":    acct.AccountID,
			"name":         acct.Name,
			"email":        acct.Email,
			"tier":         acct.Tier,
			"balance":      acct.Balance,
			"createdAt":    acct.CreatedAt,
			"apiKeyPrefix": acct.APIKeyPrefix + "...",
		})
	}
}

// HandleAddFunds adds funds to the authenticated account's balance.
// POST /api/accounts/funds  { "amount": 50.00 }
func HandleAddFunds(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := AccountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		var req struct {
			Amount float64 `json:"amount"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid JSON"})
			return
		}
		if req.Amount <= 0 {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Amount must be greater than 0"})
			return
		}
		if req.Amount > 10000 {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Amount exceeds maximum top-up of $10,000"})
			return
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		updated, err := s.AddFunds(ctx, acct.AccountID, req.Amount)
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "failed to add funds"})
			return
		}

		writeJSON(w, http.StatusOK, map[string]interface{}{
			"accountId": updated.AccountID,
			"balance":   updated.Balance,
			"added":     req.Amount,
			"message":   "Funds added successfully",
		})
	}
}

// HandleDeductFunds deducts funds from the authenticated account's balance.
// Called internally by payment-service. POST /api/accounts/funds/deduct  { "amount": 25.00 }
func HandleDeductFunds(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := AccountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		var req struct {
			Amount float64 `json:"amount"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid JSON"})
			return
		}
		if req.Amount <= 0 {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid amount"})
			return
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		updated, err := s.DeductFunds(ctx, acct.AccountID, req.Amount)
		if err != nil {
			if strings.Contains(err.Error(), "insufficient") {
				writeJSON(w, http.StatusPaymentRequired, map[string]string{
					"error": "Insufficient funds",
					"code":  "insufficient_funds",
				})
				return
			}
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "deduction failed"})
			return
		}

		writeJSON(w, http.StatusOK, map[string]interface{}{
			"accountId": updated.AccountID,
			"balance":   updated.Balance,
			"deducted":  req.Amount,
		})
	}
}

// HandleVerify is an internal endpoint for the payment-service to validate API keys.
// POST /api/auth/verify  { "apiKey": "sk_live_..." }
func HandleVerify(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			APIKey string `json:"apiKey"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.APIKey == "" {
			writeJSON(w, http.StatusOK, map[string]interface{}{"valid": false})
			return
		}

		ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
		defer cancel()

		acct, err := s.GetAccountByAPIKey(ctx, auth.HashAPIKey(req.APIKey))
		if err != nil || acct == nil {
			writeJSON(w, http.StatusOK, map[string]interface{}{"valid": false})
			return
		}

		writeJSON(w, http.StatusOK, map[string]interface{}{
			"valid":     true,
			"accountId": acct.AccountID,
			"name":      acct.Name,
			"tier":      acct.Tier,
			"balance":   acct.Balance,
		})
	}
}

// HandleHealth returns service health.
func HandleHealth(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if err := s.Health(ctx); err != nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]interface{}{
				"status": "unhealthy", "error": err.Error(),
			})
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok", "service": "account-service"})
	}
}

func extractKey(r *http.Request) string {
	if k := r.Header.Get("X-API-Key"); k != "" {
		return k
	}
	if a := r.Header.Get("Authorization"); strings.HasPrefix(a, "Bearer ") {
		return strings.TrimPrefix(a, "Bearer ")
	}
	return ""
}

func writeJSON(w http.ResponseWriter, status int, v interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
