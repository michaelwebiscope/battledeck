package com.navalarchive.imagepopulator;

import java.io.IOException;
import java.lang.reflect.Type;
import java.security.cert.X509Certificate;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/**
 * Fetches ship images from Wikipedia and uploads to NavalArchive API.
 * Run from a machine with Wikipedia access (local dev, CI).
 *
 * Usage: java -jar image-populator.jar [API_BASE_URL] [--insecure]
 * Example: java -jar image-populator.jar https://20.234.15.204 --insecure
 */
public class ImagePopulatorApplication {

    // Wikipedia User-Agent policy: identify app + contact (https://foundation.wikimedia.org/wiki/Policy:User-Agent_policy)
    private static final String USER_AGENT = "NavalArchive-ImagePopulator/1.0 (https://github.com/michaelwebiscope/battledeck; educational project)";
    private static final int DELAY_MS = 5000; // Avoid Wikipedia rate limit
    private static final int RETRY_DELAY_MS = 60000; // Wait 60s on 429 before retry
    private static final int MAX_RETRIES = 3;

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
        if (apiBase == null || apiBase.isBlank()) {
            apiBase = args.length > 0 ? args[0] : "http://localhost:5000";
        }
        boolean insecure = args.length > 1 && "--insecure".equals(args[1])
                || "true".equals(System.getenv("INSECURE"));

        System.out.println("[ImagePopulator] Target API: " + apiBase);
        OkHttpClient client = buildClient(insecure);

        System.out.println("[ImagePopulator] Connecting to API...");
        List<Map<String, Object>> ships = fetchShips(client, apiBase);
        if (ships == null || ships.isEmpty()) {
            System.err.println("[ImagePopulator] ERROR: Failed to fetch ships (connection or API error)");
            return 1;
        }

        List<Map<String, Object>> withImageList = ships.stream()
                .filter(s -> {
                    Object u = s.get("imageUrl");
                    return u != null && u.toString().startsWith("http");
                })
                .toList();
        int total = withImageList.size();
        System.out.println("[ImagePopulator] Connected. Ships: " + ships.size() + ", with image URLs: " + total);

        int index = 0;
        int stored = 0;
        for (Map<String, Object> ship : withImageList) {
            Object imageUrl = ship.get("imageUrl");
            index++;
            Object idObj = ship.get("id");
            int id = idObj instanceof Number ? ((Number) idObj).intValue() : 0;
            String name = String.valueOf(ship.getOrDefault("name", "Ship " + id));

            try {
                FetchResult imgResult = fetchImageWithRetry(client, imageUrl.toString(), name, index, total);
                if (imgResult == null || imgResult.data == null || imgResult.data.length < 100) {
                    int code = imgResult != null ? imgResult.httpCode : -1;
                    System.out.println("  [" + index + "/" + total + "] Skip: " + name + " - fetch HTTP " + code);
                    continue;
                }

                int status = uploadImage(client, apiBase, id, imgResult.data);
                if (status >= 200 && status < 300) {
                    stored++;
                    System.out.println("  [" + index + "/" + total + "] OK: " + name + " (id " + id + ")");
                } else {
                    System.out.println("  [" + index + "/" + total + "] Skip: " + name + " - upload HTTP " + status);
                }
            } catch (Exception e) {
                System.out.println("  [" + index + "/" + total + "] Fail: " + name + " - " + e.getMessage());
            }

            TimeUnit.MILLISECONDS.sleep(DELAY_MS);
        }

        System.out.println("[ImagePopulator] Done. Stored " + stored + " images.");
        return 0;
    }

    private static class FetchResult {
        final int httpCode;
        final byte[] data;
        FetchResult(int httpCode, byte[] data) {
            this.httpCode = httpCode;
            this.data = data;
        }
    }

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
                       .hostnameVerifier((hostname, session) -> true);
            } catch (Exception e) {
                System.err.println("Failed to configure SSL: " + e.getMessage());
            }
        }
        return builder.build();
    }

    private static List<Map<String, Object>> fetchShips(OkHttpClient client, String apiBase) throws IOException {
        String url = apiBase.replaceAll("/$", "") + "/api/ships";
        Request req = new Request.Builder()
                .url(url)
                .header("Accept", "application/json")
                .get()
                .build();

        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return null;
            Type type = new TypeToken<List<Map<String, Object>>>(){}.getType();
            return new Gson().fromJson(res.body().string(), type);
        }
    }

    private static FetchResult fetchImageWithRetry(OkHttpClient client, String url, String name, int index, int total) throws IOException {
        FetchResult result = null;
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++) {
            result = fetchImageWithStatus(client, url);
            if (result.httpCode != 429) break;
            if (attempt < MAX_RETRIES) {
                System.out.println("  [" + index + "/" + total + "] " + name + " - HTTP 429, retry " + attempt + "/" + MAX_RETRIES + " in 60s...");
                try { TimeUnit.MILLISECONDS.sleep(RETRY_DELAY_MS); } catch (InterruptedException e) { Thread.currentThread().interrupt(); break; }
            }
        }
        return result;
    }

    private static FetchResult fetchImageWithStatus(OkHttpClient client, String url) throws IOException {
        Request req = new Request.Builder()
                .url(url)
                .header("User-Agent", USER_AGENT)
                .get()
                .build();

        try (Response res = client.newCall(req).execute()) {
            int code = res.code();
            byte[] data = (res.body() != null && res.isSuccessful()) ? res.body().bytes() : null;
            return new FetchResult(code, data);
        }
    }

    private static int uploadImage(OkHttpClient client, String apiBase, int shipId, byte[] data) throws IOException {
        String url = apiBase.replaceAll("/$", "") + "/api/images/ship/" + shipId + "/upload";
        Request req = new Request.Builder()
                .url(url)
                .header("Content-Type", "image/jpeg")
                .post(RequestBody.create(data, MediaType.parse("image/jpeg")))
                .build();

        try (Response res = client.newCall(req).execute()) {
            return res.code();
        }
    }
}
