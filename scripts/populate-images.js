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

function fetchImage(url) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https') ? https : http;
    lib.get(url, { headers: { 'User-Agent': 'Mozilla/5.0 (compatible; NavalArchive/1.0)' }, ...tlsOpts }, (res) => {
      if (res.statusCode !== 200) {
        reject(new Error(`HTTP ${res.statusCode}`));
        return;
      }
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve(Buffer.concat(chunks)));
    }).on('error', reject);
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
  console.log('Populate images from Wikipedia ->', API_BASE);
  const ships = await fetchJson(`${API_BASE}/api/ships`);
  if (!Array.isArray(ships)) {
    console.error('Failed to fetch ships');
    process.exit(1);
  }
  let stored = 0;
  for (const ship of ships) {
    if (!ship.imageUrl || !ship.imageUrl.startsWith('http')) continue;
    try {
      const img = await fetchImage(ship.imageUrl);
      if (img.length < 100) continue;
      const ct = 'image/jpeg'; // Wikipedia thumbs are usually jpeg
      const result = await uploadImage(API_BASE, ship.id, img, ct);
      if (result.status >= 200 && result.status < 300) {
        stored++;
        console.log(`  OK: ${ship.name} (id ${ship.id})`);
      } else {
        console.log(`  Skip: ${ship.name} - upload ${result.status}`);
      }
    } catch (err) {
      console.log(`  Fail: ${ship.name} - ${err.message}`);
    }
    await new Promise((r) => setTimeout(r, 2500)); // avoid Wikipedia rate limit (429)
  }
  console.log(`Done. Stored ${stored} images.`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
