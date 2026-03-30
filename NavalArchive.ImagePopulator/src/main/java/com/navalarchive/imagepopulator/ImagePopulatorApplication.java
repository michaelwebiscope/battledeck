package com.navalarchive.imagepopulator;

import java.io.IOException;
import java.io.InputStream;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.*;
import java.util.concurrent.*;
import java.util.stream.Collectors;

import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

import org.springframework.boot.CommandLineRunner;
import org.springframework.boot.WebApplicationType;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.builder.SpringApplicationBuilder;

import com.google.gson.*;
import okhttp3.*;

/**
 * NavalArchive Image Populator.
 * Listener mode  (IMAGE_POPULATOR_LISTEN=true): Spring Boot web server serves /populate,
 *   /populate/stream, /populate/ship/{id}, /populate/captain/{id}, /search, /test-keys,
 *   /sync, /run, /health via ImagePopulatorController.
 * One-shot mode (default): fetches ship images from Wikipedia and uploads to API, then exits.
 *
 * Usage: java -jar image-populator.jar [API_BASE_URL] [--insecure]
 * Listener: set IMAGE_POPULATOR_LISTEN=true and SERVER_PORT=5099 (or use application.properties)
 */
@SpringBootApplication
public class ImagePopulatorApplication implements CommandLineRunner {

    // ─── Entry Point ──────────────────────────────────────────────────────────

    public static void main(String[] args) {
        // Parse mode-switching args before Spring processes them
        String apiBase = System.getenv("API_URL");
        if (apiBase == null || apiBase.isBlank()) {
            for (String a : args) {
                if (!a.startsWith("-")) { apiBase = a; break; }
            }
        }
        if (apiBase == null || apiBase.isBlank()) apiBase = "http://localhost:5000";

        boolean insecure = hasArg(args, "--insecure") || "true".equalsIgnoreCase(System.getenv("INSECURE"));
        boolean listen   = "true".equalsIgnoreCase(System.getenv("IMAGE_POPULATOR_LISTEN"))
                        || hasArg(args, "--listen");

        // Stash in system properties so the controller and CommandLineRunner can read them
        System.setProperty("image.populator.apibase",  apiBase);
        System.setProperty("image.populator.insecure", String.valueOf(insecure));
        System.setProperty("image.populator.listen",   String.valueOf(listen));

        SpringApplicationBuilder builder = new SpringApplicationBuilder(ImagePopulatorApplication.class);
        if (!listen) builder.web(WebApplicationType.NONE); // no Tomcat for one-shot mode
        builder.run(args);
    }

    /** Called by Spring after context is ready. One-shot mode runs here then exits. */
    @Override
    public void run(String... args) throws Exception {
        if (!Boolean.parseBoolean(System.getProperty("image.populator.listen", "false"))) {
            String apiBase  = System.getProperty("image.populator.apibase",  "http://localhost:5000");
            boolean insecure = Boolean.parseBoolean(System.getProperty("image.populator.insecure", "false"));
            int exitCode = runOnce(apiBase, insecure);
            System.out.println("[ImagePopulator exit code: " + exitCode + "]");
            System.exit(exitCode);
        }
        // Listener mode: Tomcat is already serving; just log readiness
        System.out.println("[ImagePopulator] Listener ready. API: "
                + System.getProperty("image.populator.apibase"));
    }

    // ─── Constants (package-private so ImagePopulatorController can use them) ─

    static final String USER_AGENT      = "NavalArchive-ImagePopulator/1.0 (https://github.com/michaelwebiscope/battledeck; educational project)";
    static final int    POPULATE_DELAY_MS = 500;
    static final Gson   GSON            = new GsonBuilder().serializeNulls().create();

    private static final int RETRY_DELAY_MS = 60000;
    private static final int MAX_RETRIES    = 3;

    // ─── Populate All ─────────────────────────────────────────────────────────

