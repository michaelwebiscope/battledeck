package com.navalarchive.imagepopulator;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.stream.Collectors;

import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

import com.google.gson.*;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import okhttp3.*;

/**
 * NavalArchive Image Populator.
 * Listener mode: exposes /populate, /populate/stream, /populate/ship/{id},
 *   /populate/captain/{id}, /search, /test-keys, /sync, /run, /health.
 * One-shot mode: fetches ship images from Wikipedia and uploads to API.
 *
 * Usage: java -jar image-populator.jar [API_BASE_URL] [--insecure] [--listen] [--port PORT]
 */
public class ImagePopulatorApplication {

    private static final String USER_AGENT = "NavalArchive-ImagePopulator/1.0 (https://github.com/michaelwebiscope/battledeck; educational project)";
    private static final int POPULATE_DELAY_MS = 500;
    private static final int RETRY_DELAY_MS = 60000;
    private static final int MAX_RETRIES = 3;
    private static final Gson GSON = new GsonBuilder().serializeNulls().create();

    // ─── Entry Point ──────────────────────────────────────────────────────────

    public static void main(String[] args) {
        int exitCode = 0;
        try {
            exitCode = run(args);
        } catch (Exception e) {
            System.err.println("FATAL: " + e.getMessage());
            e.printStackTrace();
            exitCode = 1;
        }
        System.out.println("[ImagePopulator exit code: " + exitCode + "]");
        System.exit(exitCode);
    }

    private static int run(String[] args) throws Exception {
        String apiBase = System.getenv("API_URL");
        if (apiBase == null || apiBase.isBlank()) apiBase = parseApiBase(args, "http://localhost:5000");
        boolean insecure = hasArg(args, "--insecure") || "true".equals(System.getenv("INSECURE"));
        boolean listen = hasArg(args, "--listen") || "true".equalsIgnoreCase(System.getenv("IMAGE_POPULATOR_LISTEN"));
        int listenPort = parsePort(args, getEnvInt("IMAGE_POPULATOR_PORT", 5099));
        if (listen) return runListener(apiBase, insecure, listenPort);
        return runOnce(apiBase, insecure);
    }

    // ─── Listener ────────────────────────────────────────────────────────────

    private static int runListener(String apiBase, boolean insecure, int listenPort) throws Exception {
        System.out.println("[ImagePopulator] Listener mode. API: " + apiBase + " Port: " + listenPort);
        OkHttpClient client = buildClient(insecure);
        HttpServer server = HttpServer.create(new java.net.InetSocketAddress("0.0.0.0", listenPort), 0);
        AtomicBoolean running = new AtomicBoolean(false);
        ExecutorService worker = Executors.newSingleThreadExecutor();
        ExecutorService httpExecutor = Executors.newCachedThreadPool();
        server.setExecutor(httpExecutor);

        server.createContext("/health", ex -> {
            if (!"GET".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            sendJson(ex, 200, mapOf("status", "ok", "apiBase", apiBase));
        });

        // /run — backward-compat Wikipedia-only populate
        server.createContext("/run", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            if (!running.compareAndSet(false, true)) { send(ex, 409, "Already running"); return; }
            send(ex, 202, "Accepted");
            worker.submit(() -> {
                try { runOnce(apiBase, insecure); }
                catch (Exception e) { System.err.println("[/run] " + e.getMessage()); }
                finally { running.set(false); }
            });
        });

        // /sync — alias for /run for now (Wikipedia image URL sync)
        server.createContext("/sync", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            if (!running.compareAndSet(false, true)) { send(ex, 409, "Already running"); return; }
            send(ex, 202, "Accepted");
            worker.submit(() -> {
                try { runOnce(apiBase, insecure); }
                catch (Exception e) { System.err.println("[/sync] " + e.getMessage()); }
                finally { running.set(false); }
            });
        });

