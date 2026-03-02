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
```

## Options

- **API_URL** (env): Overrides the API base URL argument
- **--insecure**: Accept self-signed SSL certificates (for dev/VM)

## Notes

- Waits 2.5 seconds between requests to avoid Wikipedia rate limits (429)
- Some image URLs may return 404 (moved/removed on Wikimedia)
- If you see many 429s, wait 10–15 minutes and run again
