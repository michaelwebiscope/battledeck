#!/usr/bin/env node
/**
 * Manual test: Add from URL flow with browser automation.
 * 1. Navigate to localhost:3000, enable admin mode
 * 2. Go to /admin/add-from-url
 * 3. Enter Bismarck Wikipedia URL and click Go
 * 4. Snapshot: Class dropdown - what is showing? What is selected?
 * 5. Change Class to a different catalog class (e.g. Pennsylvania, Yamato)
 * 6. Snapshot: Confirm dropdown shows new selection
 * 7. Click Save
 * 8. After redirect, check ship detail page - what class does it show?
 * 9. Report: selected class vs saved class
 *
 * Run: node test-add-from-url-flow.js
 * Requires: server at localhost:3000, API at localhost:5000
 */
const { chromium } = require('playwright');

const BASE_URL = 'http://localhost:3000';
const ADD_FROM_URL = BASE_URL + '/admin/add-from-url';
const TEST_URL = 'https://en.wikipedia.org/wiki/German_battleship_Bismarck';

function snapshot(name, data) {
  console.log('\n--- Snapshot:', name, '---');
  console.log(JSON.stringify(data, null, 2));
  return data;
}

async function runTest() {
  const report = {
    step1_server: null,
    step2_admin_nav: null,
    step3_url_entered: null,
    step4_page_loaded: null,
    snapshot1_classDropdown: { selected: null, options: [], selectedText: null },
    snapshot2_afterClassChange: { selected: null, selectedText: null },
    step5_save: null,
    step6_shipDetail: { url: null, classShown: null, classId: null },
    finalReport: { selectedClass: null, savedClass: null, match: null, why: null },
    errors: []
  };

  let browser;
  let page;

  try {
    browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    page = await context.newPage();

    // Step 1: Ensure server is running
    const homeRes = await page.goto(BASE_URL + '/', { waitUntil: 'domcontentloaded' });
    report.step1_server = homeRes?.ok() ? 'OK' : 'Failed: ' + homeRes?.status();
    if (!homeRes?.ok()) {
      report.errors.push('Server not reachable at ' + BASE_URL);
      return report;
    }

    // Step 2: Enable admin mode and navigate to add-from-url
    await page.evaluate(() => localStorage.setItem('adminMode', 'true'));
    await page.goto(ADD_FROM_URL);
    report.step2_admin_nav = page.url().includes('add-from-url') ? 'OK' : 'Redirected away';
    if (!page.url().includes('add-from-url')) {
      report.errors.push('Could not reach add-from-url (admin mode?)');
      return report;
    }

    // Step 3: Enter URL and click Go
    await page.fill('#urlInput', TEST_URL);
    await page.click('#goBtn');
    report.step3_url_entered = 'OK';

    // Step 4: Wait for page to load
    await page.waitForSelector('#pageFrame[src*="proxy"]', { timeout: 20000 });
    const frame = page.frameLocator('#pageFrame');
    await frame.locator('#firstHeading, h1').first().waitFor({ state: 'visible', timeout: 15000 });
    // Wait for suggestedData postMessage from iframe
    await page.waitForTimeout(4000);
    report.step4_page_loaded = 'OK';

    // Snapshot 1: What is the Class dropdown showing? What is selected?
    const classSelect = page.locator('#extClass');
    const selectedVal1 = await classSelect.inputValue();
    const options = await classSelect.locator('option').evaluateAll((opts) =>
      opts.map((o) => ({ value: o.value, text: (o.textContent || '').trim().slice(0, 80) }))
    );
    const selectedOpt1 = options.find((o) => o.value === selectedVal1);
    report.snapshot1_classDropdown = {
      selected: selectedVal1,
      selectedText: selectedOpt1?.text || '(none)',
      optionsCount: options.length,
      options: options.slice(0, 15)
    };
    snapshot('1 - Class dropdown after page load', report.snapshot1_classDropdown);

    // Step 5: Change Class to a different catalog class (NOT Bismarck)
    // Find a catalog option: numeric value, and text that is NOT Bismarck-class
    const catalogOptions = options.filter(
      (o) => /^\d+$/.test(o.value) && o.value !== '1' && o.text && !/bismarck/i.test(o.text)
    );
    let targetOption = catalogOptions.find((o) => /pennsylvania|yamato|iowa|king george/i.test(o.text));
    if (!targetOption) targetOption = catalogOptions[0];
    if (!targetOption) {
      report.errors.push('No catalog class option found (non-Bismarck)');
    } else {
      await classSelect.selectOption({ value: targetOption.value });
      await page.waitForTimeout(500);
      report.snapshot2_afterClassChange = {
        selected: targetOption.value,
        selectedText: targetOption.text
      };
      report.finalReport.selectedClass = targetOption.value;
      report.finalReport.selectedClassText = targetOption.text;
      snapshot('2 - After changing Class dropdown', report.snapshot2_afterClassChange);
    }

    // Ensure Name is filled (Bismarck page should have it from suggestedData)
    let nameVal = await page.locator('#extName').textContent();
    if (!nameVal || !nameVal.trim()) {
      // Fallback: postMessage from iframe
      const proxyFrame = page.frames().find((f) => f.url().includes('proxy'));
      if (proxyFrame) {
        await proxyFrame.evaluate(() => {
          const h1 = document.querySelector('h1');
          const name = h1 ? h1.innerText.trim() : 'Bismarck';
          window.parent.postMessage({ type: 'elementSelected', text: name, imageUrl: '', tagName: 'H1' }, '*');
        });
        await page.waitForTimeout(500);
      }
    }
    // Use unique name to avoid duplicate key errors
    const uniqueName = 'Test Bismarck ' + Date.now();
    await page.evaluate((n) => {
      const el = document.getElementById('extName');
      if (el) el.textContent = n;
    }, uniqueName);

    // Step 6: Click Save
    await page.click('#saveBtn');
    await page.waitForTimeout(3000);
    report.step5_save = 'Clicked';

    // Step 7: After redirect, check ship detail page
    const finalUrl = page.url();
    const shipIdMatch = finalUrl.match(/\/ships\/(\d+)/);
    if (shipIdMatch) {
      const shipId = shipIdMatch[1];
      report.step6_shipDetail.url = finalUrl;
      const shipRes = await page.request.get(`${BASE_URL}/api/ships/${shipId}`);
      if (shipRes.ok()) {
        const ship = await shipRes.json();
        const classId = ship.classId ?? ship.class?.id ?? ship.ClassId ?? ship.Class?.Id;
        const className = ship.class?.name ?? ship.Class?.Name;
        report.step6_shipDetail.classId = classId;
        report.step6_shipDetail.classShown = className;
        report.finalReport.savedClass = String(classId);
        report.finalReport.savedClassText = className;
      }
      // Also check what the ship detail page displays
      await page.goto(finalUrl);
      await page.waitForLoadState('domcontentloaded');
      const classCard = await page.locator('.card-header:has-text("Class")').first();
      const classBody = await page.locator('.card:has(.card-header:has-text("Class")) .card-body h5').first();
      const displayedClass = await classBody.textContent();
      report.step6_shipDetail.displayedOnPage = displayedClass?.trim() || null;
    } else {
      const statusText = await page.locator('#statusMsg').textContent();
      report.errors.push('Save may have failed. Final URL: ' + finalUrl + ' Status: ' + statusText);
    }

    // Final report
    const sel = report.finalReport.selectedClass;
    const sav = report.finalReport.savedClass;
    report.finalReport.match = sel && sav && String(sel) === String(sav);
    if (sel && sav && String(sel) !== String(sav)) {
      report.finalReport.why = 'Selected class ID ' + sel + ' but ship was saved with class ID ' + sav + '. Possible bug: Save handler may be using wrong value, or refreshClassDropdown overwrites selection.';
    } else if (report.finalReport.match) {
      report.finalReport.why = 'Selection and saved class match.';
    }
  } catch (err) {
    report.errors.push(err.message);
    console.error('Test error:', err);
  } finally {
    if (browser) await browser.close();
  }

  return report;
}

