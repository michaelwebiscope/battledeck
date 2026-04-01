#!/usr/bin/env node
/**
 * Naval Archive traffic simulator: endpoint sweep + realistic browser flows, many concurrent users.
 *
 * Base URL: --base-url OR terraform output vm_public_ip (https://IP)
 *
 * Usage:
 *   cd NavalArchive.Web && node scripts/traffic-simulator.mjs
 *   node scripts/traffic-simulator.mjs --parallel 15 --duration 300 --base-url https://1.2.3.4
 *   npm run traffic-sim -- --parallel 10
 */

import { chromium, request as playwrightRequest } from 'playwright';
import { execSync } from 'child_process';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const WEB_ROOT = join(__dirname, '..');
const REPO_ROOT = join(WEB_ROOT, '..'); // battledeck root (parent of NavalArchive.Web)

function parseArgs() {
  const out = {
    baseUrl: null,
    parallel: 4,
    durationSec: 120,
    targetUsers: null,
    sweep: true,
    headless: true,
    terraformDir: join(REPO_ROOT, 'terraform-navalansible')
  };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--base-url' && argv[i + 1]) {
      out.baseUrl = argv[++i].replace(/\/$/, '');
    } else if (a === '--parallel' && argv[i + 1]) {
      out.parallel = Math.max(1, parseInt(argv[++i], 10) || 4);
    } else if (a === '--duration' && argv[i + 1]) {
      out.durationSec = Math.max(5, parseInt(argv[++i], 10) || 120);
    } else if (a === '--users' && argv[i + 1]) {
      out.targetUsers = Math.max(1, parseInt(argv[++i], 10) || 100);
    } else if (a === '--no-sweep') {
      out.sweep = false;
    } else if (a === '--headed') {
      out.headless = false;
    } else if (a === '--terraform-dir' && argv[i + 1]) {
      out.terraformDir = argv[++i];
    } else if (a === '--help' || a === '-h') {
      console.log(`
Naval Archive traffic simulator

  --base-url URL       Override target (default: https://<terraform vm_public_ip>)
  --parallel N         Concurrent browser workers (default: 4)
  --duration SEC       Wall-clock seconds each worker runs (default: 120)
  --users N            Run exactly N browser sessions total (split across workers; finishes when all done)
  --no-sweep           Skip one-shot HTTP endpoint sweep
  --headed             Show browser windows
  --terraform-dir PATH Path to terraform dir (default: ../terraform-navalansible)

Examples:
  npm run traffic-sim -- --parallel 20 --duration 600
  node scripts/traffic-simulator.mjs --base-url https://127.0.0.1:3000 --parallel 2
`);
      process.exit(0);
    }
  }
  return out;
}

function getIpFromTerraform(tfDir) {
  try {
    return execSync(`terraform -chdir="${tfDir}" output -raw vm_public_ip`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe']
    }).trim();
  } catch {
    return null;
  }
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

/** Paths hit with Playwright request API (fast). */
const HTTP_GET_PATHS = [
  '/health',
  '/api/entity-types',
  '/api/trace',
  '/api/ships?pageSize=3&page=1',
  '/api/stats',
  '/api/classes',
  '/api/timeline',
  '/',
  '/fleet',
  '/trace',
  '/compare',
  '/classes',
  '/captains',
  '/stats',
  '/timeline',
  '/gallery',
  '/logs',
  '/donate',
  '/login',
  '/members',
  '/payment-account',
  '/cart',
  '/checkout',
  '/simulation',
  '/verify-member',
  '/membership',
  '/discover',
  '/fleet/search?q=war',
  '/ships/1',
  '/ships/9',
  '/classes/1',
  '/captains/1',
  '/api/images/audit',
  '/api/images/populate-queue',
  '/api/accounts/me',
  '/api/payment/methods',
  '/payment-account',
  '/membership'
];

async function runEndpointSweep(baseURL, log) {
  const ctx = await playwrightRequest.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    timeout: 30000
  });
  const results = { ok: 0, fail: 0, detail: [] };
  for (const path of HTTP_GET_PATHS) {
    try {
      const res = await ctx.get(path);
      const ok = res.ok() || res.status() === 302 || res.status() === 301;
      if (ok) results.ok++;
      else {
        results.fail++;
        results.detail.push(`${path} -> ${res.status()}`);
      }
    } catch (e) {
      results.fail++;
      results.detail.push(`${path} -> ERR ${e.message}`);
    }
  }
  await ctx.dispose();
  log(`Sweep: ${results.ok} ok, ${results.fail} failed`);
  if (results.detail.length) {
    results.detail.slice(0, 25).forEach((l) => log('  ' + l));
    if (results.detail.length > 25) log(`  ... +${results.detail.length - 25} more`);
  }
}

