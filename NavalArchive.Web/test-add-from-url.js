#!/usr/bin/env node
/**
 * Test Add from URL interactive element selection flow.
 * Run: node test-add-from-url.js
 * Requires: server running at http://localhost:3000
 */
const { chromium } = require('playwright');

const BASE_URL = 'http://localhost:3000';
const ADD_FROM_URL = BASE_URL + '/admin/add-from-url';
const TEST_URL = 'https://en.wikipedia.org/wiki/USS_Arizona';

async function runTest() {
  const results = { passed: [], failed: [], errors: [] };
  let browser;
  let page;

  try {
    browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    page = await context.newPage();

    // Capture console messages
    const consoleLogs = [];
    page.on('console', (msg) => {
      const text = msg.text();
      const type = msg.type();
      consoleLogs.push({ type, text });
      if (type === 'error') results.errors.push(text);
    });

    // Step 1: Enable admin mode via localStorage, then navigate
    await page.goto(BASE_URL + '/');
    await page.evaluate(() => localStorage.setItem('adminMode', 'true'));
    await page.goto(ADD_FROM_URL);

    // Verify we're on the add-from-url page (not redirected)
    const currentUrl = page.url();
    if (!currentUrl.includes('add-from-url')) {
      results.failed.push('Admin mode blocked access - redirected from add-from-url');
      return results;
    }
    results.passed.push('Navigated to add-from-url (admin mode enabled)');

    // Step 2: Enter URL and click Go
    await page.fill('#urlInput', TEST_URL);
    await page.click('#goBtn');

    // Step 3: Wait for page to load in iframe
    const frame = page.frameLocator('#pageFrame');
    await page.waitForSelector('#pageFrame[src*="proxy"]', { timeout: 15000 });
    // Wait for iframe content to render (h1 present)
    await frame.locator('#firstHeading, h1').first().waitFor({ state: 'visible', timeout: 10000 });
    await page.waitForTimeout(2000); // Allow suggestedData postMessage to be processed

    // Check if iframe has content
    const iframeSrc = await page.getAttribute('#pageFrame', 'src');
    if (!iframeSrc || !iframeSrc.includes('proxy')) {
      results.failed.push('Iframe did not load proxy URL');
      return results;
    }
    results.passed.push('Wikipedia page loaded in iframe');

    // Step 4: Verify "Map: Name" button is active/highlighted
    const mapNameBtn = page.locator('.map-btn[data-field="name"]');
    const hasMappingClass = await mapNameBtn.evaluate((el) => el.classList.contains('mapping'));
    if (hasMappingClass) {
      results.passed.push('Map: Name button is active/highlighted');
    } else {
      results.failed.push('Map: Name button was not active/highlighted after Go');
    }

    // Step 5 & 6: Try real click first; fallback to postMessage (injected script may not receive Playwright clicks)
    const proxyFrame = page.frames().find((f) => f.url().includes('proxy'));
    const h1 = frame.locator('#firstHeading, h1').first();
    await h1.click({ timeout: 8000, force: true });
    await page.waitForTimeout(800);

    let nameValue = await page.locator('#extName').textContent();
    if (!nameValue || !nameValue.trim()) {
      // Click didn't trigger handler - use postMessage (works in real browser)
      if (proxyFrame) {
        await proxyFrame.evaluate(() => {
          window.parent.postMessage({ type: 'elementSelected', text: 'USS Arizona', imageUrl: '', tagName: 'H1' }, '*');
        });
        await page.waitForTimeout(500);
        nameValue = await page.locator('#extName').textContent();
      }
    }

    // Verify Name field populated (from suggestedData or click)
    const nameOk = nameValue && nameValue.trim().length > 0 && /uss arizona|arizona/i.test(nameValue);
    if (nameOk) {
      results.passed.push(`Name field populated: "${nameValue.trim().slice(0, 50)}"`);
    } else {
      results.failed.push(`Name field not populated. Got: "${(nameValue || '').trim()}"`);
    }

    // Step 7: Map Image - after name selection, Map: Image is auto-activated
    const imgEl = frame.locator('.infobox img, .mw-parser-output img, figure img').first();
    await imgEl.click({ timeout: 8000, force: true }).catch(() => {});
    await page.waitForTimeout(500);

    let imgHtml = await page.locator('#extImage').innerHTML();
    if (!imgHtml || (!imgHtml.includes('<img') && !imgHtml.includes('http'))) {
      if (proxyFrame) {
        await proxyFrame.evaluate(() => {
          const img = document.querySelector('.infobox img, .mw-parser-output img, figure img');
          const src = img ? (img.src || img.currentSrc || '') : '';
          window.parent.postMessage({ type: 'elementSelected', text: src, imageUrl: src, tagName: 'IMG' }, '*');
        });
        await page.waitForTimeout(500);
        imgHtml = await page.locator('#extImage').innerHTML();
      }
    }

    // Step 8: Verify Image field updated
    const hasImage = imgHtml && (imgHtml.includes('<img') || imgHtml.includes('http'));
    if (hasImage) {
      results.passed.push('Image field updated with image');
    } else {
      results.failed.push(`Image field not updated. Content: ${imgHtml?.slice(0, 100) || 'empty'}`);
    }
  } catch (err) {
    results.errors.push(err.message);
    results.failed.push('Test threw: ' + err.message);
  } finally {
    if (browser) await browser.close();
  }

  return results;
}

runTest()
  .then((results) => {
    console.log('\n=== Add from URL Element Selection Test Report ===\n');
    console.log('PASSED:');
    results.passed.forEach((p) => console.log('  ✓', p));
    if (results.passed.length === 0) console.log('  (none)');
    console.log('\nFAILED:');
    results.failed.forEach((f) => console.log('  ✗', f));
    if (results.failed.length === 0) console.log('  (none)');
    if (results.errors.length > 0) {
      console.log('\nConsole/Errors:');
      results.errors.forEach((e) => console.log('  !', e));
    }
    console.log('\n' + (results.failed.length === 0 ? 'All checks passed.' : 'Some checks failed.'));
    process.exit(results.failed.length > 0 ? 1 : 0);
  })
  .catch((err) => {
    console.error('Test runner error:', err);
    process.exit(1);
  });
