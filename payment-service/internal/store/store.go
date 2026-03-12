package store

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	_ "github.com/lib/pq"
	_ "modernc.org/sqlite"
)

type Transaction struct {
	ID             int64
	TransactionID  string
	AccountID      string
	PaymentToken   string // payment method token used
	CardID         string // legacy field
	Amount         float64
	Currency       string
	Description    string
	Approved       bool
	Status         string // pending, completed, failed
	IdempotencyKey string
	CreatedAt      time.Time
	UpdatedAt      time.Time
	RetryCount     int
	LastError      string
}

// PaymentMethod is a tokenized payment instrument (credit/debit card).
// Raw card numbers and CVV are NEVER stored.
type PaymentMethod struct {
	ID           int64
	Token        string // "pm_xxxxxxxx"
	AccountID    string
	Brand        string // visa, mastercard, amex, discover, unknown
	Last4        string
	ExpMonth     int
	ExpYear      int
	HolderName   string
	CreatedAt    time.Time
}

type Store interface {
	// Transactions
	CreateTransaction(ctx context.Context, t *Transaction) error
	GetByTransactionID(ctx context.Context, txID string) (*Transaction, error)
	GetByIdempotencyKey(ctx context.Context, key string) (*Transaction, error)
	UpdateTransaction(ctx context.Context, t *Transaction) error
	GetTransactionsByAccount(ctx context.Context, accountID string, limit int) ([]*Transaction, error)

	// Payment methods
	CreatePaymentMethod(ctx context.Context, pm *PaymentMethod) error
	GetPaymentMethod(ctx context.Context, token string) (*PaymentMethod, error)
	GetPaymentMethodsByAccount(ctx context.Context, accountID string) ([]*PaymentMethod, error)
	DeletePaymentMethod(ctx context.Context, token, accountID string) error

	Health(ctx context.Context) error
	Close() error
}

func NewSQLStore(dbType, dbURL string) (Store, error) {
	var driver string
	switch dbType {
	case "postgres":
		driver = "postgres"
	case "sqlite":
		driver = "sqlite"
	default:
		return nil, fmt.Errorf("unsupported database type: %s", dbType)
	}

	db, err := sql.Open(driver, dbURL)
	if err != nil {
		return nil, err
	}

	db.SetMaxOpenConns(25)
	db.SetMaxIdleConns(5)
	db.SetConnMaxLifetime(5 * time.Minute)

	s := &sqlStore{db: db, driver: driver}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, err
	}
	return s, nil
}

type sqlStore struct {
	db     *sql.DB
	driver string
}