    static Map<String, Object> populateAll(OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String shipPrefix    = req != null && req.shipSearchPrefix    != null ? req.shipSearchPrefix    : "battleship";
        String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";

        PopulateQueue queue = fetchPopulateQueue(client, apiBase);
        System.out.println("[populate] Ships: " + queue.ships.size() + ", captains: " + queue.captains.size());

        List<Map<String, Object>> shipResults    = new ArrayList<>();
        List<Map<String, Object>> captainResults = new ArrayList<>();

        int idx = 0;
        for (Map<String, Object> ship : queue.ships) {
            idx++;
            int    id       = toInt(ship.get("id"));
            String name     = str(ship.get("name"));
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
            int    id       = toInt(captain.get("id"));
            String name     = str(captain.get("name"));
            String imageUrl = str(captain.get("imageUrl"));
            System.out.println("  [captain " + idx + "/" + queue.captains.size() + "] " + name);
            Map<String, Object> result = populateEntity("captain", id, name, imageUrl, captainPrefix, sources, keys, client, apiBase);
            result.put("index", idx);
            result.put("total", queue.captains.size());
            captainResults.add(result);
            sleep(POPULATE_DELAY_MS);
        }

        long shipsStored    = shipResults.stream().filter(r -> "ok".equals(r.get("status"))).count();
        long captainsStored = captainResults.stream().filter(r -> "ok".equals(r.get("status"))).count();
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("shipsStored",    shipsStored);
        out.put("captainsStored", captainsStored);
        out.put("shipResults",    shipResults);
        out.put("captainResults", captainResults);
        return out;
    }

    static Map<String, Object> populateSingleShip(int id, OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String shipPrefix = req != null && req.shipSearchPrefix != null ? req.shipSearchPrefix : "battleship";
        Map<String, Object> ship = fetchEntityById(client, apiBase, "ships", id);
        if (ship == null) return mapOf("stored", false, "reason", "Ship not found");
        return populateEntity("ship", id, str(ship.get("name")), str(ship.get("imageUrl")), shipPrefix, sources, keys, client, apiBase);
    }

    static Map<String, Object> populateSingleCaptain(int id, OkHttpClient client, String apiBase, PopulateRequest req) throws IOException {
        List<ImageSourceConfig> sources = req != null ? req.imageSources : null;
        Map<String, String> keys = req != null
            ? buildKeysMap(req.pexelsApiKey, req.pixabayApiKey, req.unsplashAccessKey, req.googleApiKey, req.googleCseId, req.customKeys)
            : new HashMap<>();
        String captainPrefix = req != null && req.captainSearchPrefix != null ? req.captainSearchPrefix : "captain";
        Map<String, Object> captain = fetchEntityById(client, apiBase, "captains", id);
        if (captain == null) return mapOf("stored", false, "reason", "Captain not found");
        return populateEntity("captain", id, str(captain.get("name")), str(captain.get("imageUrl")), captainPrefix, sources, keys, client, apiBase);
    }

    static Map<String, Object> setEntityImageFromUrl(String type, int id, String url, OkHttpClient client, String apiBase) {
        try {
            if (url == null || url.isBlank()) {
                return mapOf("id", id, "type", type, "status", "fail", "reason", "Url required");
            }
            FetchResult img = fetchImageWithRetry(client, url, type + "-" + id, 1, 1);
            if (img == null || img.data == null || img.data.length < 100) {
                return mapOf("id", id, "type", type, "status", "fail",
                        "reason", "HTTP " + (img != null ? img.httpCode : "error"));
            }
            String path = "ship".equals(type)
                    ? "/api/images/ship/" + id + "/upload"
                    : "/api/images/captain/" + id + "/upload";
            int uploadStatus = uploadImage(client, apiBase, path, img.data);
            if (uploadStatus >= 200 && uploadStatus < 300) {
                return mapOf("id", id, "type", type, "status", "ok", "stored", img.data.length, "url", url);
            }
            return mapOf("id", id, "type", type, "status", "fail", "reason", "Upload HTTP " + uploadStatus);
        } catch (Exception e) {
            return mapOf("id", id, "type", type, "status", "fail",
                    "reason", e.getMessage() != null ? e.getMessage() : "Unknown error");
        }
    }