runTest()
  .then((report) => {
    console.log('\n\n========== ADD FROM URL FLOW TEST REPORT ==========\n');
    console.log('1. Server at localhost:3000:', report.step1_server);
    console.log('2. Admin mode + /admin/add-from-url:', report.step2_admin_nav);
    console.log('3. URL entered, Go clicked:', report.step3_url_entered);
    console.log('4. Page loaded:', report.step4_page_loaded);
    console.log('\n--- Snapshot 1: Class dropdown after load ---');
    console.log('  Selected value:', report.snapshot1_classDropdown?.selected);
    console.log('  Selected text:', report.snapshot1_classDropdown?.selectedText);
    console.log('  Options (first 5):', report.snapshot1_classDropdown?.options?.slice(0, 5));
    console.log('\n--- Snapshot 2: After changing Class ---');
    console.log('  New selection:', report.snapshot2_afterClassChange?.selected, '-', report.snapshot2_afterClassChange?.selectedText);
    console.log('\n5. Save clicked:', report.step5_save);
    console.log('\n--- Ship detail page after redirect ---');
    console.log('  URL:', report.step6_shipDetail?.url);
    console.log('  Class shown (API):', report.step6_shipDetail?.classShown, '(id:', report.step6_shipDetail?.classId + ')');
    console.log('  Class displayed on page:', report.step6_shipDetail?.displayedOnPage);
    console.log('\n========== FINAL REPORT ==========');
    console.log('  Selected class (before Save):', report.finalReport?.selectedClass, '-', report.finalReport?.selectedClassText);
    console.log('  Saved class (on ship detail):', report.finalReport?.savedClass, '-', report.finalReport?.savedClassText);
    console.log('  Match:', report.finalReport?.match ? 'YES' : 'NO');
    console.log('  Why:', report.finalReport?.why || 'N/A');
    if (report.errors.length) {
      console.log('\nErrors:');
      report.errors.forEach((e) => console.log('  !', e));
    }
    console.log('\n========================================\n');
    const mismatch = report.finalReport?.selectedClass && report.finalReport?.savedClass &&
      String(report.finalReport.selectedClass) !== String(report.finalReport.savedClass);
    process.exit(mismatch ? 1 : 0);
  })
  .catch((err) => {
    console.error('Fatal error:', err);
    process.exit(1);
  });