func (s *sqlStore) migrate() error {
	var schema string
	if s.driver == "sqlite" {
		schema = `
		PRAGMA journal_mode=WAL;

		CREATE TABLE IF NOT EXISTS transactions (
			id              INTEGER PRIMARY KEY AUTOINCREMENT,
			transaction_id  TEXT UNIQUE NOT NULL,
			account_id      TEXT,
			payment_token   TEXT,
			card_id         TEXT,
			amount          REAL NOT NULL,
			currency        TEXT DEFAULT 'USD',
			description     TEXT,
			approved        INTEGER DEFAULT 0,
			status          TEXT DEFAULT 'pending',
			idempotency_key TEXT UNIQUE,
			created_at      DATETIME DEFAULT CURRENT_TIMESTAMP,
			updated_at      DATETIME DEFAULT CURRENT_TIMESTAMP,
			retry_count     INTEGER DEFAULT 0,
			last_error      TEXT
		);
		CREATE INDEX IF NOT EXISTS idx_transactions_idempotency  ON transactions(idempotency_key);
		CREATE INDEX IF NOT EXISTS idx_transactions_status       ON transactions(status);
		CREATE INDEX IF NOT EXISTS idx_transactions_account      ON transactions(account_id);

		CREATE TABLE IF NOT EXISTS payment_methods (
			id          INTEGER PRIMARY KEY AUTOINCREMENT,
			token       TEXT UNIQUE NOT NULL,
			account_id  TEXT NOT NULL,
			brand       TEXT NOT NULL,
			last4       TEXT NOT NULL,
			exp_month   INTEGER NOT NULL,
			exp_year    INTEGER NOT NULL,
			holder_name TEXT,
			created_at  DATETIME DEFAULT CURRENT_TIMESTAMP
		);
		CREATE INDEX IF NOT EXISTS idx_payment_methods_account ON payment_methods(account_id);
		`
	} else {
		schema = `
		CREATE TABLE IF NOT EXISTS transactions (
			id              BIGSERIAL PRIMARY KEY,
			transaction_id  VARCHAR(64) UNIQUE NOT NULL,
			account_id      VARCHAR(64),
			payment_token   VARCHAR(64),
			card_id         VARCHAR(64),
			amount          DECIMAL(18,4) NOT NULL,
			currency        VARCHAR(8) DEFAULT 'USD',
			description     TEXT,
			approved        BOOLEAN DEFAULT FALSE,
			status          VARCHAR(32) DEFAULT 'pending',
			idempotency_key VARCHAR(128) UNIQUE,
			created_at      TIMESTAMPTZ DEFAULT NOW(),
			updated_at      TIMESTAMPTZ DEFAULT NOW(),
			retry_count     INTEGER DEFAULT 0,
			last_error      TEXT
		);
		CREATE INDEX IF NOT EXISTS idx_transactions_idempotency  ON transactions(idempotency_key);
		CREATE INDEX IF NOT EXISTS idx_transactions_status       ON transactions(status);
		CREATE INDEX IF NOT EXISTS idx_transactions_account      ON transactions(account_id);

		CREATE TABLE IF NOT EXISTS payment_methods (
			id          BIGSERIAL PRIMARY KEY,
			token       VARCHAR(64) UNIQUE NOT NULL,
			account_id  VARCHAR(64) NOT NULL,
			brand       VARCHAR(20) NOT NULL,
			last4       VARCHAR(4) NOT NULL,
			exp_month   INTEGER NOT NULL,
			exp_year    INTEGER NOT NULL,
			holder_name VARCHAR(255),
			created_at  TIMESTAMPTZ DEFAULT NOW()
		);
		CREATE INDEX IF NOT EXISTS idx_payment_methods_account ON payment_methods(account_id);
		`
	}
	_, err := s.db.Exec(schema)
	if err != nil {
		return err
	}

	// Migrate existing transactions table: add new columns if missing
	if s.driver == "sqlite" {
		s.db.Exec(`ALTER TABLE transactions ADD COLUMN account_id TEXT`)
		s.db.Exec(`ALTER TABLE transactions ADD COLUMN payment_token TEXT`)
	} else {
		s.db.Exec(`ALTER TABLE transactions ADD COLUMN IF NOT EXISTS account_id VARCHAR(64)`)
		s.db.Exec(`ALTER TABLE transactions ADD COLUMN IF NOT EXISTS payment_token VARCHAR(64)`)
	}
	return nil
}

// ── Transactions ──────────────────────────────────────────────────────────────

