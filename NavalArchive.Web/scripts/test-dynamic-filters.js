#!/usr/bin/env node
/**
 * Unit tests for public/js/dynamicFilters.js (no browser, no API).
 * Run: npm test
 */
const assert = require('assert');
const DF = require('../public/js/dynamicFilters.js');

const ships = [
  { name: 'Yamato', class: { type: 'Battleship', country: 'JP' }, displacement: 72000, commissioned: '1941-12-16', active: false },
  { name: 'Bismarck', class: { type: 'Battleship', country: 'DE' }, displacement: 50300, commissioned: '1940-08-24', active: false },
  { name: 'Fletcher', class: { type: 'Destroyer', country: 'US' }, displacement: 2500, commissioned: '1942-06-30', active: false }
];

const cfg = DF.buildFilterConfig(ships);
const keys = cfg.map(function (c) { return c.key; });
assert(keys.includes('name'));
assert(keys.includes('class.type'));
var dispCfg = cfg.find(function (c) { return c.key === 'displacement'; });
assert.strictEqual(dispCfg.type, 'range');
assert.strictEqual(dispCfg.rangeMode, 'number');
var commCfg = cfg.find(function (c) { return c.key === 'commissioned'; });
assert.strictEqual(commCfg.type, 'range');
assert.strictEqual(commCfg.rangeMode, 'date');

const shipsNames = [
  { name: 'A', captain: { name: 'Alice Smith' } },
  { name: 'B', captain: { name: 'Bob Jones' } },
  { name: 'C', captain: { name: 'Alice Smith' } }
];
const cfgCap = DF.buildFilterConfig(shipsNames);
var cptRow = cfgCap.find(function (c) { return c.key === 'captain.name'; });
assert(cptRow && cptRow.type === 'dropdown');
assert(cptRow.options.length >= 2);
const activeCfg = cfg.find(function (c) { return c.key === 'active'; });
assert(activeCfg && activeCfg.type === 'radio');

const shipsYearZero = [
  { name: 'Old', yearCommissioned: 0 },
  { name: 'Mid', yearCommissioned: 1940 },
  { name: 'Late', yearCommissioned: 1942 }
];
var cfgYear = DF.buildFilterConfig(shipsYearZero);
var yearRow = cfgYear.find(function (c) { return c.key === 'yearCommissioned'; });
assert(yearRow && yearRow.type === 'range');
assert.strictEqual(yearRow.min, 1940);
assert.strictEqual(yearRow.max, 1942);

var restaurantsYearCol = [
  { name: 'R1', yearEstablished: 0 },
  { name: 'R2', yearEstablished: 1998 },
  { name: 'R3', yearEstablished: 2005 }
];
var cfgRest = DF.buildFilterConfig(restaurantsYearCol);
var yEst = cfgRest.find(function (c) {
  return c.key === 'yearEstablished';
});
assert(yEst && yEst.type === 'range');
assert.strictEqual(yEst.min, 1998);
assert.strictEqual(yEst.max, 2005);

const out1 = DF.applyFilters(ships, { 'class.type': 'Destroyer' }, cfg);
assert.strictEqual(out1.length, 1);
assert.strictEqual(out1[0].name, 'Fletcher');

/* name is radio for this small set — substring only applies when type is text */
const cfgNameText = DF.applyTypeOverrides(cfg, { name: 'text' });
const out2 = DF.applyFilters(ships, { name: 'mar' }, cfgNameText);
assert(out2.length >= 1);

const out3 = DF.applyFilters(ships, { displacement: { min: 50000, max: 80000 } }, cfg);
assert.strictEqual(out3.length, 2);

const out3b = DF.applyFilters(ships, { displacement: { min: 60000, max: 80000 } }, cfg);
assert.strictEqual(out3b.length, 1);
assert.strictEqual(out3b[0].name, 'Yamato');

const from = new Date('1940-01-01');
const to = new Date('1942-01-01');
const out4 = DF.applyFilters(ships, { commissioned: { from: from, to: to } }, cfg);
assert.strictEqual(out4.length, 2);

const merged = DF.mergeActiveFromRequest({
  df: JSON.stringify({ foo: 'bar' }),
  country: 'US',
  type: 'DD',
  yearMin: '1940',
  yearMax: '1945'
});
assert.strictEqual(merged.foo, 'bar');
assert.strictEqual(merged['class.country'], 'US');

const mergedOverride = DF.mergeActiveFromRequest({
  df: JSON.stringify({ foo: 'baz', 'class.country': 'JP' }),
  country: 'US'
});
assert.strictEqual(mergedOverride.foo, 'baz');
assert.strictEqual(mergedOverride['class.country'], 'JP');

const cfg2 = DF.applyTypeOverrides(DF.buildFilterConfig(ships), { 'class.type': 'dropdown' });
assert.strictEqual(cfg2.find(function (c) { return c.key === 'class.type'; }).type, 'dropdown');

/* Persisted dropdown override on a column inferred as text must get options filled (e.g. captain.name on a large roster). */
var tagEntities = [];
for (var ti = 0; ti < 12; ti++) tagEntities.push({ sku: 'x', tag: 't' + ti });
var cfgTag = DF.buildFilterConfig(tagEntities);
assert.strictEqual(cfgTag.find(function (c) { return c.key === 'tag'; }).type, 'text');
var cfgTagDrop = DF.ensureEnumOptionsFromEntities(
  DF.applyTypeOverrides(cfgTag, { tag: 'dropdown' }),
  tagEntities
);
assert.strictEqual(cfgTagDrop.find(function (c) { return c.key === 'tag'; }).type, 'dropdown');
assert.strictEqual(cfgTagDrop.find(function (c) { return c.key === 'tag'; }).options.length, 12);

var bigFleet = [];
for (var bi = 0; bi < 120; bi++) bigFleet.push({ name: 'S' + bi, captain: { name: 'Captain ' + bi } });
var cfgBig = DF.buildFilterConfig(bigFleet);
assert.strictEqual(cfgBig.find(function (c) { return c.key === 'captain.name'; }).type, 'text');
var cfgCapDrop = DF.ensureEnumOptionsFromEntities(
  DF.applyTypeOverrides(cfgBig, { 'captain.name': 'dropdown' }),
  bigFleet
);
var capOpt = cfgCapDrop.find(function (c) { return c.key === 'captain.name'; });
assert.strictEqual(capOpt.type, 'dropdown');
assert.strictEqual(capOpt.options.length, 120);

console.log('OK: dynamicFilters tests passed (' + cfg.length + ' config fields)');
