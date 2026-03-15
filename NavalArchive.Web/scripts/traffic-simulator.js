#!/usr/bin/env node
/**
 * Traffic simulator – simulates real users: visit pages, scroll, click links/buttons.
 * Uses Playwright. Run from NavalArchive.Web: node scripts/traffic-simulator.js
 * Or: BASE_URL=https://yoursite.com node scripts/traffic-simulator.js
 *
 * Options (env):
 *   BASE_URL     – site to hit (default http://localhost:3000)
 *   SESSIONS     – number of user sessions (default 5)
 *   DURATION_MS  – max run time per session (default 60000)
 */

const { chromium } = require('playwright');

const BASE_URL = process.env.BASE_URL || 'http://localhost:3000';
const SESSIONS = parseInt(process.env.SESSIONS || '5', 10);
const DURATION_MS = parseInt(process.env.DURATION_MS || '60000', 10);

const PATHS = [
  '/',
  '/fleet',
  '/donate',
  '/members',
  '/compare',
  '/timeline',
  '/stats',
  '/gallery',
  '/classes',
  '/captains',
  '/logs',
  '/simulation',
  '/cart',
  '/checkout',
  '/payment-account',
  '/login',
  '/trace',
];

const VIEWPORTS = [
  { width: 1920, height: 1080 },
  { width: 1366, height: 768 },
  { width: 1280, height: 720 },
  { width: 414, height: 896 },
  { width: 390, height: 844 },
];

const USER_AGENTS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
];

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

function rand(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

function delay(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function humanDelay(minMs = 500, maxMs = 2500) {
  await delay(rand(minMs, maxMs));
}

async function scrollPage(page) {
  const steps = rand(2, 6);
  for (let i = 0; i < steps; i++) {
    const deltaY = rand(200, 600);
    await page.mouse.wheel(0, deltaY);
    await humanDelay(300, 1200);
  }
  // Sometimes scroll back up a bit
  if (Math.random() < 0.4) {
    await page.mouse.wheel(0, -rand(100, 400));
    await humanDelay(200, 800);
  }
}

async function clickSomeLinks(page, baseUrl) {
  const links = await page.$$('a[href^="/"]:not([href="#"])');
  const toClick = links.filter(() => Math.random() < 0.35).slice(0, 3);
  for (const a of toClick) {
    try {
      const href = await a.getAttribute('href');
      if (!href || href.startsWith('http')) continue;
      await a.scrollIntoViewIfNeeded();
      await humanDelay(200, 600);
      await a.click({ timeout: 3000 }).catch(() => {});
      await humanDelay(500, 2000);
    } catch (_) {}
  }
}

async function clickButtons(page) {
  const buttons = await page.$$('button:not([disabled]), .btn:not([disabled]), input[type="submit"]');
  const toClick = buttons.filter(() => Math.random() < 0.25).slice(0, 2);
  for (const btn of toClick) {
    try {
      await btn.scrollIntoViewIfNeeded();
      await humanDelay(200, 500);
      await btn.click({ timeout: 2000 }).catch(() => {});
      await humanDelay(300, 1500);
    } catch (_) {}
  }
}

async function runOneSession(sessionId) {
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox'],
  });
  const viewport = pick(VIEWPORTS);
  const userAgent = pick(USER_AGENTS);
  const context = await browser.newContext({
    viewport,
    userAgent,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();

  const start = Date.now();
  let pagesVisited = 0;

  try {
    while (Date.now() - start < DURATION_MS && pagesVisited < 8) {
      const path = pick(PATHS);
      const url = `${BASE_URL.replace(/\/$/, '')}${path}`;
      try {
        await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 15000 });
        pagesVisited++;
        await humanDelay(800, 2500);
        await scrollPage(page);
        await humanDelay(500, 1500);
        await clickSomeLinks(page, BASE_URL);
        await humanDelay(400, 1200);
        await clickButtons(page);
      } catch (e) {
        console.error(`[session ${sessionId}] ${url}: ${e.message}`);
      }
      await humanDelay(1000, 4000);
    }
    console.log(`[session ${sessionId}] done, visited ${pagesVisited} pages`);
  } finally {
    await browser.close();
  }
}

async function main() {
  console.log(`Traffic simulator: ${BASE_URL}, ${SESSIONS} sessions, ~${DURATION_MS / 1000}s each`);
  for (let s = 0; s < SESSIONS; s++) {
    await runOneSession(s + 1);
    if (s < SESSIONS - 1) await delay(rand(2000, 8000));
  }
  console.log('Done.');
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
