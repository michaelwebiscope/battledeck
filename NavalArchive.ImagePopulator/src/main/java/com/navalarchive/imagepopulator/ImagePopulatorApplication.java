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

    private static final String USER_AGENT = "Mozilla/5.0 (compatible; NavalArchive-ImagePopulator/1.0)";
    private static final int DELAY_MS = 2500; // Avoid Wikipedia rate limit

    public static void main(String[] args) throws Exception {
        String apiBase = System.getenv("API_URL");
        if (apiBase == null || apiBase.isBlank()) {
            apiBase = args.length > 0 ? args[0] : "http://localhost:5000";
        }
        boolean insecure = args.length > 1 && "--insecure".equals(args[1])
                || "true".equals(System.getenv("INSECURE"));

        System.out.println("Populate images from Wikipedia -> " + apiBase);

        OkHttpClient client = buildClient(insecure);

        List<Map<String, Object>> ships = fetchShips(client, apiBase);
        if (ships == null || ships.isEmpty()) {
            System.err.println("Failed to fetch ships");
            System.exit(1);
        }

        int stored = 0;
        for (Map<String, Object> ship : ships) {
            Object imageUrl = ship.get("imageUrl");
            if (imageUrl == null || !imageUrl.toString().startsWith("http")) continue;

            Object idObj = ship.get("id");
            int id = idObj instanceof Number ? ((Number) idObj).intValue() : 0;
            String name = String.valueOf(ship.getOrDefault("name", "Ship " + id));

            try {
                byte[] img = fetchImage(client, imageUrl.toString());
                if (img == null || img.length < 100) continue;

                int status = uploadImage(client, apiBase, id, img);
                if (status >= 200 && status < 300) {
                    stored++;
                    System.out.println("  OK: " + name + " (id " + id + ")");
                } else {
                    System.out.println("  Skip: " + name + " - upload " + status);
                }
            } catch (Exception e) {
                System.out.println("  Fail: " + name + " - " + e.getMessage());
            }

            TimeUnit.MILLISECONDS.sleep(DELAY_MS);
        }

        System.out.println("Done. Stored " + stored + " images.");
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

    private static byte[] fetchImage(OkHttpClient client, String url) throws IOException {
        Request req = new Request.Builder()
                .url(url)
                .header("User-Agent", USER_AGENT)
                .get()
                .build();

        try (Response res = client.newCall(req).execute()) {
            if (!res.isSuccessful() || res.body() == null) return null;
            return res.body().bytes();
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