        // /populate/ship/{id} — longer prefix wins over /populate
        server.createContext("/populate/ship/", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            int id = parseIdFromPath(ex.getRequestURI().getPath(), "/populate/ship/");
            if (id <= 0) { send(ex, 400, "Invalid id"); return; }
            try {
                PopulateRequest req = readBody(ex, PopulateRequest.class);
                Map<String, Object> result = populateSingleShip(id, client, apiBase, req);
                boolean notFound = "Ship not found".equals(result.get("reason"));
                sendJson(ex, notFound ? 404 : 200, result);
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        // /populate/captain/{id}
        server.createContext("/populate/captain/", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            int id = parseIdFromPath(ex.getRequestURI().getPath(), "/populate/captain/");
            if (id <= 0) { send(ex, 400, "Invalid id"); return; }
            try {
                PopulateRequest req = readBody(ex, PopulateRequest.class);
                Map<String, Object> result = populateSingleCaptain(id, client, apiBase, req);
                boolean notFound = "Captain not found".equals(result.get("reason"));
                sendJson(ex, notFound ? 404 : 200, result);
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        // /populate/stream — SSE streaming populate
        server.createContext("/populate/stream", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            try {
                PopulateRequest req = readBody(ex, PopulateRequest.class);
                handlePopulateStream(ex, client, apiBase, req);
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        // /populate — full populate
        server.createContext("/populate", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            try {
                PopulateRequest req = readBody(ex, PopulateRequest.class);
                Map<String, Object> result = populateAll(client, apiBase, req);
                sendJson(ex, 200, result);
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        // /search
        server.createContext("/search", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            try {
                SearchRequest req = readBody(ex, SearchRequest.class);
                String query = req != null && req.query != null ? req.query : "battleship";
                int maxCount = req != null && req.maxCount != null ? req.maxCount : 12;
                String provider = req != null ? req.provider : null;
                List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
                Map<String, String> keys = req != null
                    ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
                    : new HashMap<>();
                SearchResult result = searchImages(query, maxCount, provider, sources, keys, client);
                sendJson(ex, 200, mapOf("source", result.source != null ? result.source : "", "urls", result.urls));
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        // /test-keys
        server.createContext("/test-keys", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) { send(ex, 405, "Method Not Allowed"); return; }
            try {
                PopulateRequest req = readBody(ex, PopulateRequest.class);
                List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
                Map<String, String> keys = req != null
                    ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
                    : new HashMap<>();
                List<Map<String, Object>> results = testKeys(sources, keys, client);
                sendJsonArray(ex, 200, results);
            } catch (Exception e) { sendJson(ex, 500, mapOf("error", e.getMessage())); }
        });

        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            try { server.stop(1); } catch (Exception ignored) {}
            worker.shutdownNow();
            httpExecutor.shutdownNow();
        }));

        server.start();
        System.out.println("[ImagePopulator] Ready on port " + listenPort);
        Thread.currentThread().join();
        return 0;
    }

    // ─── Populate All ─────────────────────────────────────────────────────────

    private static Map<String, Object> populateAll(OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String shipPrefix = req != null && req.shipSearchPrefix != null ? req.shipSearchPrefix : "battleship";
        String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";

        PopulateQueue queue = fetchPopulateQueue(client, apiBase);
        System.out.println("[populate] Ships: " + queue.ships.size() + ", captains: " + queue.captains.size());

        List<Map<String, Object>> shipResults = new ArrayList<>();
        List<Map<String, Object>> captainResults = new ArrayList<>();

        int idx = 0;
        for (Map<String, Object> ship : queue.ships) {
            idx++;
            int id = toInt(ship.get("id"));
            String name = str(ship.get("name"));
            String imageUrl = str(ship.get("imageUrl"));
            System.out.println("  [ship " + idx + "/" + queue.ships.size() + "] " + name);
            Map<String, Object> result = populateEntity("ship", id, name, imageUrl, shipPrefix, sources, keys, client, apiBase);
            result.put("index", idx);
            result.put("total", queue.ships.size());
            shipResults.add(result);
            sleep(POPULATE_DELAY_MS);
        }

        idx = 0;
        for (Map<String, Object> captain : queue.captains) {
            idx++;
            int id = toInt(captain.get("id"));
            String name = str(captain.get("name"));
            String imageUrl = str(captain.get("imageUrl"));
            System.out.println("  [captain " + idx + "/" + queue.captains.size() + "] " + name);
            Map<String, Object> result = populateEntity("captain", id, name, imageUrl, captainPrefix, sources, keys, client, apiBase);
            result.put("index", idx);
            result.put("total", queue.captains.size());
            captainResults.add(result);
            sleep(POPULATE_DELAY_MS);
        }

        long shipsStored = shipResults.stream().filter(r -> "ok".equals(r.get("status"))).count();
        long captainsStored = captainResults.stream().filter(r -> "ok".equals(r.get("status"))).count();
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("shipsStored", shipsStored);
        out.put("captainsStored", captainsStored);
        out.put("shipResults", shipResults);
        out.put("captainResults", captainResults);
        return out;
    }

    private static void handlePopulateStream(HttpExchange ex, OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        ex.getResponseHeaders().set("Content-Type", "text/event-stream");
        ex.getResponseHeaders().set("Cache-Control", "no-cache");
        ex.sendResponseHeaders(200, 0);

        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String shipPrefix = req != null && req.shipSearchPrefix != null ? req.shipSearchPrefix : "battleship";
        String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";

        try (OutputStream os = ex.getResponseBody()) {
            PopulateQueue queue = fetchPopulateQueue(client, apiBase);
            sendSse(os, "info", GSON.toJson("Processing " + queue.ships.size() + " ships, " + queue.captains.size() + " captains without cached images."));

            int idx = 0;
            for (Map<String, Object> ship : queue.ships) {
                idx++;
                int id = toInt(ship.get("id"));
                String name = str(ship.get("name"));
                String imageUrl = str(ship.get("imageUrl"));
                Map<String, Object> result = populateEntity("ship", id, name, imageUrl, shipPrefix, sources, keys, client, apiBase);
                result.put("index", idx);
                result.put("total", queue.ships.size());
                sendSse(os, "ship", GSON.toJson(result));
                sleep(POPULATE_DELAY_MS);
            }

            idx = 0;
            for (Map<String, Object> captain : queue.captains) {
                idx++;
                int id = toInt(captain.get("id"));
                String name = str(captain.get("name"));
                String imageUrl = str(captain.get("imageUrl"));
                Map<String, Object> result = populateEntity("captain", id, name, imageUrl, captainPrefix, sources, keys, client, apiBase);
                result.put("index", idx);
                result.put("total", queue.captains.size());
                sendSse(os, "captain", GSON.toJson(result));
                sleep(POPULATE_DELAY_MS);
            }
            sendSse(os, "done", "null");
        }
    }

    private static Map<String, Object> populateSingleShip(int id, OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String shipPrefix = req != null && req.shipSearchPrefix != null ? req.shipSearchPrefix : "battleship";
        Map<String, Object> ship = fetchEntityById(client, apiBase, "ships", id);
        if (ship == null) return mapOf("stored", false, "reason", "Ship not found");
        return populateEntity("ship", id, str(ship.get("name")), str(ship.get("imageUrl")), shipPrefix, sources, keys, client, apiBase);
    }

    private static Map<String, Object> populateSingleCaptain(int id, OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";
        Map<String, Object> captain = fetchEntityById(client, apiBase, "captains", id);
        if (captain == null) return mapOf("stored", false, "reason", "Captain not found");
        return populateEntity("captain", id, str(captain.get("name")), str(captain.get("imageUrl")), captainPrefix, sources, keys, client, apiBase);
    }

    private static Map<String, Object> populateEntity(String type, int id, String name, String imageUrl,
            String prefix, List<ImageSourceConfig> sources, Map<String, String> keys,
            OkHttpClient client, String apiBase) {
        try {
            String urlToFetch = imageUrl;
            if ((urlToFetch == null || urlToFetch.isBlank()) && sources != null && !sources.isEmpty()) {
                String query = name + " " + prefix + " " + type + " photo";
                SearchResult sr = searchImages(query, 3, null, sources, keys, client);
                if (!sr.urls.isEmpty()) urlToFetch = sr.urls.get(0);
            }
            if (urlToFetch == null || urlToFetch.isBlank()) {
                return mapOf("id", id, "name", name, "status", "fail", "reason", "No ImageUrl");
            }

            FetchResult img = fetchImageWithRetry(client, urlToFetch, name, 1, 1);
            // Fallback search if primary URL failed
            if ((img == null || img.data == null || img.data.length < 100) && sources != null && !sources.isEmpty()) {
                String[] queries = {name + " " + prefix, name + " warship photo", name + " naval portrait"};
                outer:
                for (String q : queries) {
                    SearchResult sr = searchImages(q, 5, null, sources, keys, client);
                    for (String u : sr.urls) {
                        img = fetchImageWithRetry(client, u, name, 1, 1);
                        if (img != null && img.data != null && img.data.length >= 100) { urlToFetch = u; break outer; }
                    }
                }
            }

            if (img == null || img.data == null || img.data.length < 100) {
                return mapOf("id", id, "name", name, "status", "fail",
                        "reason", "HTTP " + (img != null ? img.httpCode : "error"));
            }

            String path = "ship".equals(type)
                ? "/api/images/ship/" + id + "/upload"
                : "/api/images/captain/" + id + "/upload";
            int uploadStatus = uploadImage(client, apiBase, path, img.data);
            if (uploadStatus >= 200 && uploadStatus < 300) {
                return mapOf("id", id, "name", name, "status", "ok", "bytesStored", img.data.length);
            }
            return mapOf("id", id, "name", name, "status", "fail", "reason", "Upload HTTP " + uploadStatus);
        } catch (Exception e) {
            return mapOf("id", id, "name", name, "status", "fail",
                    "reason", e.getMessage() != null ? e.getMessage() : "Unknown error");
        }
    }

    // ─── Image Search ─────────────────────────────────────────────────────────

    private static SearchResult searchImages(String query, int maxCount, String providerFilter,
            List<ImageSourceConfig> sources, Map<String, String> keys, OkHttpClient client) {
        if (sources == null || sources.isEmpty()) return new SearchResult(new ArrayList<>(), null);
        List<ImageSourceConfig> ordered = sources.stream()
                .filter(s -> s.enabled)
                .sorted(Comparator.comparingInt(s -> s.sortOrder))
                .collect(Collectors.toList());
        for (ImageSourceConfig src : ordered) {
            if (providerFilter != null && !providerFilter.equalsIgnoreCase(src.providerType)) continue;
            int retries = Math.max(1, Math.min(src.retryCount > 0 ? src.retryCount : 2, 5));
            for (int attempt = 0; attempt < retries; attempt++) {
                try {
                    List<String> urls = searchProvider(src, query, maxCount, keys, client);
                    if (!urls.isEmpty()) return new SearchResult(urls, src.name != null ? src.name : src.providerType);
                } catch (Exception ignored) {}
                if (attempt < retries - 1) sleep(500);
            }
        }
        return new SearchResult(new ArrayList<>(), null);
    }

    private static List<String> searchProvider(ImageSourceConfig src, String query, int maxCount,
            Map<String, String> keys, OkHttpClient client) throws IOException {
        String type = src.providerType == null ? "" : src.providerType.toLowerCase();
        switch (type) {
            case "pexels":    return searchPexels(query, maxCount, resolveKey("PEXELS_API_KEY", src.authKeyRef, keys), client);
            case "pixabay":   return searchPixabay(query, maxCount, resolveKey("PIXABAY_API_KEY", src.authKeyRef, keys), client);
            case "unsplash":  return searchUnsplash(query, maxCount, resolveKey("UNSPLASH_ACCESS_KEY", src.authKeyRef, keys), client);
            case "google":    return searchGoogle(query, maxCount, resolveKey("GOOGLE_API_KEY", src.authKeyRef, keys), resolveKey("GOOGLE_CSE_ID", null, keys), client);
            case "wikipedia": return searchWikipedia(query, maxCount, client);
            case "custom":    return src.customConfig != null ? searchCustom(query, maxCount, src.customConfig, keys, client) : new ArrayList<>();
            default:          return new ArrayList<>();
        }
    }

    private static String resolveKey(String keyName, String authKeyRef, Map<String, String> keys) {
        if (keys.containsKey(keyName) && !keys.get(keyName).isBlank()) return keys.get(keyName);
        if (authKeyRef != null && keys.containsKey(authKeyRef) && !keys.get(authKeyRef).isBlank()) return keys.get(authKeyRef);
        String env = System.getenv(keyName);
        return env != null && !env.isBlank() ? env : null;
    }

    private static List<String> searchPexels(String query, int maxCount, String apiKey, OkHttpClient client) throws IOException {
        if (apiKey == null || apiKey.isBlank()) return new ArrayList<>();
        String url = "https://api.pexels.com/v1/search?query=" + enc(query) + "&per_page=" + maxCount;
        Request req = new Request.Builder().url(url).header("Authorization", apiKey).get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new ArrayList<>();
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            List<String> urls = new ArrayList<>();
            if (root.has("photos") && root.get("photos").isJsonArray()) {
                for (JsonElement el : root.getAsJsonArray("photos")) {
                    if (urls.size() >= maxCount) break;
                    JsonObject photo = el.getAsJsonObject();
                    if (photo.has("src")) {
                        JsonObject src = photo.getAsJsonObject("src");
                        String u = src.has("large") ? src.get("large").getAsString() : null;
                        if (isValidUrl(u)) urls.add(u);
                    }
                }
            }
            return urls;
        }
    }

    private static List<String> searchPixabay(String query, int maxCount, String apiKey, OkHttpClient client) throws IOException {
        if (apiKey == null || apiKey.isBlank()) return new ArrayList<>();
        String url = "https://pixabay.com/api/?key=" + enc(apiKey) + "&q=" + enc(query) + "&image_type=photo&per_page=" + maxCount;
        Request req = new Request.Builder().url(url).header("User-Agent", "NavalArchive/1.0").get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new ArrayList<>();
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            List<String> urls = new ArrayList<>();
            if (root.has("hits") && root.get("hits").isJsonArray()) {
                for (JsonElement el : root.getAsJsonArray("hits")) {
                    if (urls.size() >= maxCount) break;
                    JsonObject hit = el.getAsJsonObject();
                    String u = hit.has("largeImageURL") ? hit.get("largeImageURL").getAsString()
                             : hit.has("webformatURL")  ? hit.get("webformatURL").getAsString() : null;
                    if (isValidUrl(u)) urls.add(u);
                }
            }
            return urls;
        }
    }

    private static List<String> searchUnsplash(String query, int maxCount, String accessKey, OkHttpClient client) throws IOException {
        if (accessKey == null || accessKey.isBlank()) return new ArrayList<>();
        String url = "https://api.unsplash.com/search/photos?query=" + enc(query) + "&per_page=" + maxCount;
        Request req = new Request.Builder().url(url).header("Authorization", "Client-ID " + accessKey).get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new ArrayList<>();
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            List<String> urls = new ArrayList<>();
            if (root.has("results") && root.get("results").isJsonArray()) {
                for (JsonElement el : root.getAsJsonArray("results")) {
                    if (urls.size() >= maxCount) break;
                    JsonObject photo = el.getAsJsonObject();
                    if (photo.has("urls")) {
                        JsonObject u = photo.getAsJsonObject("urls");
                        String link = u.has("regular") ? u.get("regular").getAsString()
                                    : u.has("full")    ? u.get("full").getAsString() : null;
                        if (isValidUrl(link)) urls.add(link);
                    }
                }
            }
            return urls;
        }
    }

    private static List<String> searchGoogle(String query, int maxCount, String apiKey, String cseId, OkHttpClient client) throws IOException {
        if (apiKey == null || apiKey.isBlank() || cseId == null || cseId.isBlank()) return new ArrayList<>();
        String url = "https://www.googleapis.com/customsearch/v1?key=" + enc(apiKey)
                + "&cx=" + enc(cseId) + "&q=" + enc(query) + "&searchType=image&num=" + Math.min(maxCount, 10);
        Request req = new Request.Builder().url(url).header("User-Agent", "NavalArchive/1.0").get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new ArrayList<>();
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            List<String> urls = new ArrayList<>();
            if (root.has("items") && root.get("items").isJsonArray()) {
                for (JsonElement el : root.getAsJsonArray("items")) {
                    if (urls.size() >= maxCount) break;
                    String link = el.getAsJsonObject().has("link") ? el.getAsJsonObject().get("link").getAsString() : null;
                    if (isValidUrl(link)) urls.add(link);
                }
            }
            return urls;
        }
    }

