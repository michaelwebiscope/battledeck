/**
 * Entity-driven dynamic filters: introspect plain objects, infer control types, apply predicates.
 * Entity-agnostic — works for ships, captains, or any JSON list shaped as plain objects (nested via flattenEntity).
 * UMD: works in Node (require) and browser (global DynamicFilters).
 */
(function (global, factory) {
  typeof exports === 'object' && typeof module !== 'undefined'
    ? (module.exports = factory())
    : (global.DynamicFilters = factory());
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  /** @type {Record<string, string>} Force UI type per flattened attribute key */
  var filterTypeOverrides = {};

  /** When true, each filter header shows a type &lt;select&gt; (session-only; re-render that block). */
  var ALLOW_FILTER_TYPE_EDITING = false;

  /** Max distinct string values to offer as radio (else free-text). */
  var STRING_RADIO_MAX_UNIQUE = 10;

  /** For name-like keys (e.g. captain.name), allow more distinct values as dropdown before falling back to text. */
  var STRING_NAMEKEY_MAX_UNIQUE = 100;

  function isLikelyNameEnumKey(key) {
    if (typeof key !== 'string' || !key) return false;
    var k = key.toLowerCase();
    if (k.indexOf('captain') !== -1 && k.indexOf('name') !== -1) return true;
    if (k.slice(-5) === '.name' || k === 'name') return true;
    if (k.indexOf('.name.') !== -1) return true;
    return false;
  }

  /**
   * Heuristic: numeric column mixes 0 (unknown) with plausible calendar years.
   * Domain-agnostic — works for yearCommissioned, yearEstablished, foundingYear, etc.
   * @param {number[]} nums
   */
  function looksLikeCalendarYearColumn(nums) {
    if (!nums || !nums.length) return false;
    var hasZero = nums.some(function (x) {
      return x === 0;
    });
    if (!hasZero) return false;
    var pos = nums.filter(function (x) {
      return x > 0;
    });
    if (!pos.length) return false;
    var mn = Math.min.apply(null, pos);
    var mx = Math.max.apply(null, pos);
    return mn >= 500 && mx <= 4000 && mn <= mx;
  }

  /** @param {number[]} nums @returns {number[]} */
  function numericSamplesForRangeBounds(nums) {
    if (!nums.length) return nums;
    if (!looksLikeCalendarYearColumn(nums)) return nums;
    var pos = nums.filter(function (x) {
      return x > 0;
    });
    return pos.length ? pos : nums;
  }

  /** Flat keys to skip (blobs, noise). */
  var SKIP_KEYS_SUBSTR = ['imageData', 'imageUrl', 'imageVersion', 'videoUrl'];

  /** UI / persisted type names. Span filters use a single `range`; inferKind sets rangeMode from the data (date vs number). */
  var FILTER_TYPES = ['radio', 'dropdown', 'text', 'range'];

  /**
   * Flatten nested plain objects to dot-notation keys.
   * Arrays and non-plain objects are skipped (documented limitation).
   * @param {object} obj Root entity
   * @returns {Record<string, unknown>}
   */
  function flattenEntity(obj) {
    var out = {};
    function walk(o, prefix) {
      if (o == null) return;
      if (Array.isArray(o)) return;
      if (typeof o !== 'object') {
        out[prefix] = o;
        return;
      }
      if (Object.prototype.toString.call(o) === '[object Date]') {
        out[prefix] = o.toISOString();
        return;
      }
      var keys = Object.keys(o);
      if (keys.length === 0) return;
      for (var i = 0; i < keys.length; i++) {
        var k = keys[i];
        var v = o[k];
        var next = prefix ? prefix + '.' + k : k;
        walk(v, next);
      }
    }
    walk(obj, '');
    return out;
  }

  /**
   * Title Case from camelCase or dot segments.
   * @param {string} key
   * @returns {string}
   */
  function keyToLabel(key) {
    return key
      .split('.')
      .map(function (seg) {
        return seg.replace(/([A-Z])/g, ' $1').replace(/^./, function (s) {
          return s.toUpperCase();
        }).trim();
      })
      .join(' · ');
  }

  function shouldSkipKey(key) {
    var l = key.toLowerCase();
    for (var i = 0; i < SKIP_KEYS_SUBSTR.length; i++) {
      if (l.indexOf(SKIP_KEYS_SUBSTR[i].toLowerCase()) !== -1) return true;
    }
    return false;
  }

  function isIsoDateString(s) {
    if (typeof s !== 'string') return false;
    return /^\d{4}-\d{2}-\d{2}/.test(s) && !isNaN(Date.parse(s));
  }

  /**
   * Infer default filter kind from collected samples.
   * @param {unknown[]} samples
   * @param {number} entityCount
   * @param {string} key flattened attribute key (for name/captain dropdown heuristics)
   * @returns {{ kind: string, rangeMode?: string, options?: string[], min?: number, max?: number, dateMin?: string, dateMax?: string }}
   */
  function inferKind(samples, entityCount, key) {
    var nonNull = samples.filter(function (v) {
      return v != null && v !== '';
    });
    if (nonNull.length === 0) return { kind: 'text' };

    var allBool = nonNull.every(function (v) {
      return typeof v === 'boolean';
    });
    if (allBool) return { kind: 'radio', options: ['true', 'false'] };

    var strVals = nonNull.map(function (v) {
      return String(v);
    });
    /* ISO dates before numeric: avoids ambiguous strings; decimals like 1941.5 stay numeric. */
    var allDates = strVals.every(isIsoDateString);
    if (allDates) {
      var times = strVals.map(function (s) {
        return new Date(s).getTime();
      }).filter(function (t) {
        return !isNaN(t);
      });
      if (times.length === strVals.length) {
        var dMin = Math.min.apply(null, times);
        var dMax = Math.max.apply(null, times);
        return {
          kind: 'range',
          rangeMode: 'date',
          dateMin: new Date(dMin).toISOString().slice(0, 10),
          dateMax: new Date(dMax).toISOString().slice(0, 10)
        };
      }
    }

    var allNum = nonNull.every(function (v) {
      if (typeof v === 'number' && !isNaN(v)) return true;
      if (typeof v === 'string' && v.trim() !== '' && !isNaN(Number(v))) return true;
      return false;
    });
    if (allNum) {
      var nums = nonNull.map(function (v) {
        return Number(v);
      });
      var span = numericSamplesForRangeBounds(nums);
      return {
        kind: 'range',
        rangeMode: 'number',
        min: Math.min.apply(null, span),
        max: Math.max.apply(null, span)
      };
    }

    var uniq = [];
    var seen = {};
    for (var i = 0; i < strVals.length; i++) {
      if (!seen[strVals[i]]) {
        seen[strVals[i]] = 1;
        uniq.push(strVals[i]);
      }
    }
    uniq.sort();
    var nameKey = typeof key === 'string' && isLikelyNameEnumKey(key);
    var maxEnum = nameKey ? STRING_NAMEKEY_MAX_UNIQUE : STRING_RADIO_MAX_UNIQUE;
    if (uniq.length > maxEnum) {
      return { kind: 'text' };
    }
    if (uniq.length === entityCount && entityCount > maxEnum) {
      return { kind: 'text' };
    }
    var listKind = nameKey ? 'dropdown' : 'radio';
    return { kind: listKind, options: uniq };
  }

  /**
   * Build filter metadata from an array of homogeneous entities (e.g. ships from API).
   * @param {object[]} entities
   * @returns {Array<{ key: string, label: string, type: string, options: string[]|null, min: number|null, max: number|null }>}
   */
  function buildFilterConfig(entities) {
    if (!entities || !entities.length) return [];

    var rows = entities.map(flattenEntity);
    var keySet = {};
    for (var r = 0; r < rows.length; r++) {
      var ks = Object.keys(rows[r]);
      for (var j = 0; j < ks.length; j++) keySet[ks[j]] = true;
    }
    var keys = Object.keys(keySet).filter(function (k) {
      return !shouldSkipKey(k);
    });

    var config = [];
    for (var i = 0; i < keys.length; i++) {
      var key = keys[i];
      var samples = [];
      for (var ri = 0; ri < rows.length; ri++) {
        samples.push(rows[ri][key]);
      }
      var inferred = inferKind(samples, entities.length, key);
      var oType = filterTypeOverrides[key];
      var type = oType && FILTER_TYPES.indexOf(oType) !== -1 ? oType : inferred.kind;
      if (FILTER_TYPES.indexOf(type) === -1) type = inferred.kind;

      var rangeMode;
      if (type === 'range') {
        rangeMode = inferred.kind === 'range' ? (inferred.rangeMode || 'number') : 'number';
      }

      var entry = {
        key: key,
        label: keyToLabel(key),
        type: type,
        options: inferred.options ? inferred.options.slice() : null,
        min: inferred.min != null ? inferred.min : null,
        max: inferred.max != null ? inferred.max : null
      };
      if (type === 'range') {
        entry.rangeMode = rangeMode;
        if (inferred.dateMin != null) entry.dateMin = inferred.dateMin;
        if (inferred.dateMax != null) entry.dateMax = inferred.dateMax;
      }

      if (type === 'radio' || type === 'dropdown') {
        entry.options = (inferred.options || []).slice();
        if (!entry.options.length) continue;
      }
      if (type === 'range') {
        var rm = entry.rangeMode || 'number';
        if (rm === 'number') {
          if (entry.min == null || entry.max == null || isNaN(entry.min) || isNaN(entry.max)) continue;
        }
      }

      config.push(entry);
    }

    config.sort(function (a, b) {
      return a.label.localeCompare(b.label);
    });
    return config;
  }

  /**
   * Merge runtime type overrides into existing config rows (mutates copy).
   * @param {ReturnType<typeof buildFilterConfig>} config
   * @param {Record<string, string>} overrides
   */
  function applyTypeOverrides(config, overrides) {
    var map = overrides || filterTypeOverrides;
    return config.map(function (row) {
      var o = Object.assign({}, row);
      if (map[o.key] && FILTER_TYPES.indexOf(map[o.key]) !== -1) {
        var t = map[o.key];
        o.type = t;
        if (t === 'range' && o.rangeMode == null) {
          if (o.dateMin && o.dateMax) o.rangeMode = 'date';
          else o.rangeMode = 'number';
        }
      }
      if (o.type === 'range' && o.rangeMode == null) {
        if (o.dateMin && o.dateMax) o.rangeMode = 'date';
        else o.rangeMode = 'number';
      }
      if (o.type !== 'range') {
        delete o.rangeMode;
        delete o.dateMin;
        delete o.dateMax;
        delete o.min;
        delete o.max;
      }
      return o;
    });
  }

  /**
   * After type overrides, range rows need min/max; recompute from entity values when missing or invalid.
   * @param {ReturnType<typeof buildFilterConfig>} config
   * @param {object[]} entities
   */
  function ensureRangeBoundsFromEntities(config, entities) {
    if (!config || !config.length || !entities || !entities.length) return config || [];
    var rows = entities.map(flattenEntity);
    return config.map(function (row) {
      if (row.type !== 'range') return row;
      var mode = row.rangeMode || 'number';
      if (mode === 'date') {
        var dmin = row.dateMin;
        var dmax = row.dateMax;
        if (dmin && dmax) return row;
        var ts = [];
        for (var di = 0; di < rows.length; di++) {
          var draw = rows[di][row.key];
          if (draw == null || draw === '') continue;
          var dt = new Date(draw).getTime();
          if (!isNaN(dt)) ts.push(dt);
        }
        if (!ts.length) return row;
        var dc = Object.assign({}, row);
        dc.dateMin = new Date(Math.min.apply(null, ts)).toISOString().slice(0, 10);
        dc.dateMax = new Date(Math.max.apply(null, ts)).toISOString().slice(0, 10);
        return dc;
      }
      var rmin = row.min != null ? Number(row.min) : NaN;
      var rmax = row.max != null ? Number(row.max) : NaN;
      var nums = [];
      for (var i = 0; i < rows.length; i++) {
        var raw = rows[i][row.key];
        if (raw == null || raw === '') continue;
        var n = typeof raw === 'number' ? raw : Number(raw);
        if (!isNaN(n)) nums.push(n);
      }
      if (!nums.length) return row;
      var span = numericSamplesForRangeBounds(nums);
      var newMin = Math.min.apply(null, span);
      var newMax = Math.max.apply(null, span);
      if (looksLikeCalendarYearColumn(nums) && newMax >= newMin) {
        var cYear = Object.assign({}, row);
        cYear.min = newMin;
        cYear.max = newMax;
        return cYear;
      }
      if (!isNaN(rmin) && !isNaN(rmax) && rmax >= rmin) return row;
      var c = Object.assign({}, row);
      c.min = newMin;
      c.max = newMax;
      return c;
    });
  }

  /** Cap distinct values for radio/dropdown when options are synthesized from data (admin override). */
  var ENUM_OPTIONS_MAX = 2000;

  /**
   * Fill {@link options} for radio/dropdown rows when missing (e.g. persisted type override from `text` → `dropdown`).
   * @param {ReturnType<typeof buildFilterConfig>} config
   * @param {object[]} entities
   */
  function ensureEnumOptionsFromEntities(config, entities) {
    if (!config || !config.length || !entities || !entities.length) return config || [];
    var flatRows = entities.map(flattenEntity);
    return config.map(function (row) {
      if (row.type !== 'radio' && row.type !== 'dropdown') return row;
      if (row.options && row.options.length) return row;
      var key = row.key;
      var seen = {};
      var opts = [];
      for (var ei = 0; ei < flatRows.length; ei++) {
        var v = flatRows[ei][key];
        if (v == null || v === '') continue;
        var s = String(v);
        if (!seen[s]) {
          seen[s] = 1;
          opts.push(s);
        }
      }
      opts.sort();
      if (opts.length > ENUM_OPTIONS_MAX) opts = opts.slice(0, ENUM_OPTIONS_MAX);
      if (!opts.length) return row;
      return Object.assign({}, row, { options: opts });
    });
  }

  function getFlatValue(flat, key) {
    if (!flat || !(key in flat)) return undefined;
    return flat[key];
  }

  /**
   * Filter entities with AND logic. Decoupled from DOM.
   * @param {object[]} entities Raw API objects
   * @param {Record<string, unknown>} activeFilters keyed by dot-path
   * @param {ReturnType<typeof buildFilterConfig>} filterConfig
   * @returns {object[]}
   */
  function applyFilters(entities, activeFilters, filterConfig) {
    if (!entities || !entities.length) return [];
    var af = activeFilters || {};
    var keys = Object.keys(af);
    if (keys.length === 0) return entities.slice();

    var cfgByKey = {};
    for (var ci = 0; ci < filterConfig.length; ci++) {
      cfgByKey[filterConfig[ci].key] = filterConfig[ci];
    }

    return entities.filter(function (ent) {
      var flat = flattenEntity(ent);
      for (var i = 0; i < keys.length; i++) {
        var key = keys[i];
        var val = af[key];
        if (val == null || val === '') continue;
        var cfgR = cfgByKey[key];
        var ftype = cfgR ? cfgR.type : 'text';
        var spanMode = ftype === 'range' && cfgR ? cfgR.rangeMode || 'number' : null;
        if (ftype === 'range' && val && typeof val === 'object') {
          if (spanMode === 'date') {
            if (!val.from && !val.to) continue;
          } else {
            if ((val.min == null || val.min === '') && (val.max == null || val.max === '')) continue;
          }
        }
        var raw = getFlatValue(flat, key);

        if (ftype === 'radio' || ftype === 'dropdown') {
          if (String(raw) !== String(val)) return false;
        } else if (ftype === 'text') {
          if (raw == null) return false;
          if (!String(raw).toLowerCase().includes(String(val).toLowerCase())) return false;
        } else if (ftype === 'range' && spanMode !== 'date') {
          var rv = val && typeof val === 'object' ? val : {};
          var n = Number(raw);
          if (isNaN(n)) return false;
          if (rv.min != null && rv.min !== '' && n < Number(rv.min)) return false;
          if (rv.max != null && rv.max !== '' && n > Number(rv.max)) return false;
        } else if (ftype === 'range' && spanMode === 'date') {
          var dv1 = val && typeof val === 'object' ? val : {};
          if (raw == null) return false;
          var d1 = new Date(raw);
          if (isNaN(d1.getTime())) return false;
          if (dv1.from) {
            var from1 = new Date(dv1.from);
            if (d1 < from1) return false;
          }
          if (dv1.to) {
            var to1 = new Date(dv1.to);
            to1.setHours(23, 59, 59, 999);
            if (d1 > to1) return false;
          }
        }
      }
      return true;
    });
  }

  /**
   * @param {Record<string, unknown>} active
   * @returns {string}
   */
  function serializeActiveFilters(active) {
    try {
      return JSON.stringify(active == null ? {} : active);
    } catch (e) {
      return '{}';
    }
  }

  /**
   * @param {string|null} json
   * @returns {Record<string, unknown>}
   */
  function parseActiveFilters(json) {
    if (!json || typeof json !== 'string') return {};
    try {
      var o = JSON.parse(json);
      return typeof o === 'object' && o ? o : {};
    } catch (e) {
      return {};
    }
  }

  /**
   * @param {URLSearchParams} params
   * @returns {Record<string, unknown>}
   */
  function parseDfFromSearchParams(params) {
    var df = params.get('df');
    if (df) return parseActiveFilters(df);
    return {};
  }

  /**
   * Legacy query keys → dynamic map (class.* from camelCase API).
   * @param {Record<string, string|undefined>} query Express req.query-like
   */
  function legacyQueryToActiveFilters(query) {
    var out = {};
    if (!query) return out;
    var c = query.country;
    var t = query.type;
    var yMin = query.yearMin;
    var yMax = query.yearMax;
    if (c) out['class.country'] = c;
    if (t) out['class.type'] = t;
    if (yMin || yMax) {
      out.yearCommissioned = {
        min: yMin ? Number(yMin) : null,
        max: yMax ? Number(yMax) : null
      };
    }
    return out;
  }

  /**
   * Merge legacy + df (df wins on key conflict).
   */
  function mergeActiveFromRequest(query) {
    var leg = legacyQueryToActiveFilters(query);
    var rawDf = query && query.df != null ? query.df : '';
    var dfStr = typeof rawDf === 'string' ? rawDf : JSON.stringify(rawDf);
    var df = parseActiveFilters(dfStr);
    var merged = Object.assign({}, leg, df);
    return merged;
  }

  /**
   * Drop range filters that cover the full data span (no narrowing).
   * @param {Record<string, unknown>} active
   * @param {ReturnType<typeof buildFilterConfig>} filterConfig
   */
  function stripNoopRangeFilters(active, filterConfig) {
    var out = Object.assign({}, active || {});
    var cfgByKey = {};
    for (var i = 0; i < filterConfig.length; i++) {
      cfgByKey[filterConfig[i].key] = filterConfig[i];
    }
    var keys = Object.keys(out);
    for (var j = 0; j < keys.length; j++) {
      var k = keys[j];
      var cfg = cfgByKey[k];
      if (!cfg || cfg.type !== 'range') continue;
      var v = out[k];
      if (!v || typeof v !== 'object') continue;
      var mode = cfg.rangeMode || 'number';
      if (mode === 'date') {
        var dmin = cfg.dateMin ? String(cfg.dateMin).slice(0, 10) : '';
        var dmax = cfg.dateMax ? String(cfg.dateMax).slice(0, 10) : '';
        if (!dmin || !dmax) continue;
        var vf = v.from ? String(v.from).slice(0, 10) : '';
        var vt = v.to ? String(v.to).slice(0, 10) : '';
        if (vf === dmin && vt === dmax) delete out[k];
        continue;
      }
      var rmin = cfg.min != null ? Number(cfg.min) : NaN;
      var rmax = cfg.max != null ? Number(cfg.max) : NaN;
      if (isNaN(rmin) || isNaN(rmax)) continue;
      var mn = v.min != null && v.min !== '' ? Number(v.min) : null;
      var mx = v.max != null && v.max !== '' ? Number(v.max) : null;
      if (mn === rmin && mx === rmax) delete out[k];
    }
    return out;
  }

  // --- DOM (browser only) ---

  /**
   * @param {{ key: string, label: string, type: string, options?: string[]|null, min?: number|null, max?: number|null }} cfg
   * @param {unknown} currentValue
   * @param {function({ key: string, value: unknown }): void} onChange
   * @param {function(string): void} [onTypeChange] invoked when user picks a new control type
   * @param {boolean} [allowTypeEdit] omit to use module default {@link ALLOW_FILTER_TYPE_EDITING}
   * @param {{ adminPersist?: { enabled: boolean, persistedMap: Record<string, string>, hiddenKeys?: string[], persist?: function({ overrides?: Record<string, string>, hiddenKeys?: string[] }): void, commit?: function(Record<string, string>): void } }} [sectionExtras] optional admin toolbar (e.g. persist types to JSON via your API)
   */
  function createFilterSection(cfg, currentValue, onChange, onTypeChange, allowTypeEdit, sectionExtras) {
    if (typeof document === 'undefined') throw new Error('createFilterSection is browser-only');

    sectionExtras = sectionExtras || {};
    var adminPersist = sectionExtras.adminPersist;

    function adminCallPersist(patch) {
      if (!adminPersist) return;
      if (typeof adminPersist.persist === 'function') adminPersist.persist(patch || {});
      else if (patch && patch.overrides !== undefined && typeof adminPersist.commit === 'function') {
        adminPersist.commit(patch.overrides);
      }
    }

    var useTypeEditing =
      (allowTypeEdit !== undefined ? !!allowTypeEdit : ALLOW_FILTER_TYPE_EDITING) &&
      typeof onTypeChange === 'function';

    var wrap = document.createElement('div');
    wrap.className = 'fleet-df-section mb-0';
    wrap.dataset.filterKey = cfg.key;

    var header = document.createElement('div');
    header.className = 'd-flex align-items-center justify-content-between gap-2 mb-2';

    var title = document.createElement('div');
    title.className = 'small text-muted fw-semibold text-uppercase flex-grow-1 min-w-0';
    title.style.letterSpacing = '0.05em';
    title.textContent = cfg.label;
    header.appendChild(title);

    if (
      adminPersist &&
      adminPersist.enabled &&
      (typeof adminPersist.persist === 'function' || typeof adminPersist.commit === 'function')
    ) {
      var map = adminPersist.persistedMap || {};
      var act = document.createElement('div');
      act.className = 'fleet-df-persist-actions admin-only d-flex align-items-center gap-1 flex-shrink-0';

      var btnEdit = document.createElement('button');
      btnEdit.type = 'button';
      btnEdit.className = 'btn btn-outline-secondary btn-sm py-0 px-2';
      btnEdit.title = 'Set filter control type (saved for this site)';
      btnEdit.innerHTML = '<i class="bi bi-pencil" aria-hidden="true"></i><span class="visually-hidden"> Edit</span>';
      btnEdit.setAttribute('aria-label', 'Edit filter type for ' + cfg.label);

      var btnClear = document.createElement('button');
      btnClear.type = 'button';
      btnClear.className = 'btn btn-outline-danger btn-sm py-0 px-2 fleet-df-remove-filter';
      btnClear.title = 'Remove this filter from the panel (restore from toolbar)';
      var rmSym = document.createElement('span');
      rmSym.className = 'fleet-df-remove-x';
      rmSym.setAttribute('aria-hidden', 'true');
      rmSym.textContent = '\u2715';
      btnClear.appendChild(rmSym);
      var rmSr = document.createElement('span');
      rmSr.className = 'visually-hidden';
      rmSr.textContent = ' Remove filter';
      btnClear.appendChild(rmSr);
      btnClear.setAttribute('aria-label', 'Remove filter ' + cfg.label + ' from panel');

      /* No admin-only here: body.admin-mode .admin-only { display:flex !important } would override display:none and break toggle. */
      var editPanel = document.createElement('div');
      editPanel.className = 'fleet-df-persist-edit border rounded px-2 py-2 bg-light mb-2';
      editPanel.hidden = true;
      var lbl = document.createElement('div');
      lbl.className = 'form-label small text-muted mb-1';
      lbl.textContent = 'Control type';
      var gname = 'df-persist-t-' + cfg.key.replace(/\W/g, '_').slice(0, 72);
      var typeRadios = document.createElement('div');
      typeRadios.className = 'd-flex flex-column gap-1 fleet-df-persist-type-radios';
      FILTER_TYPES.forEach(function (ft) {
        var id = gname + '-' + ft;
        var row = document.createElement('div');
        row.className = 'form-check mb-0';
        var inp = document.createElement('input');
        inp.type = 'radio';
        inp.className = 'form-check-input';
        inp.name = gname;
        inp.id = id;
        inp.value = ft;
        if (ft === cfg.type) inp.checked = true;
        var lab = document.createElement('label');
        lab.className = 'form-check-label small';
        lab.htmlFor = id;
        lab.textContent = ft;
        row.appendChild(inp);
        row.appendChild(lab);
        typeRadios.appendChild(row);
      });
      var btnRow = document.createElement('div');
      btnRow.className = 'd-flex flex-wrap gap-2 mt-2';
      var applyBtn = document.createElement('button');
      applyBtn.type = 'button';
      applyBtn.className = 'btn btn-sm btn-primary';
      applyBtn.textContent = 'Save';
      var cancelBtn = document.createElement('button');
      cancelBtn.type = 'button';
      cancelBtn.className = 'btn btn-sm btn-outline-secondary';
      cancelBtn.textContent = 'Cancel';
      btnRow.appendChild(applyBtn);
      btnRow.appendChild(cancelBtn);
      editPanel.appendChild(lbl);
      editPanel.appendChild(typeRadios);
      editPanel.appendChild(btnRow);

      function syncPersistRadiosToCfgType() {
        var radios = typeRadios.querySelectorAll('input[type="radio"]');
        for (var ri = 0; ri < radios.length; ri++) {
          radios[ri].checked = radios[ri].value === cfg.type;
        }
      }

      btnEdit.addEventListener('click', function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        var opening = editPanel.hidden;
        editPanel.hidden = !opening;
        if (opening) syncPersistRadiosToCfgType();
      });
      cancelBtn.addEventListener('click', function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        editPanel.hidden = true;
      });
      applyBtn.addEventListener('click', function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        var checked = typeRadios.querySelector('input[name="' + gname + '"]:checked');
        var picked = checked ? checked.value : cfg.type;
        var next = Object.assign({}, map);
        next[cfg.key] = picked;
        adminCallPersist({ overrides: next });
      });
      /* Delegated capture: clicks on the icon/child sometimes miss the button hit target (stacking, icon font). */
      act.addEventListener(
        'click',
        function (ev) {
          var rm = ev.target && ev.target.closest && ev.target.closest('button.fleet-df-remove-filter');
          if (!rm) return;
          ev.preventDefault();
          ev.stopPropagation();
          var hk = adminPersist.hiddenKeys;
          var cur = Array.isArray(hk) ? hk.slice() : [];
          if (cur.indexOf(cfg.key) < 0) cur.push(cfg.key);
          adminCallPersist({ hiddenKeys: cur });
        },
        true
      );

      act.appendChild(btnEdit);
      act.appendChild(btnClear);
      header.appendChild(act);
      wrap.appendChild(header);
      wrap.appendChild(editPanel);
    } else {
      wrap.appendChild(header);
    }

    if (useTypeEditing) {
      var typeSel = document.createElement('select');
      typeSel.className = 'form-select form-select-sm fleet-filter-type-select';
      typeSel.setAttribute('aria-label', 'Filter type for ' + cfg.label);
      FILTER_TYPES.forEach(function (ft) {
        var opt = document.createElement('option');
        opt.value = ft;
        opt.textContent = ft;
        if (ft === cfg.type) opt.selected = true;
        typeSel.appendChild(opt);
      });
      typeSel.addEventListener('change', function () {
        onTypeChange(typeSel.value);
      });
      header.appendChild(typeSel);
    }

    var body = document.createElement('div');
    body.className = 'fleet-df-section__body';
    mountControlInto(body, cfg, currentValue, onChange);
    wrap.appendChild(body);

    wrap._replaceBody = function (newCfg, val) {
      body.innerHTML = '';
      mountControlInto(body, newCfg, val, onChange);
    };

    return wrap;
  }

  function mountControlInto(container, cfg, currentValue, onChange) {
    var type = cfg.type;

    if (type === 'radio') {
      var opts = cfg.options || [];
      var stack = document.createElement('div');
      stack.className = 'd-flex flex-column gap-1 align-items-start fleet-df-radio-stack';

      function addRadio(val, label, name, checked) {
        var id = 'df-' + name + '-' + (val === '' ? 'all' : String(val).replace(/\W/g, '_').slice(0, 40));
        var row = document.createElement('div');
        row.className = 'form-check';
        var inp = document.createElement('input');
        inp.type = 'radio';
        inp.className = 'form-check-input';
        inp.name = name;
        inp.id = id;
        inp.value = val;
        if (checked) inp.checked = true;
        inp.addEventListener('change', function () {
          if (inp.checked) onChange({ key: cfg.key, value: val === '' ? null : val });
        });
        var lab = document.createElement('label');
        lab.className = 'form-check-label';
        lab.htmlFor = id;
        lab.textContent = label;
        row.appendChild(inp);
        row.appendChild(lab);
        stack.appendChild(row);
      }

      var gname = 'df-radio-' + cfg.key.replace(/\W/g, '_');
      var cur = currentValue == null || currentValue === '' ? null : String(currentValue);
      addRadio('', 'All', gname, cur == null);
      for (var i = 0; i < opts.length; i++) {
        addRadio(opts[i], opts[i], gname, cur === String(opts[i]));
      }
      container.appendChild(stack);
      return;
    }

    if (type === 'dropdown') {
      var sel = document.createElement('select');
      sel.className = 'form-select form-select-sm';
      var allOpt = document.createElement('option');
      allOpt.value = '';
      allOpt.textContent = 'All / Any';
      sel.appendChild(allOpt);
      (cfg.options || []).forEach(function (o) {
        var op = document.createElement('option');
        op.value = o;
        op.textContent = o;
        sel.appendChild(op);
      });
      if (currentValue != null && currentValue !== '') sel.value = String(currentValue);
      sel.addEventListener('change', function () {
        onChange({ key: cfg.key, value: sel.value === '' ? null : sel.value });
      });
      container.appendChild(sel);
      return;
    }

    if (type === 'text') {
      var inp2 = document.createElement('input');
      inp2.type = 'text';
      inp2.className = 'form-control form-control-sm';
      inp2.placeholder = 'Contains…';
      if (currentValue != null) inp2.value = String(currentValue);
      inp2.addEventListener('input', function () {
        onChange({ key: cfg.key, value: inp2.value.trim() === '' ? null : inp2.value.trim() });
      });
      container.appendChild(inp2);
      return;
    }

    var isDateSpan = type === 'range' && (cfg.rangeMode || 'number') === 'date';

    if (isDateSpan) {
      var dr = currentValue && typeof currentValue === 'object' ? currentValue : {};
      var rowD = document.createElement('div');
      rowD.className = 'd-flex flex-wrap gap-2 align-items-end';

      var f1d = document.createElement('div');
      var l1d = document.createElement('label');
      l1d.className = 'form-label small mb-0';
      l1d.textContent = 'From';
      var i1d = document.createElement('input');
      i1d.type = 'date';
      i1d.className = 'form-control form-control-sm';
      if (dr.from) i1d.value = String(dr.from).slice(0, 10);
      f1d.appendChild(l1d);
      f1d.appendChild(i1d);

      var f2d = document.createElement('div');
      var l2d = document.createElement('label');
      l2d.className = 'form-label small mb-0';
      l2d.textContent = 'To';
      var i2d = document.createElement('input');
      i2d.type = 'date';
      i2d.className = 'form-control form-control-sm';
      if (dr.to) i2d.value = String(dr.to).slice(0, 10);
      f2d.appendChild(l2d);
      f2d.appendChild(i2d);

      function emitDd() {
        onChange({
          key: cfg.key,
          value: {
            from: i1d.value || null,
            to: i2d.value || null
          }
        });
      }
      i1d.addEventListener('change', emitDd);
      i2d.addEventListener('change', emitDd);

      rowD.appendChild(f1d);
      rowD.appendChild(f2d);
      container.appendChild(rowD);
      return;
    }

    if (type === 'range') {
      var rmin = cfg.min != null ? Number(cfg.min) : NaN;
      var rmax = cfg.max != null ? Number(cfg.max) : NaN;
      if (isNaN(rmin) || isNaN(rmax) || rmax < rmin) {
        container.appendChild(document.createTextNode(''));
        return;
      }
      var cur = currentValue && typeof currentValue === 'object' ? currentValue : {};
      function spanBoundOrDefault(v, fb) {
        if (v == null || v === '') return fb;
        var n = Number(v);
        return isNaN(n) ? fb : n;
      }
      var vMin = spanBoundOrDefault(cur.min, rmin);
      var vMax = spanBoundOrDefault(cur.max, rmax);
      var spanLooksLikeYears =
        !isNaN(rmin) &&
        !isNaN(rmax) &&
        rmin >= 500 &&
        rmax <= 4000 &&
        rmax >= rmin;
      if (spanLooksLikeYears && vMin === 0 && rmin > 0) vMin = rmin;
      if (spanLooksLikeYears && vMax === 0 && rmax > 0) vMax = rmax;
      if (vMin < rmin) vMin = rmin;
      if (vMax > rmax) vMax = rmax;
      if (vMin > vMax) vMin = vMax;

      var wrapR = document.createElement('div');
      wrapR.className = 'fleet-df-range';

      var numRow = document.createElement('div');
      numRow.className = 'd-flex gap-2 align-items-center mb-2';

      var minNum = document.createElement('input');
      minNum.type = 'number';
      minNum.className = 'form-control form-control-sm';
      minNum.placeholder = 'Min';
      minNum.value = String(vMin);
      var maxNum = document.createElement('input');
      maxNum.type = 'number';
      maxNum.className = 'form-control form-control-sm';
      maxNum.placeholder = 'Max';
      maxNum.value = String(vMax);

      var rngMin = document.createElement('input');
      rngMin.type = 'range';
      rngMin.className = 'form-range fleet-df-range-slider';
      rngMin.min = String(rmin);
      rngMin.max = String(rmax);
      var span = rmax - rmin;
      rngMin.step =
        span > 1000 ? String(Math.max(1, Math.round(span / 500))) : span > 0 && span < 1 ? 'any' : '1';
      rngMin.value = String(vMin);

      var rngMax = document.createElement('input');
      rngMax.type = 'range';
      rngMax.className = 'form-range fleet-df-range-slider';
      rngMax.min = String(rmin);
      rngMax.max = String(rmax);
      rngMax.step = rngMin.step;
      rngMax.value = String(vMax);

      function emit() {
        var a = Number(minNum.value);
        var b = Number(maxNum.value);
        if (a > b) {
          var t = a;
          a = b;
          b = t;
        }
        onChange({
          key: cfg.key,
          value: {
            min: isNaN(a) ? null : a,
            max: isNaN(b) ? null : b
          }
        });
      }

      function syncFromNums() {
        var a = Number(minNum.value);
        var b = Number(maxNum.value);
        if (!isNaN(a)) rngMin.value = String(Math.min(Math.max(a, rmin), rmax));
        if (!isNaN(b)) rngMax.value = String(Math.min(Math.max(b, rmin), rmax));
        emit();
      }

      minNum.addEventListener('change', syncFromNums);
      maxNum.addEventListener('change', syncFromNums);
      rngMin.addEventListener('input', function () {
        minNum.value = rngMin.value;
        var lo = Number(rngMin.value);
        var hi = Number(rngMax.value);
        if (lo > hi) {
          rngMax.value = rngMin.value;
          maxNum.value = rngMax.value;
        } else maxNum.value = rngMax.value;
        emit();
      });
      rngMax.addEventListener('input', function () {
        maxNum.value = rngMax.value;
        var lo = Number(rngMin.value);
        var hi = Number(rngMax.value);
        if (hi < lo) {
          rngMin.value = rngMax.value;
          minNum.value = rngMin.value;
        } else minNum.value = rngMin.value;
        emit();
      });

      numRow.appendChild(minNum);
      numRow.appendChild(maxNum);
      wrapR.appendChild(numRow);

      var labLo = document.createElement('label');
      labLo.className = 'form-label small text-muted mb-0';
      labLo.textContent = 'Min';
      wrapR.appendChild(labLo);
      wrapR.appendChild(rngMin);
      var labHi = document.createElement('label');
      labHi.className = 'form-label small text-muted mb-1';
      labHi.textContent = 'Max';
      wrapR.appendChild(labHi);
      wrapR.appendChild(rngMax);

      container.appendChild(wrapR);
      return;
    }

    container.appendChild(document.createTextNode(''));
  }

  /**
   * @param {HTMLElement} container
   * @param {ReturnType<typeof buildFilterConfig>} config
   * @param {Record<string, unknown>} activeFilters
   * @param {{ onChange: function(Record<string, unknown>): void, debounceTextMs?: number, allowFilterTypeEditing?: boolean, adminPersist?: { enabled: boolean, persistedMap: Record<string, string>, hiddenKeys?: string[], persist?: function({ overrides?: Record<string, string>, hiddenKeys?: string[] }): void, commit?: function(Record<string, string>): void } }} options
   */
  function mountDynamicFilters(container, config, activeFilters, options) {
    if (typeof document === 'undefined') return;
    var opts = options || {};
    var onFilterChange = opts.onChange || function () {};
    var debounceTextMs = opts.debounceTextMs != null ? opts.debounceTextMs : 250;
    var allowFilterTypeEditing =
      opts.allowFilterTypeEditing !== undefined ? !!opts.allowFilterTypeEditing : ALLOW_FILTER_TYPE_EDITING;
    var adminPersist = opts.adminPersist;

    /* Pending text debounces close over a previous mount's state; clear them before remount or pill/hide remount re-applies stale values (e.g. captain name). */
    if (typeof container._dfClearTextDebounce === 'function') {
      container._dfClearTextDebounce();
    }

    var state = Object.assign({}, activeFilters || {});
    var cfgList = applyTypeOverrides(config, filterTypeOverrides);
    container.innerHTML = '';

    var pendingTextTimerIds = [];
    function clearPendingTextTimers() {
      for (var pi = 0; pi < pendingTextTimerIds.length; pi++) {
        clearTimeout(pendingTextTimerIds[pi]);
      }
      pendingTextTimerIds.length = 0;
    }
    container._dfClearTextDebounce = clearPendingTextTimers;

    var textTimers = {};

    cfgList.forEach(function (row) {
      var key = row.key;
      var initial = state[key];

      var section = createFilterSection(
        row,
        initial,
        function (payload) {
          var k = payload.key;
          var v = payload.value;
          if (row.type === 'text') {
            clearTimeout(textTimers[k]);
            var tId = setTimeout(function () {
              if (v == null || v === '') delete state[k];
              else state[k] = v;
              onFilterChange(Object.assign({}, state));
            }, debounceTextMs);
            textTimers[k] = tId;
            pendingTextTimerIds.push(tId);
            return;
          }
          if (row.type === 'range') {
            if (!v || typeof v !== 'object') {
              delete state[k];
            } else if (row.rangeMode === 'date') {
              var dLo = row.dateMin ? String(row.dateMin).slice(0, 10) : '';
              var dHi = row.dateMax ? String(row.dateMax).slice(0, 10) : '';
              var vf = v.from ? String(v.from).slice(0, 10) : '';
              var vt = v.to ? String(v.to).slice(0, 10) : '';
              var bothBlankD = !v.from && !v.to;
              var fullSpanD = dLo && dHi && vf === dLo && vt === dHi;
              if (bothBlankD || fullSpanD) delete state[k];
              else state[k] = v;
            } else {
              var rLo = row.min != null ? Number(row.min) : NaN;
              var rHi = row.max != null ? Number(row.max) : NaN;
              var a = v.min != null && v.min !== '' ? Number(v.min) : NaN;
              var b = v.max != null && v.max !== '' ? Number(v.max) : NaN;
              var bothBlank =
                (v.min == null || v.min === '') && (v.max == null || v.max === '');
              var fullSpan =
                !isNaN(rLo) && !isNaN(rHi) && !isNaN(a) && !isNaN(b) && a === rLo && b === rHi;
              if (bothBlank || fullSpan || (isNaN(a) && isNaN(b))) delete state[k];
              else state[k] = v;
            }
            onFilterChange(Object.assign({}, state));
            return;
          }
          if (v == null || v === '') delete state[k];
          else state[k] = v;
          onFilterChange(Object.assign({}, state));
        },
        allowFilterTypeEditing
          ? function (newType) {
              if (newType === 'range') {
                row.type = 'range';
                if (row.dateMin && row.dateMax) row.rangeMode = 'date';
                else row.rangeMode = 'number';
              } else {
                row.type = newType;
                delete row.rangeMode;
              }
              delete state[key];
              section._replaceBody(row, state[key]);
              onFilterChange(Object.assign({}, state));
            }
          : undefined,
        allowFilterTypeEditing,
        { adminPersist: adminPersist }
      );

      container.appendChild(section);
    });

  }

  /**
   * Human summary line for active filters (pills helper).
   * @param {Record<string, unknown>} active
   * @param {ReturnType<typeof buildFilterConfig>} filterConfig
   */
  function summarizeActiveFilters(active, filterConfig) {
    var parts = [];
    var labelByKey = {};
    for (var i = 0; i < filterConfig.length; i++) labelByKey[filterConfig[i].key] = filterConfig[i].label;

    var keys = Object.keys(active || {});
    for (var j = 0; j < keys.length; j++) {
      var k = keys[j];
      var v = active[k];
      var lab = labelByKey[k] || k;
      if (v && typeof v === 'object' && !Array.isArray(v)) {
        parts.push(lab + ': ' + JSON.stringify(v));
      } else {
        parts.push(lab + ': ' + String(v));
      }
    }
    return parts.join(' · ');
  }

  return {
    filterTypeOverrides: filterTypeOverrides,
    FILTER_TYPES: FILTER_TYPES.slice(),
    ALLOW_FILTER_TYPE_EDITING: ALLOW_FILTER_TYPE_EDITING,
    STRING_RADIO_MAX_UNIQUE: STRING_RADIO_MAX_UNIQUE,
    flattenEntity: flattenEntity,
    keyToLabel: keyToLabel,
    buildFilterConfig: buildFilterConfig,
    applyTypeOverrides: applyTypeOverrides,
    ensureRangeBoundsFromEntities: ensureRangeBoundsFromEntities,
    ensureEnumOptionsFromEntities: ensureEnumOptionsFromEntities,
    applyFilters: applyFilters,
    serializeActiveFilters: serializeActiveFilters,
    parseActiveFilters: parseActiveFilters,
    parseDfFromSearchParams: parseDfFromSearchParams,
    legacyQueryToActiveFilters: legacyQueryToActiveFilters,
    mergeActiveFromRequest: mergeActiveFromRequest,
    stripNoopRangeFilters: stripNoopRangeFilters,
    createFilterSection: createFilterSection,
    mountDynamicFilters: mountDynamicFilters,
    summarizeActiveFilters: summarizeActiveFilters
  };
});
