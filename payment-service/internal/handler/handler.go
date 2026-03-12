package handler

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"
	"github.com/newrelic/go-agent/v3/newrelic"
	"github.com/navalarchive/payment-service/internal/idempotency"
	"github.com/navalarchive/payment-service/internal/processor"
	"github.com/navalarchive/payment-service/internal/queue"
	"github.com/navalarchive/payment-service/internal/store"
)

type contextKey string

const accountCtxKey contextKey = "account"

// AccountInfo is injected into the request context by AuthMiddleware.
type AccountInfo struct {
	AccountID string
	Name      string
	Tier      string
}

func accountFromContext(ctx context.Context) *AccountInfo {
	a, _ := ctx.Value(accountCtxKey).(*AccountInfo)
	return a
}

// AuthMiddleware validates X-API-Key or Authorization: Bearer by calling account-service.
// If nrApp is non-nil, the verify HTTP call is instrumented as a New Relic external segment.
func AuthMiddleware(accountServiceURL string, nrApp *newrelic.Application) func(http.Handler) http.Handler {
	verifyURL := accountServiceURL + "/api/auth/verify"
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			key := extractKey(r)
			if key == "" {
				writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "API key required (X-API-Key header)"})
				return
			}

			acct, err := verifyKey(r.Context(), verifyURL, key, nrApp)
			if err != nil || acct == nil {
				writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "Invalid API key"})
				return
			}

			next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), accountCtxKey, acct)))
		})
	}
}