    private static List<String> searchWikipedia(String query, int maxCount, OkHttpClient client) throws IOException {
        String searchUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch="
                + enc(query) + "&srlimit=" + Math.min(maxCount, 5) + "&format=json";
        Request req = new Request.Builder().url(searchUrl).header("User-Agent", USER_AGENT).get().build();
        List<String> urls = new ArrayList<>();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return urls;
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            if (!root.has("query")) return urls;
            JsonObject queryObj = root.getAsJsonObject("query");
            if (!queryObj.has("search")) return urls;
            for (JsonElement el : queryObj.getAsJsonArray("search")) {
                if (urls.size() >= maxCount) break;
                String title = el.getAsJsonObject().get("title").getAsString();
                String imageUrl = fetchWikipediaPageImage(title, client);
                if (isValidUrl(imageUrl)) { urls.add(imageUrl); sleep(150); }
            }
        }
        return urls;
    }

    private static String fetchWikipediaPageImage(String pageTitle, OkHttpClient client) {
        try {
            String url = "https://en.wikipedia.org/w/api.php?action=query&titles=" + enc(pageTitle)
                    + "&prop=pageimages&pithumbsize=800&format=json";
            Request req = new Request.Builder().url(url).header("User-Agent", USER_AGENT).get().build();
            try (Response res = client.newCall(req).execute()) {
                if (!res.isSuccessful() || res.body() == null) return null;
                JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
                JsonObject pages = root.getAsJsonObject("query").getAsJsonObject("pages");
                for (Map.Entry<String, JsonElement> entry : pages.entrySet()) {
                    JsonObject page = entry.getValue().getAsJsonObject();
                    if (page.has("thumbnail")) {
                        JsonObject thumb = page.getAsJsonObject("thumbnail");
                        if (thumb.has("source")) return thumb.get("source").getAsString();
                    }
                }
            }
        } catch (Exception ignored) {}
        return null;
    }

    private static List<String> searchCustom(String query, int maxCount, CustomApiConfig config,
            Map<String, String> keys, OkHttpClient client) throws IOException {
        String url = config.baseUrl.replaceAll("[?&]+$", "");
        char sep = url.contains("?") ? '&' : '?';
        url += sep + enc(config.queryParam != null ? config.queryParam : "q") + "=" + enc(query);
        if ("query".equalsIgnoreCase(config.authType) && config.authQueryParam != null && config.authValueFromKey != null) {
            String val = resolveKey(config.authValueFromKey, null, keys);
            if (val != null) url += "&" + enc(config.authQueryParam) + "=" + enc(val);
        }
        Request.Builder reqBuilder = new Request.Builder().url(url).get();
        if ("header".equalsIgnoreCase(config.authType) && config.authHeaderName != null && config.authValueFromKey != null) {
            String val = resolveKey(config.authValueFromKey, null, keys);
            if (val != null) reqBuilder.header(config.authHeaderName, val);
        }
        try (Response res = client.newCall(reqBuilder.build()).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new ArrayList<>();
            JsonElement root = GSON.fromJson(res.body().string(), JsonElement.class);
            for (String part : (config.responsePath != null ? config.responsePath : "results").split("\\.")) {
                if (!root.isJsonObject()) return new ArrayList<>();
                JsonElement next = root.getAsJsonObject().get(part);
                if (next == null) return new ArrayList<>();
                root = next;
            }
            if (!root.isJsonArray()) return new ArrayList<>();
            List<String> urls = new ArrayList<>();
            for (JsonElement el : root.getAsJsonArray()) {
                if (urls.size() >= maxCount) break;
                String u = getNestedString(el, config.imageUrlPath != null ? config.imageUrlPath : "url");
                if (isValidUrl(u)) urls.add(u);
            }
            return urls;
        }
    }

    // ─── Test Keys ────────────────────────────────────────────────────────────

    private static List<Map<String, Object>> testKeys(List<ImageSourceConfig> sources,
            Map<String, String> keys, OkHttpClient client) {
        List<Map<String, Object>> results = new ArrayList<>();
        if (sources == null || sources.isEmpty()) {
            results.add(mapOf("provider", "(none)", "ok", false, "message", "No image sources configured."));
            return results;
        }
        String testQuery = "battleship";
        for (ImageSourceConfig src : sources.stream()
                .filter(s -> s.enabled)
                .sorted(Comparator.comparingInt(s -> s.sortOrder))
                .collect(Collectors.toList())) {
            String name = src.name != null ? src.name : src.providerType != null ? src.providerType : "?";
            if ("wikipedia".equalsIgnoreCase(src.providerType)) {
                results.add(mapOf("provider", name, "ok", true, "message", "OK (no key required)"));
                continue;
            }
            String keyVal = src.authKeyRef != null ? resolveKey(src.authKeyRef, src.authKeyRef, keys) : null;
            if ("google".equalsIgnoreCase(src.providerType)) {
                String cseId = resolveKey("GOOGLE_CSE_ID", null, keys);
                if (keyVal == null || keyVal.isBlank() || cseId == null || cseId.isBlank()) {
                    results.add(mapOf("provider", name, "ok", false, "message", "nokey")); continue;
                }
            } else if (keyVal == null || keyVal.isBlank()) {
                results.add(mapOf("provider", name, "ok", false, "message", "nokey")); continue;
            }
            try {
                List<String> urls = searchProvider(src, testQuery, 1, keys, client);
                results.add(mapOf("provider", name, "ok", !urls.isEmpty(), "message", urls.isEmpty() ? "No results" : "OK"));
            } catch (Exception e) {
                results.add(mapOf("provider", name, "ok", false, "message", e.getMessage() != null ? e.getMessage() : "Error"));
            }
        }
        return results;
    }

    // ─── Wikipedia-only populate (one-shot) ───────────────────────────────────

    private static int runOnce(String apiBase, boolean insecure) throws Exception {
        System.out.println("[ImagePopulator] Wikipedia populate. API: " + apiBase);
        OkHttpClient client = buildClient(insecure);
        List<Map<String, Object>> ships = fetchShips(client, apiBase);
        if (ships == null || ships.isEmpty()) {
            System.err.println("[ImagePopulator] ERROR: Failed to fetch ships");
            return 1;
        }
        List<Map<String, Object>> withImageList = ships.stream()
                .filter(s -> { Object u = s.get("imageUrl"); return u != null && u.toString().startsWith("http"); })
                .collect(Collectors.toList());
        int total = withImageList.size();
        System.out.println("[ImagePopulator] Ships: " + ships.size() + ", with image URLs: " + total);

        int index = 0, stored = 0;
        for (Map<String, Object> ship : withImageList) {
            Object imageUrl = ship.get("imageUrl");
            index++;
            int id = toInt(ship.get("id"));
            String name = str(ship.getOrDefault("name", "Ship " + id));
            try {
                FetchResult imgResult = fetchImageWithRetry(client, imageUrl.toString(), name, index, total);
                if (imgResult == null || imgResult.data == null || imgResult.data.length < 100) {
                    System.out.println("  [" + index + "/" + total + "] Skip: " + name + " - fetch HTTP " + (imgResult != null ? imgResult.httpCode : -1));
                    continue;
                }
                int status = uploadImage(client, apiBase, "/api/images/ship/" + id + "/upload", imgResult.data);
                if (status >= 200 && status < 300) {
                    stored++;
                    System.out.println("  [" + index + "/" + total + "] OK: " + name);
                } else {
                    System.out.println("  [" + index + "/" + total + "] Skip: " + name + " - upload HTTP " + status);
                }
            } catch (Exception e) {
                System.out.println("  [" + index + "/" + total + "] Fail: " + name + " - " + e.getMessage());
            }
            TimeUnit.MILLISECONDS.sleep(5000);
        }
        System.out.println("[ImagePopulator] Done. Stored " + stored + " images.");
        return 0;
    }

    // ─── API Client Helpers ───────────────────────────────────────────────────

    private static PopulateQueue fetchPopulateQueue(OkHttpClient client, String apiBase) throws IOException {
        String url = apiBase.replaceAll("/$", "") + "/api/images/populate-queue";
        Request req = new Request.Builder().url(url).header("Accept", "application/json").get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return new PopulateQueue(new ArrayList<>(), new ArrayList<>());
            JsonObject root = GSON.fromJson(res.body().string(), JsonObject.class);
            return new PopulateQueue(parseEntityList(root, "ships"), parseEntityList(root, "captains"));
        }
    }

    private static List<Map<String, Object>> parseEntityList(JsonObject root, String key) {
        List<Map<String, Object>> list = new ArrayList<>();
        if (root.has(key) && root.get(key).isJsonArray()) {
            for (JsonElement el : root.getAsJsonArray(key)) {
                JsonObject obj = el.getAsJsonObject();
                Map<String, Object> item = new HashMap<>();
                if (obj.has("id")) item.put("id", obj.get("id").getAsInt());
                if (obj.has("name") && !obj.get("name").isJsonNull()) item.put("name", obj.get("name").getAsString());
                if (obj.has("imageUrl") && !obj.get("imageUrl").isJsonNull()) item.put("imageUrl", obj.get("imageUrl").getAsString());
                list.add(item);
            }
        }
        return list;
    }

    private static Map<String, Object> fetchEntityById(OkHttpClient client, String apiBase, String resource, int id) throws IOException {
        String url = apiBase.replaceAll("/$", "") + "/api/" + resource + "/" + id;
        Request req = new Request.Builder().url(url).header("Accept", "application/json").get().build();
        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return null;
            JsonObject obj = GSON.fromJson(res.body().string(), JsonObject.class);
            Map<String, Object> item = new HashMap<>();
            if (obj.has("id")) item.put("id", obj.get("id").getAsInt());
            if (obj.has("name") && !obj.get("name").isJsonNull()) item.put("name", obj.get("name").getAsString());
            if (obj.has("imageUrl") && !obj.get("imageUrl").isJsonNull()) item.put("imageUrl", obj.get("imageUrl").getAsString());
            return item;
        }
    }

    private static List<Map<String, Object>> fetchShips(OkHttpClient client, String apiBase) throws IOException {
        String base = apiBase.replaceAll("/$", "");
        List<Map<String, Object>> all = new ArrayList<>();
        int page = 1, pageSize = 500;
        while (true) {
            String url = base + "/api/ships?page=" + page + "&pageSize=" + pageSize;
            Request req = new Request.Builder().url(url).header("Accept", "application/json").get().build();
            try (Response res = client.newCall(req).execute()) {
                if (!res.isSuccessful() || res.body() == null) break;
                JsonObject parsed = GSON.fromJson(res.body().string(), JsonObject.class);
                if (!parsed.has("items") || !parsed.get("items").isJsonArray()) break;
                JsonArray items = parsed.getAsJsonArray("items");
                if (items.size() == 0) break;
                for (JsonElement el : items) {
                    Map<String, Object> m = new HashMap<>();
                    JsonObject o = el.getAsJsonObject();
                    if (o.has("id")) m.put("id", o.get("id").getAsInt());
                    if (o.has("name") && !o.get("name").isJsonNull()) m.put("name", o.get("name").getAsString());
                    if (o.has("imageUrl") && !o.get("imageUrl").isJsonNull()) m.put("imageUrl", o.get("imageUrl").getAsString());
                    all.add(m);
                }
                int total = parsed.has("total") ? parsed.get("total").getAsInt() : 0;
                if (total <= 0 || all.size() >= total) break;
                page++;
            }
        }
        return all;
    }

    private static int uploadImage(OkHttpClient client, String apiBase, String path, byte[] data) throws IOException {
        String url = apiBase.replaceAll("/$", "") + path;
        Request req = new Request.Builder().url(url)
                .header("Content-Type", "image/jpeg")
                .post(RequestBody.create(data, MediaType.parse("image/jpeg")))
                .build();
        try (Response res = client.newCall(req).execute()) { return res.code(); }
    }

    private static FetchResult fetchImageWithRetry(OkHttpClient client, String url, String name, int index, int total) throws IOException {
        FetchResult result = null;
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++) {
            result = fetchImageWithStatus(client, url);
            if (result.httpCode != 429) break;
            if (attempt < MAX_RETRIES) {
                System.out.println("  [" + index + "/" + total + "] " + name + " - HTTP 429, retry " + attempt + "/" + MAX_RETRIES + " in 60s...");
                sleep(RETRY_DELAY_MS);
            }
        }
        return result;
    }

    private static FetchResult fetchImageWithStatus(OkHttpClient client, String url) throws IOException {
        Request req = new Request.Builder().url(url).header("User-Agent", USER_AGENT).get().build();
        try (Response res = client.newCall(req).execute()) {
            int code = res.code();
            byte[] data = (res.body() != null && res.isSuccessful()) ? res.body().bytes() : null;
            return new FetchResult(code, data);
        }
    }

    // ─── HTTP Utilities ───────────────────────────────────────────────────────

    private static OkHttpClient buildClient(boolean insecure) {
        OkHttpClient.Builder builder = new OkHttpClient.Builder()
                .connectTimeout(15, TimeUnit.SECONDS)
                .readTimeout(30, TimeUnit.SECONDS)
                .writeTimeout(30, TimeUnit.SECONDS)
                .followRedirects(true);
        if (insecure) {
            try {
                X509TrustManager trustAll = new X509TrustManager() {
                    public void checkClientTrusted(X509Certificate[] c, String s) {}
                    public void checkServerTrusted(X509Certificate[] c, String s) {}
                    public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
                };
                SSLContext ssl = SSLContext.getInstance("TLS");
                ssl.init(null, new TrustManager[]{trustAll}, null);
                builder.sslSocketFactory(ssl.getSocketFactory(), trustAll)
                       .hostnameVerifier((h, s) -> true);
            } catch (Exception e) { System.err.println("SSL config failed: " + e.getMessage()); }
        }
        return builder.build();
    }

    private static void send(HttpExchange ex, int status, String body) throws IOException {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().set("Content-Type", "text/plain; charset=utf-8");
        ex.sendResponseHeaders(status, bytes.length);
        try (OutputStream os = ex.getResponseBody()) { os.write(bytes); }
        ex.close();
    }

    private static void sendJson(HttpExchange ex, int status, Object obj) throws IOException {
        byte[] bytes = GSON.toJson(obj).getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
        ex.sendResponseHeaders(status, bytes.length);
        try (OutputStream os = ex.getResponseBody()) { os.write(bytes); }
        ex.close();
    }

    private static void sendJsonArray(HttpExchange ex, int status, List<?> list) throws IOException {
        byte[] bytes = GSON.toJson(list).getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
        ex.sendResponseHeaders(status, bytes.length);
        try (OutputStream os = ex.getResponseBody()) { os.write(bytes); }
        ex.close();
    }

    private static void sendSse(OutputStream os, String type, String dataJson) throws IOException {
        String line = "data: {\"type\":\"" + type + "\",\"data\":" + dataJson + "}\n\n";
        os.write(line.getBytes(StandardCharsets.UTF_8));
        os.flush();
    }

    private static <T> T readBody(HttpExchange ex, Class<T> cls) {
        try (InputStream is = ex.getRequestBody()) {
            String body = new String(is.readAllBytes(), StandardCharsets.UTF_8);
            if (body.isBlank()) return null;
            return GSON.fromJson(body, cls);
        } catch (Exception e) { return null; }
    }

    // ─── Misc Helpers ─────────────────────────────────────────────────────────

    private static int parseIdFromPath(String path, String prefix) {
        try {
            String rest = path.substring(prefix.length());
            int slash = rest.indexOf('/');
            String idStr = slash >= 0 ? rest.substring(0, slash) : rest;
            return Integer.parseInt(idStr.trim());
        } catch (Exception e) { return -1; }
    }

    private static Map<String, String> buildKeysMap(String pexels, String pixabay, String unsplash,
            String google, String googleCseId, Map<String, String> customKeys) {
        Map<String, String> m = new HashMap<>();
        if (pexels != null && !pexels.isBlank())       m.put("PEXELS_API_KEY", pexels);
        if (pixabay != null && !pixabay.isBlank())     m.put("PIXABAY_API_KEY", pixabay);
        if (unsplash != null && !unsplash.isBlank())   m.put("UNSPLASH_ACCESS_KEY", unsplash);
        if (google != null && !google.isBlank())       m.put("GOOGLE_API_KEY", google);
        if (googleCseId != null && !googleCseId.isBlank()) m.put("GOOGLE_CSE_ID", googleCseId);
        if (customKeys != null) m.putAll(customKeys);
        return m;
    }

    private static String enc(String s) {
        return URLEncoder.encode(s != null ? s : "", StandardCharsets.UTF_8);
    }

    private static String getNestedString(JsonElement el, String path) {
        for (String seg : path.split("\\.")) {
            if (!el.isJsonObject()) return null;
            JsonElement next = el.getAsJsonObject().get(seg);
            if (next == null) return null;
            el = next;
        }
        return el.isJsonPrimitive() ? el.getAsString() : null;
    }

    private static int toInt(Object o) {
        if (o instanceof Number) return ((Number) o).intValue();
        try { return Integer.parseInt(String.valueOf(o)); } catch (Exception e) { return 0; }
    }

    private static String str(Object o) { return o != null ? o.toString() : null; }

    private static Map<String, Object> mapOf(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i + 1 < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    private static boolean isValidUrl(String url) {
        return url != null && (url.startsWith("http://") || url.startsWith("https://"));
    }

    private static void sleep(long ms) {
        try { Thread.sleep(ms); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
    }

    private static boolean hasArg(String[] args, String flag) {
        for (String a : args) if (flag.equals(a)) return true;
        return false;
    }

    private static String parseApiBase(String[] args, String def) {
        for (int i = 0; i < args.length; i++) {
            if (!args[i].startsWith("--")) return args[i];
            if ("--port".equals(args[i])) i++;
        }
        return def;
    }

    private static int parsePort(String[] args, int def) {
        for (int i = 0; i + 1 < args.length; i++) {
            if ("--port".equals(args[i])) {
                try { return Integer.parseInt(args[i + 1]); } catch (NumberFormatException ignored) {}
            }
        }
        return def;
    }

    private static int getEnvInt(String key, int def) {
        String raw = System.getenv(key);
        if (raw == null || raw.isBlank()) return def;
        try { return Integer.parseInt(raw.trim()); } catch (NumberFormatException ignored) { return def; }
    }

    // ─── Inner Models ─────────────────────────────────────────────────────────

    private static class FetchResult {
        final int httpCode;
        final byte[] data;
        FetchResult(int httpCode, byte[] data) { this.httpCode = httpCode; this.data = data; }
    }

    static class PopulateRequest {
        String pexelsApiKey;
        String pixabayApiKey;
        String unsplashAccessKey;
        String googleApiKey;
        String googleCseId;
        String shipSearchPrefix;
        String captainSearchPrefix;
        List<ImageSourceConfig> imageSources;
        Map<String, String> customKeys;
    }

    static class SearchRequest {
        String query;
        Integer maxCount;
        String provider;
        String pexelsApiKey;
        String pixabayApiKey;
        String unsplashAccessKey;
        String googleApiKey;
        String googleCseId;
        List<ImageSourceConfig> imageSources;
        Map<String, String> customKeys;
    }

    static class ImageSourceConfig {
        String id;
        String name;
        String providerType;
        int retryCount = 2;
        int sortOrder;
        boolean enabled = true;
        String authKeyRef;
        CustomApiConfig customConfig;
    }

    static class CustomApiConfig {
        String baseUrl = "";
        String queryParam = "q";
        String authType = "none";
        String authHeaderName;
        String authQueryParam;
        String authValueFromKey;
        String responsePath = "results";
        String imageUrlPath = "";
    }

    static class SearchResult {
        final List<String> urls;
        final String source;
        SearchResult(List<String> urls, String source) { this.urls = urls; this.source = source; }
    }

    static class PopulateQueue {
        final List<Map<String, Object>> ships;
        final List<Map<String, Object>> captains;
        PopulateQueue(List<Map<String, Object>> s, List<Map<String, Object>> c) { ships = s; captains = c; }
    }
}
