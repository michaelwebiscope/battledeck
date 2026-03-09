/**
 * Test: After "imageUploaded" postMessage from search iframe, captain-detail page reloads.
 * Run: node test-captain-image-uploaded.js (requires server at localhost:3000, API at 5000)
 */
const { chromium } = require('playwright');

const BASE = 'http://localhost:3000';

(async () => {
  let passed = false;
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();

    // Enable admin mode so Edit button is visible
    await page.goto(BASE + '/captains', { waitUntil: 'networkidle' });
    await page.evaluate(() => {
      localStorage.setItem('adminMode', 'true');
      document.body.classList.add('admin-mode');
    });

    // Get first captain id from API
    const listRes = await page.goto(BASE + '/api/captains', { waitUntil: 'networkidle' });
    if (!listRes || listRes.status() !== 200) {
      console.log('FAIL: Could not load /api/captains');
      process.exit(1);
    }
    const list = await listRes.json();
    const captainId = list[0]?.id ?? list[0]?.Id;
    if (!captainId) {
      console.log('FAIL: No captain id in list');
      process.exit(1);
    }

    // Go to captain detail and re-apply admin mode (new page)
    await page.goto(BASE + '/captains/' + captainId, { waitUntil: 'networkidle' });
    await page.evaluate(() => {
      localStorage.setItem('adminMode', 'true');
      document.body.classList.add('admin-mode');
    });
    const urlBefore = page.url();

    // Open Edit Captain modal via the Edit button
    await page.click('#entityEditBtn');
    await page.waitForSelector('#entityEditModal.show', { timeout: 5000 });

    // Click "Change Image" to show search pane and load iframe
    await page.click('#entityEditImageBtn');
    await page.waitForSelector('#entityEditSearchPane[style*="block"]', { timeout: 3000 });
    const frame = page.frameLocator('#entityEditSearchFrame');
    await frame.locator('body').waitFor({ state: 'visible', timeout: 5000 }).catch(() => {});

    // Simulate the iframe sending imageUploaded (parent listens and reloads)
    await page.evaluate((id) => {
      window.postMessage({ type: 'imageUploaded', entity: 'captain', entityId: String(id) }, '*');
    }, captainId);

    // Wait for reload (navigation)
    await page.waitForURL(/\/captains\/\d+/, { waitUntil: 'load', timeout: 5000 });
    const urlAfter = page.url();

    if (urlAfter.includes('/captains/' + captainId)) {
      passed = true;
      console.log('PASS: Page reloaded after imageUploaded (URL: ' + urlAfter + ')');
    } else {
      console.log('FAIL: Expected reload on same captain page. Before: ' + urlBefore + ', After: ' + urlAfter);
    }
  } catch (err) {
    console.log('FAIL: ' + err.message);
  } finally {
    await browser.close();
  }
  process.exit(passed ? 0 : 1);
})();
