package config

import (
	"os"
	"strconv"
)

type Config struct {
	HTTPPort          int
	DatabaseURL       string
	DatabaseType      string // "postgres" or "sqlite"
	RabbitMQURL       string
	QueueName         string
	UseQueue          bool   // if false, process synchronously
	Idempotency       string // "redis", "memory", or "db"
	RedisURL          string
	AccountServiceURL string // URL of account-service for API key verification
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

	useQueue := os.Getenv("USE_QUEUE") == "true" || os.Getenv("USE_QUEUE") == "1"

	idempotency := os.Getenv("IDEMPOTENCY")
	if idempotency == "" {
		idempotency = "memory"
	}

	redisURL := os.Getenv("REDIS_URL")
	if redisURL == "" {
		redisURL = "redis://localhost:6379"
	}

	accountServiceURL := os.Getenv("ACCOUNT_SERVICE_URL")
	if accountServiceURL == "" {
		accountServiceURL = "http://localhost:5005"
	}

	return &Config{
		HTTPPort:          httpPort,
		DatabaseURL:       dbURL,
		DatabaseType:      dbType,
		RabbitMQURL:       rabbitURL,
		QueueName:         queueName,
		UseQueue:          useQueue,
		Idempotency:       idempotency,
		RedisURL:          redisURL,
		AccountServiceURL: accountServiceURL,
	}
}
