# NavalArchive Video Service (Java/Spring Boot)

Self-hosted video streaming for ships. Serves MP4/WebM files with HTTP Range support for seeking.

## Run

```bash
mvn spring-boot:run
```

Runs on port 5020 by default.

## Add Videos

Place `.mp4` or `.webm` files in `videos/` with naming: `ship-{id}.mp4` (e.g. `ship-1.mp4`, `ship-9.mp4`).

## Build JAR

```bash
mvn package
java -jar target/video-service-1.0.0.jar
```

## Configuration

- `server.port` – HTTP port (default 5020)
- `video.storage-path` – directory for video files (default `./videos`)
