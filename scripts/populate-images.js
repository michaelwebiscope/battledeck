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

// Wikipedia User-Agent policy: identify app + contact
const USER_AGENT = 'NavalArchive/1.0 (https://github.com/michaelwebiscope/battledeck; educational project)';
const DELAY_MS = 5000;
const RETRY_DELAY_MS = 60000;
const MAX_RETRIES = 3;

function fetchImageWithStatus(url) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https') ? https : http;
    lib.get(url, { headers: { 'User-Agent': USER_AGENT }, ...tlsOpts }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ statusCode: res.statusCode, data: Buffer.concat(chunks) }));
    }).on('error', (e) => reject(e));
  });
}

async function fetchImageWithRetry(url, name, index, total) {
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    const res = await fetchImageWithStatus(url);
    if (res.statusCode !== 429) return res;
    if (attempt < MAX_RETRIES) {
      console.log(`  [${index}/${total}] ${name} - HTTP 429, retry ${attempt}/${MAX_RETRIES} in 60s...`);
      await new Promise((r) => setTimeout(r, RETRY_DELAY_MS));
    }
  }
  return { statusCode: 429, data: null };
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
    const first = await fetchJson(`${API_BASE}/api/ships?page=1&pageSize=500`);
    let ships = Array.isArray(first?.items) ? first.items : (Array.isArray(first) ? first : []);
    const total = first?.total ?? ships.length;
    if (total > 500) {
      for (let p = 2; p <= Math.ceil(total / 500); p++) {
        const page = await fetchJson(`${API_BASE}/api/ships?page=${p}&pageSize=500`);
        ships = ships.concat(page?.items || []);
      }
    }
    const withImage = ships.filter((s) => s.imageUrl && String(s.imageUrl).startsWith('http'));
    console.log('[populate-images] Connected. Ships:', ships.length, ', with image URLs:', withImage.length);

    let stored = 0;
    const total = withImage.length;
    for (let i = 0; i < withImage.length; i++) {
      const ship = withImage[i];
      const index = i + 1;
      try {
        const imgRes = await fetchImageWithRetry(ship.imageUrl, ship.name, index, total);
        if (imgRes.statusCode !== 200 || !imgRes.data || imgRes.data.length < 100) {
          console.log(`  [${index}/${total}] Skip: ${ship.name} - fetch HTTP ${imgRes.statusCode}`);
          await new Promise((r) => setTimeout(r, DELAY_MS));
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
      await new Promise((r) => setTimeout(r, DELAY_MS));
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
