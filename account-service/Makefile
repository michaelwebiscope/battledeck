.PHONY: build run build-windows

build:
	go build -o account-service ./cmd/server

run:
	go run ./cmd/server

build-windows:
	GOOS=windows GOARCH=amd64 go build -o account-service.exe ./cmd/server
