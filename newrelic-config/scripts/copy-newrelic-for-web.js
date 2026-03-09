#!/usr/bin/env node
/** Copy newrelic.js to current dir (e.g. NavalArchive.Web) for local dev. Run from app dir. */
const fs = require('fs');
const path = require('path');
const src = path.join(__dirname, '..', 'config', 'newrelic.js');
const dest = path.join(process.cwd(), 'newrelic.js');
if (fs.existsSync(src)) {
  fs.copyFileSync(src, dest);
}
