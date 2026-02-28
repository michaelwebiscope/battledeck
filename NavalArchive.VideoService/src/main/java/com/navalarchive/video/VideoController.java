package com.navalarchive.video;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.core.io.FileSystemResource;
import org.springframework.core.io.Resource;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.nio.file.Files;
import java.nio.file.Path;

/**
 * Self-hosted video streaming for ships. Supports HTTP Range requests for seeking.
 * Store videos in the configured videos directory as ship-{id}.mp4
 */
@RestController
@RequestMapping("/api/videos")
public class VideoController {

    private final Path videosPath;

    public VideoController(@Value("${video.storage-path:./videos}") String storagePath) {
        this.videosPath = Path.of(storagePath).toAbsolutePath().normalize();
    }

    @GetMapping("/{shipId}")
    public ResponseEntity<Resource> getVideo(@PathVariable int shipId) {
        // Convention: ship-{id}.mp4; sanitize shipId to prevent path traversal
        if (shipId <= 0 || shipId > 99999) {
            return ResponseEntity.badRequest().build();
        }
        String fileName = "ship-" + shipId + ".mp4";
        Path filePath = videosPath.resolve(fileName).normalize();

        if (!filePath.startsWith(videosPath) || !Files.exists(filePath)) {
            return ResponseEntity.notFound().build();
        }

        Resource resource = new FileSystemResource(filePath);
        String contentType = fileName.toLowerCase().endsWith(".webm") ? "video/webm" : "video/mp4";

        return ResponseEntity.ok()
                .contentType(MediaType.parseMediaType(contentType))
                .header(HttpHeaders.ACCEPT_RANGES, "bytes")
                .header(HttpHeaders.CACHE_CONTROL, "no-cache")
                .body(resource);
    }
}
