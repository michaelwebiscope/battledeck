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
  '/captains/1'
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
      await flow.run(page);
      sessions++;
      log(`worker ${workerId} ${flow.name} ok ${Date.now() - t0}ms`);
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