    static Map<String, Object> populateEntity(String type, int id, String name, String imageUrl,
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

    static SearchResult searchImages(String query, int maxCount, String providerFilter,
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
                String title    = el.getAsJsonObject().get("title").getAsString();
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
                JsonObject root  = GSON.fromJson(res.body().string(), JsonObject.class);
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

    static List<Map<String, Object>> testKeys(List<ImageSourceConfig> sources,
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

    static int runOnce(String apiBase, boolean insecure) throws Exception {
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
            int    id   = toInt(ship.get("id"));
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

    static PopulateQueue fetchPopulateQueue(OkHttpClient client, String apiBase) throws IOException {
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
                JsonObject obj  = el.getAsJsonObject();
                Map<String, Object> item = new HashMap<>();
                if (obj.has("id")) item.put("id", obj.get("id").getAsInt());
                if (obj.has("name")     && !obj.get("name").isJsonNull())     item.put("name",     obj.get("name").getAsString());
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
            JsonObject obj  = GSON.fromJson(res.body().string(), JsonObject.class);
            Map<String, Object> item = new HashMap<>();
            if (obj.has("id")) item.put("id", obj.get("id").getAsInt());
            if (obj.has("name")     && !obj.get("name").isJsonNull())     item.put("name",     obj.get("name").getAsString());
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
                    if (o.has("name")     && !o.get("name").isJsonNull())     m.put("name",     o.get("name").getAsString());
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
            int    code = res.code();
            byte[] data = (res.body() != null && res.isSuccessful()) ? res.body().bytes() : null;
            return new FetchResult(code, data);
        }
    }

    // ─── HTTP / OkHttp Utilities ──────────────────────────────────────────────

    static OkHttpClient buildClient(boolean insecure) {
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

    // ─── Misc Helpers ─────────────────────────────────────────────────────────

    static Map<String, String> buildKeysMap(String pexels, String pixabay, String unsplash,
            String google, String googleCseId, Map<String, String> customKeys) {
        Map<String, String> m = new HashMap<>();
        if (pexels      != null && !pexels.isBlank())      m.put("PEXELS_API_KEY",       pexels);
        if (pixabay     != null && !pixabay.isBlank())     m.put("PIXABAY_API_KEY",      pixabay);
        if (unsplash    != null && !unsplash.isBlank())    m.put("UNSPLASH_ACCESS_KEY",  unsplash);
        if (google      != null && !google.isBlank())      m.put("GOOGLE_API_KEY",       google);
        if (googleCseId != null && !googleCseId.isBlank()) m.put("GOOGLE_CSE_ID",        googleCseId);
        if (customKeys  != null) m.putAll(customKeys);
        return m;
    }

    static Map<String, Object> mapOf(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i + 1 < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    static void sleep(long ms) {
        try { Thread.sleep(ms); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
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

    private static boolean isValidUrl(String url) {
        return url != null && (url.startsWith("http://") || url.startsWith("https://"));
    }

    private static boolean hasArg(String[] args, String flag) {
        for (String a : args) if (flag.equals(a)) return true;
        return false;
    }

    // ─── Inner Models ─────────────────────────────────────────────────────────

    private static class FetchResult {
        final int    httpCode;
        final byte[] data;
        FetchResult(int httpCode, byte[] data) { this.httpCode = httpCode; this.data = data; }
    }

    static class PopulateRequest {
        public String pexelsApiKey;
        public String pixabayApiKey;
        public String unsplashAccessKey;
        public String googleApiKey;
        public String googleCseId;
        public String shipSearchPrefix;
        public String captainSearchPrefix;
        public List<ImageSourceConfig> imageSources;
        public Map<String, String> customKeys;
    }

    static class SearchRequest {
        public String query;
        public Integer maxCount;
        public String provider;
        public String pexelsApiKey;
        public String pixabayApiKey;
        public String unsplashAccessKey;
        public String googleApiKey;
        public String googleCseId;
        public List<ImageSourceConfig> imageSources;
        public Map<String, String> customKeys;
    }

    static class ImageSourceConfig {
        public String id;
        public String name;
        public String providerType;
        public int    retryCount = 2;
        public int    sortOrder;
        public boolean enabled   = true;
        public String authKeyRef;
        public CustomApiConfig customConfig;
    }

    static class CustomApiConfig {
        public String baseUrl         = "";
        public String queryParam      = "q";
        public String authType        = "none";
        public String authHeaderName;
        public String authQueryParam;
        public String authValueFromKey;
        public String responsePath    = "results";
        public String imageUrlPath    = "";
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