func (s *sqlStore) CreateTransaction(ctx context.Context, t *Transaction) error {
	approved := 0
	if t.Approved {
		approved = 1
	}
	if s.driver == "postgres" {
		return s.db.QueryRowContext(ctx, `
			INSERT INTO transactions (transaction_id, account_id, payment_token, card_id, amount, currency, description, approved, status, idempotency_key, retry_count, last_error)
			VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) RETURNING id
		`, t.TransactionID, nullIfEmpty(t.AccountID), nullIfEmpty(t.PaymentToken), nullIfEmpty(t.CardID),
			t.Amount, t.Currency, t.Description, t.Approved, t.Status,
			nullIfEmpty(t.IdempotencyKey), t.RetryCount, nullIfEmpty(t.LastError)).Scan(&t.ID)
	}
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO transactions (transaction_id, account_id, payment_token, card_id, amount, currency, description, approved, status, idempotency_key, retry_count, last_error)
		VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
	`, t.TransactionID, nullIfEmpty(t.AccountID), nullIfEmpty(t.PaymentToken), nullIfEmpty(t.CardID),
		t.Amount, t.Currency, t.Description, approved, t.Status,
		nullIfEmpty(t.IdempotencyKey), t.RetryCount, nullIfEmpty(t.LastError))
	if err != nil {
		return err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return err
	}
	t.ID = id
	return nil
}

func (s *sqlStore) GetByTransactionID(ctx context.Context, txID string) (*Transaction, error) {
	var t Transaction
	var approved interface{}
	var q string
	if s.driver == "postgres" {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE transaction_id = $1`
	} else {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE transaction_id = ?`
	}
	err := s.db.QueryRowContext(ctx, q, txID).Scan(
		&t.ID, &t.TransactionID, &t.AccountID, &t.PaymentToken, &t.CardID,
		&t.Amount, &t.Currency, &t.Description, &approved, &t.Status,
		&t.IdempotencyKey, &t.CreatedAt, &t.UpdatedAt, &t.RetryCount, &t.LastError)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	t.Approved = toBool(approved)
	return &t, nil
}

func (s *sqlStore) GetByIdempotencyKey(ctx context.Context, key string) (*Transaction, error) {
	if key == "" {
		return nil, nil
	}
	var t Transaction
	var approved interface{}
	var q string
	if s.driver == "postgres" {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE idempotency_key = $1`
	} else {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE idempotency_key = ?`
	}
	err := s.db.QueryRowContext(ctx, q, key).Scan(
		&t.ID, &t.TransactionID, &t.AccountID, &t.PaymentToken, &t.CardID,
		&t.Amount, &t.Currency, &t.Description, &approved, &t.Status,
		&t.IdempotencyKey, &t.CreatedAt, &t.UpdatedAt, &t.RetryCount, &t.LastError)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	t.Approved = toBool(approved)
	return &t, nil
}

func (s *sqlStore) UpdateTransaction(ctx context.Context, t *Transaction) error {
	approved := 0
	if t.Approved {
		approved = 1
	}
	if s.driver == "postgres" {
		_, err := s.db.ExecContext(ctx, `
			UPDATE transactions SET approved=$1, status=$2, updated_at=NOW(), retry_count=$3, last_error=$4 WHERE id=$5
		`, t.Approved, t.Status, t.RetryCount, nullIfEmpty(t.LastError), t.ID)
		return err
	}
	_, err := s.db.ExecContext(ctx, `
		UPDATE transactions SET approved=?, status=?, updated_at=CURRENT_TIMESTAMP, retry_count=?, last_error=? WHERE id=?
	`, approved, t.Status, t.RetryCount, nullIfEmpty(t.LastError), t.ID)
	return err
}

func (s *sqlStore) GetTransactionsByAccount(ctx context.Context, accountID string, limit int) ([]*Transaction, error) {
	if limit <= 0 || limit > 100 {
		limit = 50
	}
	var q string
	if s.driver == "postgres" {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE account_id=$1 ORDER BY created_at DESC LIMIT $2`
	} else {
		q = `SELECT id, transaction_id, COALESCE(account_id,''), COALESCE(payment_token,''), COALESCE(card_id,''), amount, currency, COALESCE(description,''), approved, status, COALESCE(idempotency_key,''), created_at, updated_at, retry_count, COALESCE(last_error,'') FROM transactions WHERE account_id=? ORDER BY created_at DESC LIMIT ?`
	}
	rows, err := s.db.QueryContext(ctx, q, accountID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var txns []*Transaction
	for rows.Next() {
		var t Transaction
		var approved interface{}
		if err := rows.Scan(&t.ID, &t.TransactionID, &t.AccountID, &t.PaymentToken, &t.CardID,
			&t.Amount, &t.Currency, &t.Description, &approved, &t.Status,
			&t.IdempotencyKey, &t.CreatedAt, &t.UpdatedAt, &t.RetryCount, &t.LastError); err != nil {
			return nil, err
		}
		t.Approved = toBool(approved)
		txns = append(txns, &t)
	}
	return txns, rows.Err()
}

