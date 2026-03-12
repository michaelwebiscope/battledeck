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
	CreatedAt    time.Time
}

type Store interface {
	CreateAccount(ctx context.Context, a *Account) error
	GetAccountByID(ctx context.Context, accountID string) (*Account, error)
	GetAccountByAPIKey(ctx context.Context, keyHash string) (*Account, error)
	GetAccountByKeyPrefix(ctx context.Context, prefix string) (*Account, error)
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
			created_at   DATETIME DEFAULT CURRENT_TIMESTAMP
		);
		CREATE INDEX IF NOT EXISTS idx_accounts_key_hash   ON accounts(api_key_hash);
		CREATE INDEX IF NOT EXISTS idx_accounts_key_prefix ON accounts(api_key_prefix);
	`)
	return err
}

func (s *sqlStore) CreateAccount(ctx context.Context, a *Account) error {
	res, err := s.db.ExecContext(ctx, `
		INSERT INTO accounts (account_id, name, email, api_key_hash, api_key_prefix, tier)
		VALUES (?, ?, ?, ?, ?, ?)
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
	var a Account
	err := s.db.QueryRowContext(ctx, `
		SELECT id, account_id, name, email, api_key_hash, api_key_prefix, tier, created_at
		FROM accounts WHERE account_id = ?
	`, accountID).Scan(&a.ID, &a.AccountID, &a.Name, &a.Email, &a.APIKeyHash, &a.APIKeyPrefix, &a.Tier, &a.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &a, nil
}

func (s *sqlStore) GetAccountByAPIKey(ctx context.Context, keyHash string) (*Account, error) {
	var a Account
	err := s.db.QueryRowContext(ctx, `
		SELECT id, account_id, name, email, api_key_hash, api_key_prefix, tier, created_at
		FROM accounts WHERE api_key_hash = ?
	`, keyHash).Scan(&a.ID, &a.AccountID, &a.Name, &a.Email, &a.APIKeyHash, &a.APIKeyPrefix, &a.Tier, &a.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &a, nil
}

func (s *sqlStore) GetAccountByKeyPrefix(ctx context.Context, prefix string) (*Account, error) {
	var a Account
	err := s.db.QueryRowContext(ctx, `
		SELECT id, account_id, name, email, api_key_hash, api_key_prefix, tier, created_at
		FROM accounts WHERE api_key_prefix = ?
	`, prefix).Scan(&a.ID, &a.AccountID, &a.Name, &a.Email, &a.APIKeyHash, &a.APIKeyPrefix, &a.Tier, &a.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &a, nil
}

func (s *sqlStore) Health(ctx context.Context) error {
	return s.db.PingContext(ctx)
}

func (s *sqlStore) Close() error {
	return s.db.Close()
}
