package store

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	_ "modernc.org/sqlite"
)

type Account struct {
	ID           int64
	AccountID    string    // "acct_xxxxxxxx"
	Name         string
	Email        string
	APIKeyHash   string    // SHA-256 of the raw API key
	APIKeyPrefix string    // first 16 chars for fast index lookup
	Tier         string    // "sandbox" or "production"
	Balance      float64   // simulated wallet balance in USD
	CreatedAt    time.Time
}

type Store interface {
	CreateAccount(ctx context.Context, a *Account) error
	GetAccountByID(ctx context.Context, accountID string) (*Account, error)
	GetAccountByAPIKey(ctx context.Context, keyHash string) (*Account, error)
	GetAccountByKeyPrefix(ctx context.Context, prefix string) (*Account, error)
	AddFunds(ctx context.Context, accountID string, amount float64) (*Account, error)
	DeductFunds(ctx context.Context, accountID string, amount float64) (*Account, error)
	Health(ctx context.Context) error
	Close() error
}

func NewSQLiteStore(path string) (Store, error) {
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(1) // SQLite: single writer
	db.SetMaxIdleConns(1)

	s := &sqlStore{db: db}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, fmt.Errorf("migrate: %w", err)
	}
	return s, nil
}

type sqlStore struct {
	db *sql.DB
}

func (s *sqlStore) migrate() error {
	_, err := s.db.Exec(`
		PRAGMA journal_mode=WAL;

		CREATE TABLE IF NOT EXISTS accounts (
			id           INTEGER PRIMARY KEY AUTOINCREMENT,
			account_id   TEXT UNIQUE NOT NULL,
			name         TEXT NOT NULL,
			email        TEXT UNIQUE NOT NULL,
			api_key_hash TEXT NOT NULL,
			api_key_prefix TEXT NOT NULL,
			tier         TEXT DEFAULT 'sandbox',
			balance      REAL DEFAULT 0,
			created_at   DATETIME DEFAULT CURRENT_TIMESTAMP
		);
		CREATE INDEX IF NOT EXISTS idx_accounts_key_hash   ON accounts(api_key_hash);
		CREATE INDEX IF NOT EXISTS idx_accounts_key_prefix ON accounts(api_key_prefix);
	`)
	if err != nil {
		return err
	}
	// Add balance column to existing databases (idempotent)
	_, _ = s.db.Exec(`ALTER TABLE accounts ADD COLUMN balance REAL DEFAULT 0`)
	return nil
}

const selectCols = `id, account_id, name, email, api_key_hash, api_key_prefix, tier, balance, created_at`

func scanAccount(row interface{ Scan(...interface{}) error }) (*Account, error) {
	var a Account
	err := row.Scan(&a.ID, &a.AccountID, &a.Name, &a.Email, &a.APIKeyHash, &a.APIKeyPrefix, &a.Tier, &a.Balance, &a.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	return &a, err
}

func (s *sqlStore) CreateAccount(ctx context.Context, a *Account) error {
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO accounts (account_id, name, email, api_key_hash, api_key_prefix, tier, balance)
		VALUES (?, ?, ?, ?, ?, ?, 0)
	`, a.AccountID, a.Name, a.Email, a.APIKeyHash, a.APIKeyPrefix, a.Tier)
	if err != nil {
		return err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return err
	}
	a.ID = id
	return nil
}

func (s *sqlStore) GetAccountByID(ctx context.Context, accountID string) (*Account, error) {
	row := s.db.QueryRowContext(ctx,
		`SELECT `+selectCols+` FROM accounts WHERE account_id = ?`, accountID)
	return scanAccount(row)
}

func (s *sqlStore) GetAccountByAPIKey(ctx context.Context, keyHash string) (*Account, error) {
	row := s.db.QueryRowContext(ctx,
		`SELECT `+selectCols+` FROM accounts WHERE api_key_hash = ?`, keyHash)
	return scanAccount(row)
}

func (s *sqlStore) GetAccountByKeyPrefix(ctx context.Context, prefix string) (*Account, error) {
	row := s.db.QueryRowContext(ctx,
		`SELECT `+selectCols+` FROM accounts WHERE api_key_prefix = ?`, prefix)
	return scanAccount(row)
}

func (s *sqlStore) AddFunds(ctx context.Context, accountID string, amount float64) (*Account, error) {
	_, err := s.db.ExecContext(ctx,
		`UPDATE accounts SET balance = balance + ? WHERE account_id = ?`, amount, accountID)
	if err != nil {
		return nil, err
	}
	return s.GetAccountByID(ctx, accountID)
}

func (s *sqlStore) DeductFunds(ctx context.Context, accountID string, amount float64) (*Account, error) {
	res, err := s.db.ExecContext(ctx,
		`UPDATE accounts SET balance = balance - ? WHERE account_id = ? AND balance >= ?`,
		amount, accountID, amount)
	if err != nil {
		return nil, err
	}
	rows, _ := res.RowsAffected()
	if rows == 0 {
		return nil, fmt.Errorf("insufficient funds")
	}
	return s.GetAccountByID(ctx, accountID)
}

func (s *sqlStore) Health(ctx context.Context) error {
	return s.db.PingContext(ctx)
}

func (s *sqlStore) Close() error {
	return s.db.Close()
}
