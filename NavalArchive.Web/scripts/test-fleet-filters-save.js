#!/usr/bin/env node
/**
 * Integration: POST /admin/fleet-filters/config + fleet page “add filter” UI markers.
 * Expects the web app on http://127.0.0.1:3000 (WEB_URL override ok).
 * Restores config/fleet-filter-hidden.json after run.
 *
 * Run: npm run test:fleet-save
 */
const fs = require('fs');
const path = require('path');
const http = require('http');
const querystring = require('querystring');

const WEB_URL = process.env.WEB_URL || 'http://127.0.0.1:3000';
const HIDDEN_PATH = path.join(__dirname, '..', 'config', 'fleet-filter-hidden.json');
const TEST_KEY_A = 'zz.test.filter.key';
const TEST_KEY_B = 'zz.test.filter.keyb';

function parseUrl(base) {
  const u = new URL(base);
  return { hostname: u.hostname, port: u.port || (u.protocol === 'https:' ? 443 : 80) };
}

function request(method, pathAndQuery, headers, body) {
  const { hostname, port } = parseUrl(WEB_URL);
  return new Promise((resolve, reject) => {
    const req = http.request(
      { hostname, port, method, path: pathAndQuery, headers: headers || {} },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString('utf8') });
        });
      }
    );
    req.on('error', reject);
    if (body) req.write(body);
    req.end();
  });
}

function postPayload(obj) {
  const payload = querystring.stringify({ payload: JSON.stringify(obj) });
  return {
    body: payload,
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      Accept: 'application/json',
      'Content-Length': Buffer.byteLength(payload)
    }
  };
}

async function main() {
  let previous = '[]';
  try {
    previous = fs.readFileSync(HIDDEN_PATH, 'utf8');
  } catch (e) {}

  const health = await request('GET', '/health', {});
  if (health.status !== 200) {
    console.error('FAIL: /health expected 200, got', health.status, '— start NavalArchive.Web on', WEB_URL);
    process.exit(1);
  }

  const { body: p1, headers: h1 } = postPayload({ hiddenKeys: [TEST_KEY_A, TEST_KEY_B] });
  const r1 = await request('POST', '/admin/fleet-filters/config', h1, p1);
  if (r1.status !== 200) {
    console.error('FAIL: save expected 200, got', r1.status, r1.text.slice(0, 300));
    process.exit(1);
  }

  const rFleet = await request('GET', '/fleet', { Accept: 'text/html' });
  if (rFleet.status !== 200) {
    console.error('FAIL: GET /fleet expected 200, got', rFleet.status);
    process.exit(1);
  }
  const html = rFleet.text;
  if (!html.includes('fleet-filter-dock__hidden-tools')) {
    console.error('FAIL: fleet HTML missing .fleet-filter-dock__hidden-tools (hidden toolbar)');
    process.exit(1);
  }
  if (!html.includes('fleetAddFilterDropdown')) {
    console.error('FAIL: fleet HTML missing #fleetAddFilterDropdown');
    process.exit(1);
  }
  if (!html.includes('fleet-add-filter-item') || !html.includes(TEST_KEY_A) || !html.includes(TEST_KEY_B)) {
    console.error('FAIL: fleet HTML missing add-filter items for test keys');
    process.exit(1);
  }
  if (!html.includes('fleetRestoreHiddenFiltersBtn')) {
    console.error('FAIL: fleet HTML missing Restore all button');
    process.exit(1);
  }

  const { body: p2, headers: h2 } = postPayload({ hiddenKeys: [TEST_KEY_B] });
  const r2 = await request('POST', '/admin/fleet-filters/config', h2, p2);
  if (r2.status !== 200) {
    console.error('FAIL: partial unhide expected 200, got', r2.status);
    process.exit(1);
  }
  const arr = JSON.parse(fs.readFileSync(HIDDEN_PATH, 'utf8'));
  if (arr.length !== 1 || arr[0] !== TEST_KEY_B) {
    console.error('FAIL: after partial restore expected hidden', [TEST_KEY_B], 'got', arr);
    process.exit(1);
  }

  const { body: p3, headers: h3 } = postPayload({ hiddenKeys: [] });
  const r3 = await request('POST', '/admin/fleet-filters/config', h3, p3);
  if (r3.status !== 200) {
    console.error('FAIL: clear hidden expected 200');
    process.exit(1);
  }

  fs.writeFileSync(HIDDEN_PATH, previous, 'utf8');
  console.log('OK: fleet-filter save + fleet page add-filter UI + partial hidden update on', WEB_URL);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