const FLOWS = [
  {
    name: 'home->fleet->ship',
    weight: 3,
    run: async (page) => {
      await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200 + Math.random() * 400);
      await page.goto('/fleet', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200 + Math.random() * 400);
      const link = page.locator('a[href^="/ships/"]').first();
      if ((await link.count()) > 0) {
        await Promise.all([page.waitForNavigation({ timeout: 60000 }).catch(() => {}), link.click()]);
      } else {
        await page.goto('/ships/9', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
      }
    }
  },
  {
    name: 'explore-mix',
    weight: 2,
    run: async (page) => {
      const paths = ['/timeline', '/stats', '/gallery', '/classes', '/captains'];
      await page.goto(paths[Math.floor(Math.random() * paths.length)], {
        waitUntil: 'domcontentloaded',
        timeout: 60000
      });
      await sleep(300 + Math.random() * 500);
      await page.goto('/compare', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
    }
  },
  {
    name: 'trace',
    weight: 1,
    run: async (page) => {
      await page.goto('/trace', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(500);
    }
  },
  {
    name: 'support-pages',
    weight: 2,
    run: async (page) => {
      await page.goto('/donate', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200);
      await page.goto('/login', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200);
      await page.goto('/members', { waitUntil: 'domcontentloaded', timeout: 60000 });
    }
  },
  {
    name: 'commerce-path',
    weight: 1,
    run: async (page) => {
      await page.goto('/fleet', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200);
      await page.goto('/cart', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(200);
      await page.goto('/checkout', { waitUntil: 'domcontentloaded', timeout: 60000 });
    }
  },
  {
    name: 'logs-simulation',
    weight: 1,
    run: async (page) => {
      await page.goto('/logs', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await sleep(300);
      await page.goto('/simulation', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
    }
  },
  {
    name: 'account-register-funds',
    weight: 2,
    run: async (page, baseURL) => {
      const ctx = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 15000 });
      // Register a fresh account — response contains apiKey (shown only once)
      const name = `SimUser_${Date.now()}_${Math.floor(Math.random() * 9999)}`;
      const email = `${name.toLowerCase()}@sim.test`;
      const reg = await ctx.post('/api/accounts/register', {
        data: { name, email, tier: 'sandbox' },
        headers: { 'Content-Type': 'application/json' }
      }).catch(() => null);
      const regStatus = reg?.status() ?? 'err';

      let apiKey = null;
      if (reg?.status() === 201) {
        const body = await reg.json().catch(() => null);
        apiKey = body?.apiKey ?? null;
      }

      let meStatus = 'skip';
      let fundsStatus = 'skip';
      if (apiKey) {
        // GET /api/accounts/me with real API key
        const me = await ctx.get('/api/accounts/me', {
          headers: { 'X-API-Key': apiKey }
        }).catch(() => null);
        meStatus = me?.status() ?? 'err';

        // Add funds to the new account
        const funds = await ctx.post('/api/accounts/funds', {
          data: { amount: 100 },
          headers: { 'Content-Type': 'application/json', 'X-API-Key': apiKey }
        }).catch(() => null);
        fundsStatus = funds?.status() ?? 'err';
      }

      await ctx.dispose();
      await page.goto('/payment-account', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
      await sleep(200);
      await page.goto('/membership', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
      return `register=${regStatus} me=${meStatus} funds=${fundsStatus}`;
    }
  },
  {
    name: 'payment-simulate',
    weight: 2,
    run: async (page, baseURL) => {
      const ctx = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 15000 });

      // Register an account first to get an API key
      const name = `PaySim_${Date.now()}_${Math.floor(Math.random() * 9999)}`;
      const reg = await ctx.post('/api/accounts/register', {
        data: { name, email: `${name.toLowerCase()}@sim.test`, tier: 'sandbox' },
        headers: { 'Content-Type': 'application/json' }
      }).catch(() => null);
      const apiKey = reg?.status() === 201 ? (await reg.json().catch(() => null))?.apiKey : null;

      // Simulate a payment (public endpoint — no auth needed)
      const sim = await ctx.post('/api/payments/simulate', {
        data: {
          amount: Math.floor(Math.random() * 200) + 10,
          currency: 'USD',
          description: `Traffic sim purchase ${Date.now()}`
        },
        headers: { 'Content-Type': 'application/json' }
      }).catch(() => null);
      const simStatus = sim?.status() ?? 'err';

      // Check transaction status if simulate returned one
      let statusCheck = 'skip';
      if (sim?.status() === 200 || sim?.status() === 201) {
        const simBody = await sim.json().catch(() => null);
        const txId = simBody?.transactionId ?? simBody?.id ?? null;
        if (txId) {
          const st = await ctx.get(`/api/payment/status/${txId}`).catch(() => null);
          statusCheck = st?.status() ?? 'err';
        }
      }

      // List payment methods with API key (authenticated)
      let methodsStatus = 'skip';
      if (apiKey) {
        const methods = await ctx.get('/api/payment/methods', {
          headers: { 'X-API-Key': apiKey }
        }).catch(() => null);
        methodsStatus = methods?.status() ?? 'err';

        // Payment history
        await ctx.get('/api/payment/history', {
          headers: { 'X-API-Key': apiKey }
        }).catch(() => null);
      }

      await ctx.dispose();
      await page.goto('/checkout', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
      await sleep(200);
      return `simulate=${simStatus} txStatus=${statusCheck} methods=${methodsStatus}`;
    }
  },
  {
    name: 'image-populate-queue',
    weight: 1,
    run: async (page, baseURL) => {
      const ctx = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 30000 });
      // Check what's in the populate queue (read-only — safe to call frequently)
      const queue = await ctx.get('/api/images/populate-queue').catch(() => null);
      const queueStatus = queue?.status() ?? 'err';

      // Audit images (read-only)
      const audit = await ctx.get('/api/images/audit').catch(() => null);
      const auditStatus = audit?.status() ?? 'err';

      // Trigger image populate only occasionally — this hits Wikipedia externally so
      // we gate it to roughly once every 5 minutes across all workers using a time bucket.
      let popStatus = 'skip';
      const bucketMinute = Math.floor(Date.now() / 300000); // 5-min bucket
      if (bucketMinute % 5 === 0) {
        const shipId = Math.floor(Math.random() * 10) + 1;
        const pop = await ctx.post(`/api/images/populate/ship/${shipId}`, {
          headers: { 'Content-Type': 'application/json' }
        }).catch(() => null);
        popStatus = pop?.status() ?? 'err';
        await sleep(2000); // extra breathing room after external call
      }

      await ctx.dispose();
      await page.goto('/gallery', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
      await sleep(500);
      return `queue=${queueStatus} audit=${auditStatus} populate=${popStatus}`;
    }
  }
];

function pickFlow() {
  const total = FLOWS.reduce((s, f) => s + f.weight, 0);
  let r = Math.random() * total;
  for (const f of FLOWS) {
    r -= f.weight;
    if (r <= 0) return f;
  }
  return FLOWS[0];
}

async function workerLoop({ browser, baseURL, workerId, endTime, maxSessions, log, errors }) {
  let sessions = 0;
  while (sessions < maxSessions && Date.now() < endTime) {
    const context = await browser.newContext({
      baseURL,
      ignoreHTTPSErrors: true,
      viewport: { width: 1280, height: 720 }
    });
    const page = await context.newPage();
    const flow = pickFlow();
    const t0 = Date.now();
    try {
      const detail = await flow.run(page, baseURL);
      sessions++;
      log(`worker ${workerId} ${flow.name} ok ${Date.now() - t0}ms${detail ? ' ' + detail : ''}`);
    } catch (e) {
      errors.push({ workerId, flow: flow.name, err: e.message });
      log(`worker ${workerId} ${flow.name} FAIL: ${e.message}`);
    } finally {
      await context.close();
    }

    await sleep(150 + Math.random() * 800);
  }
  return sessions;
}

async function main() {
  const opts = parseArgs();
  let baseUrl = opts.baseUrl;
  if (!baseUrl) {
    const ip = getIpFromTerraform(opts.terraformDir);
    if (!ip) {
      console.error(
        'ERROR: No --base-url and terraform output vm_public_ip failed. Set --base-url or run from repo with terraform state.'
      );
      process.exit(1);
    }
    baseUrl = `https://${ip}`;
  }

  const log = (...a) => console.log(new Date().toISOString(), ...a);

  log(`Target ${baseUrl} | parallel=${opts.parallel} duration=${opts.durationSec}s`);

  if (opts.sweep) {
    log('--- HTTP endpoint sweep ---');
    await runEndpointSweep(baseUrl, log);
  }

  const browser = await chromium.launch({ headless: opts.headless });
  const errors = [];
  const endTime = Date.now() + opts.durationSec * 1000;

  let perWorkerBase = 0;
  let remainder = 0;
  if (opts.targetUsers != null) {
    perWorkerBase = Math.floor(opts.targetUsers / opts.parallel);
    remainder = opts.targetUsers % opts.parallel;
    log(`Target sessions: ${opts.targetUsers} (split across workers)`);
  }

  const workers = Array.from({ length: opts.parallel }, (_, wid) => {
    const extra = wid < remainder ? 1 : 0;
    const maxSessions =
      opts.targetUsers != null ? perWorkerBase + extra : Number.MAX_SAFE_INTEGER;
    return workerLoop({
      browser,
      baseURL: baseUrl,
      workerId: wid,
      endTime,
      maxSessions,
      log,
      errors
    });
  });

  const sessionCounts = await Promise.all(workers);
  const totalSessions = sessionCounts.reduce((a, b) => a + b, 0);
  await browser.close();

  log(`--- Done: ~${totalSessions} browser sessions, ${errors.length} flow errors ---`);
  if (errors.length) {
    errors.slice(0, 15).forEach((e) => log(`  err`, e));
    if (errors.length > 15) log(`  ... +${errors.length - 15} more`);
    process.exitCode = 1;
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
