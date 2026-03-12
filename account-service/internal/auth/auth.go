package auth

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
)

const keyPrefix = "sk_live_"

// GenerateAPIKey returns the raw key (shown once to the user), its SHA-256 hash,
// and the first 16 characters used as a lookup prefix.
func GenerateAPIKey() (key, hash, prefix string, err error) {
	b := make([]byte, 24)
	if _, err = rand.Read(b); err != nil {
		return
	}
	key = keyPrefix + hex.EncodeToString(b)
	hash = HashAPIKey(key)
	prefix = key[:16]
	return
}

// HashAPIKey returns the SHA-256 hex digest of the key.
func HashAPIKey(key string) string {
	h := sha256.Sum256([]byte(key))
	return hex.EncodeToString(h[:])
}

// GenerateID creates a prefixed random ID, e.g. "acct_a1b2c3d4".
func GenerateID(prefix string) (string, error) {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("generate id: %w", err)
	}
	return prefix + "_" + hex.EncodeToString(b), nil
}
