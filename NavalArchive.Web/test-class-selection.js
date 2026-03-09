#!/usr/bin/env node
/**
 * Test Add from URL class selection bug.
 * Reproduces: User selects a catalog class (e.g. Pennsylvania) but ship saves with page class (e.g. Bismarck-class).
 * Run: node test-class-selection.js
 * Requires: server + API running (scripts/run-local.sh or similar)
 */
const { chromium } = require('playwright');

const BASE_URL = 'http://localhost:3000';
const ADD_FROM_URL = BASE_URL + '/admin/add-from-url';
// USS Arizona is Pennsylvania-class; Bismarck is Bismarck-class
const TEST_URL = 'https://en.wikipedia.org/wiki/USS_Arizona';

async function runTest() {
  const report = {
    classSelectedBeforeSave: null,
    classSavedWith: null,
    defaultClassInDropdown: null,
    dropdownOptions: [],
    suggestedDataTiming: [],
    refreshClassDropdownCalls: [],
    saveHandlerClassVal: null,
    errors: [],
    consoleLogs: []
  };

  let browser;
  let page;

  try {
    browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    page = await context.newPage();

    // Capture all console messages
    page.on('console', (msg) => {
      const text = msg.text();
      report.consoleLogs.push({ type: msg.type(), text });
      if (text.includes('[AddFromUrl]')) {
        if (text.includes('suggestedData')) report.suggestedDataTiming.push(text);
        if (text.includes('refreshClassDropdown')) report.refreshClassDropdownCalls.push(text);
        if (text.includes('SAVE: classVal')) report.saveHandlerClassVal = text;
      }
    });

    // Step 1: Enable admin mode and navigate
    await page.goto(BASE_URL + '/');
    await page.evaluate(() => localStorage.setItem('adminMode', 'true'));
    await page.goto(ADD_FROM_URL);

    if (!page.url().includes('add-from-url')) {
      report.errors.push('Redirected from add-from-url - admin mode?');
      return report;
    }

    // Step 2: Enter URL and click Go
    await page.fill('#urlInput', TEST_URL);
    await page.click('#goBtn');

    // Step 3: Wait for iframe and content
    await page.waitForSelector('#pageFrame[src*="proxy"]', { timeout: 15000 });
    const frame = page.frameLocator('#pageFrame');
    await frame.locator('#firstHeading, h1').first().waitFor({ state: 'visible', timeout: 10000 });
    // Wait for suggestedData (iframe DOMContentLoaded) - can arrive late
    await page.waitForTimeout(3000);

    // Step 4: Observe Class dropdown - default selection and options
    const classSelect = page.locator('#extClass');
    report.defaultClassInDropdown = await classSelect.inputValue();
    report.dropdownOptions = await classSelect.locator('option').evaluateAll((opts) =>
      opts.map((o) => ({ value: o.value, text: o.textContent?.trim().slice(0, 60) }))
    );

    // Step 5: Change Class to a catalog class (not page class)
    const options = report.dropdownOptions;
    let catalogOption = options.find((o) => /^\d+$/.test(o.value) && o.text && /pennsylvania/i.test(o.text));
    if (!catalogOption) catalogOption = options.find((o) => /^\d+$/.test(o.value) && o.value !== '1');
    if (!catalogOption) {
      report.errors.push('No catalog class option found in dropdown');
    } else {
      await classSelect.selectOption({ value: catalogOption.value });
      report.classSelectedBeforeSave = catalogOption.value;
      report.catalogOptionText = catalogOption.text;
      await page.waitForTimeout(500);
    }

    // Step 6: Ensure Name is filled (from suggestedData or default)
    let nameVal = await page.locator('#extName').textContent();
    if (!nameVal || !nameVal.trim()) {
      // Simulate suggestedData or click - use postMessage
      const proxyFrame = page.frames().find((f) => f.url().includes('proxy'));
      if (proxyFrame) {
        await proxyFrame.evaluate(() => {
          const h1 = document.querySelector('h1');
          const name = h1 ? h1.innerText.trim() : 'USS Arizona';
          window.parent.postMessage({ type: 'elementSelected', text: name, imageUrl: '', tagName: 'H1' }, '*');
        });
        await page.waitForTimeout(500);
      }
      nameVal = await page.locator('#extName').textContent();
    }

    // Use unique name to avoid conflicts
    const uniqueName = 'Test Bismarck ' + Date.now();
    await page.evaluate((n) => {
      document.getElementById('extName').textContent = n;
    }, uniqueName);

    // Step 7: Click Save
    await page.click('#saveBtn');
    await page.waitForTimeout(2000);

    // Step 8: Check redirect - we should land on /ships/:id
    const finalUrl = page.url();
    const shipIdMatch = finalUrl.match(/\/ships\/(\d+)/);
    if (shipIdMatch) {
      const shipId = shipIdMatch[1];
      // Fetch ship details to see saved class
      // Web server proxies /api to backend
      const shipRes = await page.request.get(`${BASE_URL}/api/ships/${shipId}`);
      if (shipRes.ok()) {
        const ship = await shipRes.json();
        report.classSavedWith = ship.classId ?? ship.class?.id ?? ship.ClassId ?? ship.Class?.Id;
        report.shipName = ship.name ?? ship.Name;
        report.shipId = shipId;
      }
    } else {
      const statusText = await page.locator('#statusMsg').textContent();
      report.errors.push('Save may have failed. URL: ' + finalUrl + ' Status: ' + statusText);
    }
  } catch (err) {
    report.errors.push(err.message);
  } finally {
    if (browser) await browser.close();
  }

  return report;
}

runTest()
  .then((report) => {
    console.log('\n=== Add from URL Class Selection Test Report ===\n');
    console.log('Default class in dropdown:', report.defaultClassInDropdown);
    console.log('Class selected before Save:', report.classSelectedBeforeSave);
    console.log('Class ship was saved with:', report.classSavedWith);
    console.log('Ship:', report.shipName, '(id:', report.shipId + ')');
    console.log('\nDropdown options (first 10):');
    (report.dropdownOptions || []).slice(0, 10).forEach((o) => console.log('  ', o.value, '->', o.text));
    console.log('\nsuggestedData postMessage logs:');
    (report.suggestedDataTiming || []).forEach((l) => console.log('  ', l));
    console.log('\nrefreshClassDropdown logs:');
    (report.refreshClassDropdownCalls || []).forEach((l) => console.log('  ', l));
    console.log('\nSave handler classVal:', report.saveHandlerClassVal);
    if (report.errors.length) {
      console.log('\nErrors:');
      report.errors.forEach((e) => console.log('  !', e));
    }
    console.log('\n---');
    const mismatch = report.classSelectedBeforeSave && report.classSavedWith &&
      String(report.classSelectedBeforeSave) !== String(report.classSavedWith);
    if (mismatch) {
      console.log('BUG REPRODUCED: Selected', report.classSelectedBeforeSave, 'but saved with', report.classSavedWith);
    } else {
      console.log('Class selection OK (or test incomplete)');
    }
    process.exit(mismatch ? 1 : 0);
  })
  .catch((err) => {
    console.error('Test error:', err);
    process.exit(1);
  });
