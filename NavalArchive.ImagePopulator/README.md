# NavalArchive Image Populator

Java CLI app that fetches ship images from Wikipedia and uploads them to the NavalArchive API. Run from a machine with Wikipedia access (local dev, CI).

## Build

```bash
mvn clean package
```

## Run

```bash
# Against local API
java -jar target/image-populator-1.0.0.jar http://localhost:5000

# Against deployed VM (use --insecure for self-signed cert)
java -jar target/image-populator-1.0.0.jar https://20.234.15.204 --insecure

# Or with env vars
export API_URL=https://20.234.15.204
java -jar target/image-populator-1.0.0.jar --insecure

# Listener mode (long-lived process)
# Starts HTTP server on :5099 and waits for POST /run
java -jar target/image-populator-1.0.0.jar http://localhost:5000 --listen --port 5099
# Trigger:
curl -X POST http://localhost:5099/run
```

## Options

- **API_URL** (env): Overrides the API base URL argument
- **--insecure**: Accept self-signed SSL certificates (for dev/VM)
- **--listen**: Start long-lived listener mode
- **--port PORT**: Listener port (default 5099)

## Notes

- Waits 2.5 seconds between requests to avoid Wikipedia rate limits (429)
- Some image URLs may return 404 (moved/removed on Wikimedia)
- If you see many 429s, wait 10–15 minutes and run again
