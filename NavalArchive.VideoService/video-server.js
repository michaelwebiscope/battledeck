#!/usr/bin/env node
/**
 * Standalone Node.js video server - use when Maven is not available.
 * Mimics the Java Spring Boot service for local testing.
 * Run: node video-server.js
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = 5020;
const VIDEOS_DIR = path.join(__dirname, 'videos');

const server = http.createServer((req, res) => {
  const match = req.url.match(/^\/api\/videos\/(\d+)$/);
  if (!match) {
    res.writeHead(404);
    res.end();
    return;
  }
  const shipId = parseInt(match[1], 10);
  if (shipId <= 0 || shipId > 99999) {
    res.writeHead(400);
    res.end();
    return;
  }
  const filePath = path.join(VIDEOS_DIR, `ship-${shipId}.mp4`);
  if (!fs.existsSync(filePath)) {
    res.writeHead(404);
    res.end();
    return;
  }
  const stat = fs.statSync(filePath);
  const fileSize = stat.size;
  let range = req.headers.range;

  if (range) {
    const parts = range.replace(/bytes=/, '').split('-');
    const start = parseInt(parts[0], 10) || 0;
    const end = parts[1] ? parseInt(parts[1], 10) : fileSize - 1;
    const chunkSize = end - start + 1;
    const stream = fs.createReadStream(filePath, { start, end });
    res.writeHead(206, {
      'Content-Range': `bytes ${start}-${end}/${fileSize}`,
      'Accept-Ranges': 'bytes',
      'Content-Length': chunkSize,
      'Content-Type': 'video/mp4'
    });
    stream.pipe(res);
  } else {
    res.writeHead(200, {
      'Content-Length': fileSize,
      'Accept-Ranges': 'bytes',
      'Content-Type': 'video/mp4'
    });
    fs.createReadStream(filePath).pipe(res);
  }
});

server.listen(PORT, () => {
  console.log(`Video service running at http://localhost:${PORT}`);
  console.log(`Videos dir: ${VIDEOS_DIR}`);
});
