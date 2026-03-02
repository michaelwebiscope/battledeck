#!/usr/bin/env node
/**
 * Populate ship images from Wikipedia. Run from a machine with Wikipedia access
 * (local dev, CI). Fetches images and uploads to the API.
 *
 * Usage: node scripts/populate-images.js [API_BASE_URL]
 * Example: node scripts/populate-images.js https://20.234.15.204
 *
 * Requires: npm install axios (or use npx)
 */
const https = require('https');
const http = require('http');

const API_BASE = process.argv[2] || process.env.API_URL || 'http://localhost:5000';

// Allow self-signed certs for dev/VM (set NODE_TLS_REJECT_UNAUTHORIZED=0 or use --insecure)
const tlsOpts = (process.env.NODE_TLS_REJECT_UNAUTHORIZED === '0' || process.argv.includes('--insecure'))
  ? { rejectUnauthorized: false } : {};

function fetchJson(url) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https') ? https : http;
    lib.get(url, { headers: { Accept: 'application/json' }, ...tlsOpts }, (res) => {
      let data = '';
      res.on('data', (c) => (data += c));
      res.on('end', () => {
        try {
          resolve(JSON.parse(data));
        } catch {
          reject(new Error('Invalid JSON'));
        }
      });
    }).on('error', reject);
  });
}

function fetchImageWithStatus(url) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https') ? https : http;
    lib.get(url, { headers: { 'User-Agent': 'Mozilla/5.0 (compatible; NavalArchive/1.0)' }, ...tlsOpts }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ statusCode: res.statusCode, data: Buffer.concat(chunks) }));
    }).on('error', (e) => reject(e));
  });
}

function uploadImage(apiBase, shipId, buffer, contentType) {
  return new Promise((resolve, reject) => {
    const url = new URL(`${apiBase}/api/images/ship/${shipId}/upload`);
    const lib = url.protocol === 'https:' ? https : http;
    const opts = {
      hostname: url.hostname,
      port: url.port || (url.protocol === 'https:' ? 443 : 80),
      path: url.pathname,
      method: 'POST',
      ...tlsOpts,
      headers: {
        'Content-Type': contentType || 'image/jpeg',
        'Content-Length': buffer.length
      }
    };
    const req = lib.request(opts, (res) => {
      let data = '';
      res.on('data', (c) => (data += c));
      res.on('end', () => {
        try {
          resolve({ status: res.statusCode, body: JSON.parse(data || '{}') });
        } catch {
          resolve({ status: res.statusCode, body: data });
        }
      });
    });
    req.on('error', reject);
    req.write(buffer);
    req.end();
  });
}

async function main() {
  console.log('[populate-images] Target API:', API_BASE);
  try {
    const ships = await fetchJson(`${API_BASE}/api/ships`);
    if (!Array.isArray(ships)) {
      console.error('[populate-images] ERROR: Failed to fetch ships (invalid response)');
      process.exit(1);
    }
    const withImage = ships.filter((s) => s.imageUrl && String(s.imageUrl).startsWith('http'));
    console.log('[populate-images] Connected. Ships:', ships.length, ', with image URLs:', withImage.length);

    let stored = 0;
    const total = withImage.length;
    for (let i = 0; i < withImage.length; i++) {
      const ship = withImage[i];
      const index = i + 1;
      try {
        const imgRes = await fetchImageWithStatus(ship.imageUrl);
        if (imgRes.statusCode !== 200 || !imgRes.data || imgRes.data.length < 100) {
          console.log(`  [${index}/${total}] Skip: ${ship.name} - fetch HTTP ${imgRes.statusCode}`);
          await new Promise((r) => setTimeout(r, 2500));
          continue;
        }
        const ct = 'image/jpeg';
        const result = await uploadImage(API_BASE, ship.id, imgRes.data, ct);
        if (result.status >= 200 && result.status < 300) {
          stored++;
          console.log(`  [${index}/${total}] OK: ${ship.name} (id ${ship.id})`);
        } else {
          console.log(`  [${index}/${total}] Skip: ${ship.name} - upload HTTP ${result.status}`);
        }
      } catch (err) {
        console.log(`  [${index}/${total}] Fail: ${ship.name} - ${err.message}`);
      }
      await new Promise((r) => setTimeout(r, 2500)); // avoid Wikipedia rate limit (429)
    }
    console.log('[populate-images] Done. Stored', stored, 'images.');
    console.log('[populate-images exit code: 0]');
    process.exit(0);
  } catch (e) {
    console.error('[populate-images] FATAL:', e.message);
    console.log('[populate-images exit code: 1]');
    process.exit(1);
  }
}

main();
