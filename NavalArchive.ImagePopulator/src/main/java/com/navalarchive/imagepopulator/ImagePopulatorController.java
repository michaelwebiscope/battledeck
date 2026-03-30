package com.navalarchive.imagepopulator;

import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;
import org.springframework.web.servlet.mvc.method.annotation.SseEmitter;

import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

import okhttp3.OkHttpClient;

/**
 * Spring MVC REST controller for ImagePopulator listener mode.
 * New Relic will name these transactions:
 *   WebTransaction/Spring Controller/ImagePopulatorController/{methodName}
 */
@RestController
public class ImagePopulatorController {

    private final String       apiBase;
    private final boolean      insecure;
    private final OkHttpClient client;

    private final AtomicBoolean  running       = new AtomicBoolean(false);
    private final ExecutorService worker        = Executors.newSingleThreadExecutor();
    private final ExecutorService streamExecutor = Executors.newCachedThreadPool();

    public ImagePopulatorController() {
        this.apiBase  = System.getProperty("image.populator.apibase",  "http://localhost:5000");
        this.insecure = Boolean.parseBoolean(System.getProperty("image.populator.insecure", "false"));
        this.client   = ImagePopulatorApplication.buildClient(insecure);
    }

    // GET /health
    @GetMapping("/health")
    public Map<String, Object> health() {
        return ImagePopulatorApplication.mapOf("status", "ok", "apiBase", apiBase);
    }

    // POST /run — backward-compat Wikipedia-only one-shot populate (async)
    @PostMapping("/run")
    public ResponseEntity<String> run() {
        if (!running.compareAndSet(false, true)) {
            return ResponseEntity.status(409).body("Already running");
        }
        worker.submit(() -> {
            try { ImagePopulatorApplication.runOnce(apiBase, insecure); }
            catch (Exception e) { System.err.println("[/run] " + e.getMessage()); }
            finally { running.set(false); }
        });
        return ResponseEntity.accepted().body("Accepted");
    }

    // POST /sync — alias for /run
    @PostMapping("/sync")
    public ResponseEntity<String> sync() { return run(); }