// ── Payment Methods ───────────────────────────────────────────────────────────

func (s *sqlStore) CreatePaymentMethod(ctx context.Context, pm *PaymentMethod) error {
	if s.driver == "postgres" {
		return s.db.QueryRowContext(ctx, `
			INSERT INTO payment_methods (token, account_id, brand, last4, exp_month, exp_year, holder_name)
			VALUES ($1,$2,$3,$4,$5,$6,$7) RETURNING id
		`, pm.Token, pm.AccountID, pm.Brand, pm.Last4, pm.ExpMonth, pm.ExpYear, nullIfEmpty(pm.HolderName)).Scan(&pm.ID)
	}
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO payment_methods (token, account_id, brand, last4, exp_month, exp_year, holder_name)
		VALUES (?,?,?,?,?,?,?)
	`, pm.Token, pm.AccountID, pm.Brand, pm.Last4, pm.ExpMonth, pm.ExpYear, nullIfEmpty(pm.HolderName))
	if err != nil {
		return err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return err
	}
	pm.ID = id
	return nil
}

func (s *sqlStore) GetPaymentMethod(ctx context.Context, token string) (*PaymentMethod, error) {
	var pm PaymentMethod
	var q string
	if s.driver == "postgres" {
		q = `SELECT id, token, account_id, brand, last4, exp_month, exp_year, COALESCE(holder_name,''), created_at FROM payment_methods WHERE token=$1`
	} else {
		q = `SELECT id, token, account_id, brand, last4, exp_month, exp_year, COALESCE(holder_name,''), created_at FROM payment_methods WHERE token=?`
	}
	err := s.db.QueryRowContext(ctx, q, token).Scan(
		&pm.ID, &pm.Token, &pm.AccountID, &pm.Brand, &pm.Last4, &pm.ExpMonth, &pm.ExpYear, &pm.HolderName, &pm.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &pm, nil
}

func (s *sqlStore) GetPaymentMethodsByAccount(ctx context.Context, accountID string) ([]*PaymentMethod, error) {
	var q string
	if s.driver == "postgres" {
		q = `SELECT id, token, account_id, brand, last4, exp_month, exp_year, COALESCE(holder_name,''), created_at FROM payment_methods WHERE account_id=$1 ORDER BY created_at DESC`
	} else {
		q = `SELECT id, token, account_id, brand, last4, exp_month, exp_year, COALESCE(holder_name,''), created_at FROM payment_methods WHERE account_id=? ORDER BY created_at DESC`
	}
	rows, err := s.db.QueryContext(ctx, q, accountID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var methods []*PaymentMethod
	for rows.Next() {
		var pm PaymentMethod
		if err := rows.Scan(&pm.ID, &pm.Token, &pm.AccountID, &pm.Brand, &pm.Last4, &pm.ExpMonth, &pm.ExpYear, &pm.HolderName, &pm.CreatedAt); err != nil {
			return nil, err
		}
		methods = append(methods, &pm)
	}
	return methods, rows.Err()
}

func (s *sqlStore) DeletePaymentMethod(ctx context.Context, token, accountID string) error {
	var q string
	if s.driver == "postgres" {
		q = `DELETE FROM payment_methods WHERE token=$1 AND account_id=$2`
	} else {
		q = `DELETE FROM payment_methods WHERE token=? AND account_id=?`
	}
	res, err := s.db.ExecContext(ctx, q, token, accountID)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

func (s *sqlStore) Health(ctx context.Context) error {
	return s.db.PingContext(ctx)
}

func (s *sqlStore) Close() error {
	return s.db.Close()
}

func nullIfEmpty(s string) interface{} {
	if s == "" {
		return nil
	}
	return s
}

func toBool(v interface{}) bool {
	switch x := v.(type) {
	case bool:
		return x
	case int64:
		return x != 0
	case int:
		return x != 0
	default:
		return false
	}
}
