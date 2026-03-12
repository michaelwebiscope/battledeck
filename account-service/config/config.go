package config

import (
	"os"
	"strconv"
)

type Config struct {
	HTTPPort    int
	DatabaseURL string
}

func Load() *Config {
	port := 5005
	if p := os.Getenv("HTTP_PORT"); p != "" {
		if v, err := strconv.Atoi(p); err == nil {
			port = v
		}
	}

	dbURL := os.Getenv("DATABASE_URL")
	if dbURL == "" {
		dbURL = "accounts.db"
	}

	return &Config{
		HTTPPort:    port,
		DatabaseURL: dbURL,
	}
}