    // POST /populate — full populate all ships & captains
    @PostMapping("/populate")
    public ResponseEntity<Object> populate(
            @RequestBody(required = false) ImagePopulatorApplication.PopulateRequest req) {
        try {
            return ResponseEntity.ok(ImagePopulatorApplication.populateAll(client, apiBase, req));
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /populate/stream — SSE streaming populate
    @PostMapping("/populate/stream")
    public SseEmitter populateStream(
            @RequestBody(required = false) ImagePopulatorApplication.PopulateRequest req) {
        SseEmitter emitter = new SseEmitter(0L); // no timeout
        streamExecutor.submit(() -> {
            try {
                List<ImagePopulatorApplication.ImageSourceConfig> sources =
                        req != null ? req.imageSources : null;
                Map<String, String> keys = req != null
                        ? ImagePopulatorApplication.buildKeysMap(
                                req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey,
                                req.googleApiKey, req.googleCseId, req.customKeys)
                        : new HashMap<>();
                String shipPrefix    = req != null && req.shipSearchPrefix    != null ? req.shipSearchPrefix    : "battleship";
                String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";

                ImagePopulatorApplication.PopulateQueue queue =
                        ImagePopulatorApplication.fetchPopulateQueue(client, apiBase);

                sendSse(emitter, "info", ImagePopulatorApplication.GSON.toJson(
                        "Processing " + queue.ships.size() + " ships, " + queue.captains.size() + " captains without cached images."));

                int idx = 0;
                for (Map<String, Object> ship : queue.ships) {
                    idx++;
                    int    id       = shipInt(ship.get("id"));
                    String name     = shipStr(ship.get("name"));
                    String imageUrl = shipStr(ship.get("imageUrl"));
                    Map<String, Object> result = ImagePopulatorApplication.populateEntity(
                            "ship", id, name, imageUrl, shipPrefix, sources, keys, client, apiBase);
                    result.put("index", idx);
                    result.put("total", queue.ships.size());
                    sendSse(emitter, "ship", ImagePopulatorApplication.GSON.toJson(result));
                    ImagePopulatorApplication.sleep(ImagePopulatorApplication.POPULATE_DELAY_MS);
                }

                idx = 0;
                for (Map<String, Object> captain : queue.captains) {
                    idx++;
                    int    id       = shipInt(captain.get("id"));
                    String name     = shipStr(captain.get("name"));
                    String imageUrl = shipStr(captain.get("imageUrl"));
                    Map<String, Object> result = ImagePopulatorApplication.populateEntity(
                            "captain", id, name, imageUrl, captainPrefix, sources, keys, client, apiBase);
                    result.put("index", idx);
                    result.put("total", queue.captains.size());
                    sendSse(emitter, "captain", ImagePopulatorApplication.GSON.toJson(result));
                    ImagePopulatorApplication.sleep(ImagePopulatorApplication.POPULATE_DELAY_MS);
                }

                sendSse(emitter, "done", "null");
                emitter.complete();
            } catch (Exception e) {
                try { emitter.completeWithError(e); } catch (Exception ignored) {}
            }
        });
        return emitter;
    }

    // POST /populate/ship/{id}
    @PostMapping("/populate/ship/{id}")
    public ResponseEntity<Object> populateShip(
            @PathVariable int id,
            @RequestBody(required = false) ImagePopulatorApplication.PopulateRequest req) {
        try {
            Map<String, Object> result =
                    ImagePopulatorApplication.populateSingleShip(id, client, apiBase, req);
            boolean notFound = "Ship not found".equals(result.get("reason"));
            return ResponseEntity.status(notFound ? 404 : 200).body(result);
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /populate/captain/{id}
    @PostMapping("/populate/captain/{id}")
    public ResponseEntity<Object> populateCaptain(
            @PathVariable int id,
            @RequestBody(required = false) ImagePopulatorApplication.PopulateRequest req) {
        try {
            Map<String, Object> result =
                    ImagePopulatorApplication.populateSingleCaptain(id, client, apiBase, req);
            boolean notFound = "Captain not found".equals(result.get("reason"));
            return ResponseEntity.status(notFound ? 404 : 200).body(result);
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /set-from-url/ship/{id}
    @PostMapping("/set-from-url/ship/{id}")
    public ResponseEntity<Object> setShipFromUrl(
            @PathVariable int id,
            @RequestBody(required = false) SetImageFromUrlRequest req) {
        try {
            String url = req != null ? req.url : null;
            Map<String, Object> result =
                    ImagePopulatorApplication.setEntityImageFromUrl("ship", id, url, client, apiBase);
            String reason = String.valueOf(result.get("reason"));
            boolean ok = "ok".equalsIgnoreCase(String.valueOf(result.get("status")));
            if (ok) return ResponseEntity.ok(result);
            if (reason != null && reason.contains("Upload HTTP 404")) return ResponseEntity.status(404).body(result);
            return ResponseEntity.status(400).body(result);
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /set-from-url/captain/{id}
    @PostMapping("/set-from-url/captain/{id}")
    public ResponseEntity<Object> setCaptainFromUrl(
            @PathVariable int id,
            @RequestBody(required = false) SetImageFromUrlRequest req) {
        try {
            String url = req != null ? req.url : null;
            Map<String, Object> result =
                    ImagePopulatorApplication.setEntityImageFromUrl("captain", id, url, client, apiBase);
            String reason = String.valueOf(result.get("reason"));
            boolean ok = "ok".equalsIgnoreCase(String.valueOf(result.get("status")));
            if (ok) return ResponseEntity.ok(result);
            if (reason != null && reason.contains("Upload HTTP 404")) return ResponseEntity.status(404).body(result);
            return ResponseEntity.status(400).body(result);
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /search
    @PostMapping("/search")
    public ResponseEntity<Object> search(
            @RequestBody(required = false) ImagePopulatorApplication.SearchRequest req) {
        try {
            String query    = req != null && req.query    != null ? req.query    : "battleship";
            int    maxCount = req != null && req.maxCount != null ? req.maxCount : 12;
            String provider = req != null ? req.provider : null;
            List<ImagePopulatorApplication.ImageSourceConfig> sources = req != null ? req.imageSources : null;
            Map<String, String> keys = req != null
                    ? ImagePopulatorApplication.buildKeysMap(
                            req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey,
                            req.googleApiKey, req.googleCseId, req.customKeys)
                    : new HashMap<>();
            ImagePopulatorApplication.SearchResult result =
                    ImagePopulatorApplication.searchImages(query, maxCount, provider, sources, keys, client);
            return ResponseEntity.ok(ImagePopulatorApplication.mapOf(
                    "source", result.source != null ? result.source : "",
                    "urls",   result.urls));
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // POST /test-keys
    @PostMapping("/test-keys")
    public ResponseEntity<Object> testKeys(
            @RequestBody(required = false) ImagePopulatorApplication.PopulateRequest req) {
        try {
            List<ImagePopulatorApplication.ImageSourceConfig> sources = req != null ? req.imageSources : null;
            Map<String, String> keys = req != null
                    ? ImagePopulatorApplication.buildKeysMap(
                            req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey,
                            req.googleApiKey, req.googleCseId, req.customKeys)
                    : new HashMap<>();
            return ResponseEntity.ok(
                    ImagePopulatorApplication.testKeys(sources, keys, client));
        } catch (Exception e) {
            return ResponseEntity.status(500).body(
                    ImagePopulatorApplication.mapOf("error", e.getMessage()));
        }
    }

    // ─── SSE helper ───────────────────────────────────────────────────────────

    /** Sends one SSE event matching the original wire format: data: {"type":"...","data":...} */
    private static void sendSse(SseEmitter emitter, String type, String dataJson) throws Exception {
        emitter.send(SseEmitter.event()
                .data("{\"type\":\"" + type + "\",\"data\":" + dataJson + "}"));
    }

    private static int shipInt(Object o) {
        if (o instanceof Number) return ((Number) o).intValue();
        try { return Integer.parseInt(String.valueOf(o)); } catch (Exception e) { return 0; }
    }

    private static String shipStr(Object o) { return o != null ? o.toString() : null; }

    public static class SetImageFromUrlRequest {
        public String url;
    }
}
