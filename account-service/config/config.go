package config

import (
	"os"
	"strconv"
)

type Config struct {
	HTTPPort    int
	DatabaseURL string
	NRLicenseKey string // NEW_RELIC_LICENSE_KEY
	NRAppName    string // NEW_RELIC_APP_NAME
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

	nrApp := os.Getenv("NEW_RELIC_APP_NAME")
	if nrApp == "" {
		nrApp = "NavalArchiveAccount"
	}

	return &Config{
		HTTPPort:     port,
		DatabaseURL:  dbURL,
		NRLicenseKey: os.Getenv("NEW_RELIC_LICENSE_KEY"),
		NRAppName:    nrApp,
	}
}