func verifyKey(ctx context.Context, verifyURL, key string, nrApp *newrelic.Application) (*AccountInfo, error) {
	body, _ := json.Marshal(map[string]string{"apiKey": key})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, verifyURL, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")

	// Instrument as a New Relic external segment if a transaction exists in context.
	client := &http.Client{Timeout: 3 * time.Second}
	if txn := newrelic.FromContext(ctx); txn != nil {
		seg := newrelic.StartExternalSegment(txn, req)
		resp, err := client.Do(req)
		seg.Response = resp
		seg.End()
		if err != nil {
			return nil, err
		}
		defer resp.Body.Close()
		return decodeVerifyResponse(resp)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	return decodeVerifyResponse(resp)
}

func decodeVerifyResponse(resp *http.Response) (*AccountInfo, error) {
	var result struct {
		Valid     bool   `json:"valid"`
		AccountID string `json:"accountId"`
		Name      string `json:"name"`
		Tier      string `json:"tier"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil || !result.Valid {
		return nil, nil
	}
	return &AccountInfo{AccountID: result.AccountID, Name: result.Name, Tier: result.Tier}, nil
}

// ── Payment Methods ───────────────────────────────────────────────────────────

type addMethodRequest struct {
	CardNumber string `json:"cardNumber"`
	ExpMonth   int    `json:"expMonth"`
	ExpYear    int    `json:"expYear"`
	CVV        string `json:"cvv"`
	HolderName string `json:"holderName"`
}

func HandleAddPaymentMethod(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := accountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		var req addMethodRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid JSON"})
			return
		}
		if len(req.CardNumber) < 13 || req.ExpMonth < 1 || req.ExpMonth > 12 || req.ExpYear < 2024 {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid card details"})
			return
		}

		token, err := generateToken("pm")
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "token generation failed"})
			return
		}

		pm := &store.PaymentMethod{
			Token:      token,
			AccountID:  acct.AccountID,
			Brand:      detectBrand(req.CardNumber),
			Last4:      req.CardNumber[len(req.CardNumber)-4:],
			ExpMonth:   req.ExpMonth,
			ExpYear:    req.ExpYear,
			HolderName: req.HolderName,
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		// NR segment for DB write
		if txn := newrelic.FromContext(ctx); txn != nil {
			seg := txn.StartSegment("db/CreatePaymentMethod")
			defer seg.End()
		}

		if err := s.CreatePaymentMethod(ctx, pm); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "failed to save payment method"})
			return
		}

		writeJSON(w, http.StatusCreated, map[string]interface{}{
			"token":      pm.Token,
			"brand":      pm.Brand,
			"last4":      pm.Last4,
			"expMonth":   pm.ExpMonth,
			"expYear":    pm.ExpYear,
			"holderName": pm.HolderName,
		})
	}
}

func HandleListPaymentMethods(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := accountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		methods, err := s.GetPaymentMethodsByAccount(ctx, acct.AccountID)
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "failed to list payment methods"})
			return
		}

		type item struct {
			Token      string `json:"token"`
			Brand      string `json:"brand"`
			Last4      string `json:"last4"`
			ExpMonth   int    `json:"expMonth"`
			ExpYear    int    `json:"expYear"`
			HolderName string `json:"holderName,omitempty"`
		}
		result := make([]item, 0, len(methods))
		for _, pm := range methods {
			result = append(result, item{pm.Token, pm.Brand, pm.Last4, pm.ExpMonth, pm.ExpYear, pm.HolderName})
		}
		writeJSON(w, http.StatusOK, result)
	}
}

func HandleDeletePaymentMethod(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := accountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		token := chi.URLParam(r, "token")
		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		if err := s.DeletePaymentMethod(ctx, token, acct.AccountID); err != nil {
			writeJSON(w, http.StatusNotFound, map[string]string{"error": "payment method not found"})
			return
		}
		w.WriteHeader(http.StatusNoContent)
	}
}

// ── Simulate / Charge ─────────────────────────────────────────────────────────

type SimulateRequest struct {
	Amount             float64 `json:"amount"`
	Currency           string  `json:"currency"`
	Description        string  `json:"description"`
	PaymentMethodToken string  `json:"paymentMethodToken"`
	CardID             string  `json:"cardId"`
	IdempotencyKey     string  `json:"idempotencyKey"`
}

type SimulateResponse struct {
	Approved      bool    `json:"approved"`
	TransactionID string  `json:"transactionId"`
	Amount        float64 `json:"amount"`
	Currency      string  `json:"currency"`
	Message       string  `json:"message"`
}

func HandleSimulate(proc *processor.Processor, idem idempotency.IdempotencyStore, s store.Store, q queue.Queue, useQueue bool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := accountFromContext(r.Context())

		var req SimulateRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid JSON"})
			return
		}
		if req.Amount <= 0 {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Invalid amount"})
			return
		}
		if req.Currency == "" {
			req.Currency = "USD"
		}

		ctx := r.Context()

		// Validate payment method belongs to this account
		if req.PaymentMethodToken != "" && acct != nil {
			ctx2, cancel := context.WithTimeout(ctx, 3*time.Second)
			if txn := newrelic.FromContext(ctx); txn != nil {
				seg := txn.StartSegment("db/GetPaymentMethod")
				defer seg.End()
			}
			pm, err := s.GetPaymentMethod(ctx2, req.PaymentMethodToken)
			cancel()
			if err != nil || pm == nil {
				writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Payment method not found"})
				return
			}
			if pm.AccountID != acct.AccountID {
				writeJSON(w, http.StatusForbidden, map[string]string{"error": "Payment method does not belong to this account"})
				return
			}
		}

		// Idempotency check
		if req.IdempotencyKey != "" {
			if res, ok := idem.Get(ctx, req.IdempotencyKey); ok {
				msg := "Payment declined"
				if res.Approved {
					msg = "Payment approved"
				}
				writeJSON(w, http.StatusOK, SimulateResponse{
					Approved:      res.Approved,
					TransactionID: res.TransactionID,
					Amount:        res.Amount,
					Currency:      res.Currency,
					Message:       msg,
				})
				return
			}
		}

		accountID := ""
		if acct != nil {
			accountID = acct.AccountID
		}

		intent := &queue.PaymentIntent{
			AccountID:      accountID,
			PaymentToken:   req.PaymentMethodToken,
			CardID:         req.CardID,
			Amount:         req.Amount,
			Currency:       req.Currency,
			Description:    req.Description,
			IdempotencyKey: req.IdempotencyKey,
		}

		// Async queue mode
		if useQueue && q != nil {
			txID := generateTxID()
			intent.TransactionID = txID
			if err := q.Publish(ctx, intent); err != nil {
				writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": "Queue unavailable"})
				return
			}
			writeJSON(w, http.StatusAccepted, map[string]interface{}{
				"status":        "queued",
				"message":       "Payment queued for processing",
				"transactionId": txID,
			})
			return
		}

		// Sync mode — add NR segment around processing
		var t *store.Transaction
		var procErr error
		if txn := newrelic.FromContext(ctx); txn != nil {
			seg := txn.StartSegment("payment/Process")
			t, procErr = proc.Process(ctx, intent)
			seg.End()
		} else {
			t, procErr = proc.Process(ctx, intent)
		}
		if procErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": procErr.Error()})
			return
		}

		msg := "Payment declined (simulation)"
		if t.Approved {
			msg = "Payment approved"
		}
		res := SimulateResponse{
			Approved:      t.Approved,
			TransactionID: t.TransactionID,
			Amount:        t.Amount,
			Currency:      t.Currency,
			Message:       msg,
		}

		if req.IdempotencyKey != "" && t.Approved {
			idem.Set(ctx, req.IdempotencyKey, &idempotency.Result{
				Approved:      t.Approved,
				TransactionID: t.TransactionID,
				Amount:        t.Amount,
				Currency:      t.Currency,
			}, 24*time.Hour)
		}

		writeJSON(w, http.StatusOK, res)
	}
}

// ── Status + History ──────────────────────────────────────────────────────────

func HandleStatus(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		txID := chi.URLParam(r, "transactionId")
		if txID == "" {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "transactionId required"})
			return
		}

		acct := accountFromContext(r.Context())
		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		t, err := s.GetByTransactionID(ctx, txID)
		if err != nil || t == nil {
			writeJSON(w, http.StatusNotFound, map[string]string{"error": "Transaction not found"})
			return
		}
		if acct != nil && t.AccountID != "" && t.AccountID != acct.AccountID {
			writeJSON(w, http.StatusNotFound, map[string]string{"error": "Transaction not found"})
			return
		}

		msg := "Payment declined"
		if t.Approved {
			msg = "Payment approved"
		}
		writeJSON(w, http.StatusOK, map[string]interface{}{
			"transactionId": t.TransactionID,
			"approved":      t.Approved,
			"amount":        t.Amount,
			"currency":      t.Currency,
			"status":        t.Status,
			"paymentToken":  t.PaymentToken,
			"createdAt":     t.CreatedAt,
			"message":       msg,
		})
	}
}

func HandleHistory(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		acct := accountFromContext(r.Context())
		if acct == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "not authenticated"})
			return
		}

		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		txns, err := s.GetTransactionsByAccount(ctx, acct.AccountID, 50)
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "failed to fetch history"})
			return
		}

		type item struct {
			TransactionID string    `json:"transactionId"`
			Approved      bool      `json:"approved"`
			Amount        float64   `json:"amount"`
			Currency      string    `json:"currency"`
			Description   string    `json:"description,omitempty"`
			PaymentToken  string    `json:"paymentToken,omitempty"`
			Status        string    `json:"status"`
			CreatedAt     time.Time `json:"createdAt"`
		}
		result := make([]item, 0, len(txns))
		for _, t := range txns {
			result = append(result, item{
				t.TransactionID, t.Approved, t.Amount, t.Currency,
				t.Description, t.PaymentToken, t.Status, t.CreatedAt,
			})
		}
		writeJSON(w, http.StatusOK, result)
	}
}

// ── Health ────────────────────────────────────────────────────────────────────

func HandleHealth(s store.Store) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if err := s.Health(ctx); err != nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]interface{}{
				"status": "unhealthy", "service": "payment-service", "error": err.Error(),
			})
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok", "service": "payment-service"})
	}
}

// ── Helpers ───────────────────────────────────────────────────────────────────

func detectBrand(cardNumber string) string {
	if len(cardNumber) == 0 {
		return "unknown"
	}
	switch cardNumber[0] {
	case '4':
		return "visa"
	case '5':
		return "mastercard"
	case '3':
		return "amex"
	case '6':
		return "discover"
	default:
		return "unknown"
	}
}

func generateToken(prefix string) (string, error) {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return prefix + "_" + hex.EncodeToString(b), nil
}

func generateTxID() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)[:8]
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
