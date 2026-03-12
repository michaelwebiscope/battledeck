package config

import (
	"os"
	"strconv"
)

type Config struct {
	HTTPPort          int
	DatabaseURL       string
	DatabaseType      string
	RabbitMQURL       string
	QueueName         string
	UseQueue          bool
	Idempotency       string
	RedisURL          string
	AccountServiceURL string
	NRLicenseKey      string // NEW_RELIC_LICENSE_KEY
	NRAppName         string // NEW_RELIC_APP_NAME
}

func Load() *Config {
	httpPort := 5001
	if p := os.Getenv("HTTP_PORT"); p != "" {
		if v, err := strconv.Atoi(p); err == nil {
			httpPort = v
		}
	}

	dbURL := os.Getenv("DATABASE_URL")
	if dbURL == "" {
		dbURL = "payment.db"
	}

	dbType := os.Getenv("DATABASE_TYPE")
	if dbType == "" {
		if dbURL == "payment.db" || dbURL == "" {
			dbType = "sqlite"
		} else {
			dbType = "postgres"
		}
	}

	rabbitURL := os.Getenv("RABBITMQ_URL")
	if rabbitURL == "" {
		rabbitURL = "amqp://guest:guest@localhost:5672/"
	}

	queueName := os.Getenv("QUEUE_NAME")
	if queueName == "" {
		queueName = "payment_intents"
	}

	accountServiceURL := os.Getenv("ACCOUNT_SERVICE_URL")
	if accountServiceURL == "" {
		accountServiceURL = "http://localhost:5005"
	}

	nrApp := os.Getenv("NEW_RELIC_APP_NAME")
	if nrApp == "" {
		nrApp = "NavalArchivePayment"
	}

	return &Config{
		HTTPPort:          httpPort,
		DatabaseURL:       dbURL,
		DatabaseType:      dbType,
		RabbitMQURL:       rabbitURL,
		QueueName:         queueName,
		UseQueue:          os.Getenv("USE_QUEUE") == "true" || os.Getenv("USE_QUEUE") == "1",
		Idempotency:       func() string { if v := os.Getenv("IDEMPOTENCY"); v != "" { return v }; return "memory" }(),
		RedisURL:          func() string { if v := os.Getenv("REDIS_URL"); v != "" { return v }; return "redis://localhost:6379" }(),
		AccountServiceURL: accountServiceURL,
		NRLicenseKey:      os.Getenv("NEW_RELIC_LICENSE_KEY"),
		NRAppName:         nrApp,
	}
}
