/**
 * Dynamic list filters client module — loaded via partials/dynamic-list-filters-scripts.ejs.
 * Wires DynamicFilters UI, URL (?q & ?df), pills, partial refresh, floating dock.
 * Reads #dynamicListRuntimeConfig: listId, pageBasePath, partialPath, summaryAllLabel,
 * domPrefix, resultsPanelId, paginationAriaLabel, refreshMode, adapterName, adapterOptions
 * (all emitted from the server view model for each entity list).
 *
 * Adapter contract (optional, for custom page DOM):
 * - getRows(ctx): any[] for clientMemory mode
 * - renderClientResults(rows, ctx): render filtered rows in clientMemory mode
 * - onError(err, ctx): optional error hook
 *
 * Registry:
 * - window.DynamicListPageAdapters[name] = adapterObject
 */
(function () {
  var DF = typeof window !== 'undefined' ? window.DynamicFilters : null;
  var BOOT = typeof window !== 'undefined' ? window.__DlfBootstrap : null;

  var RTC = {};
  try {
    var rtcEl = document.getElementById('dynamicListRuntimeConfig');
    if (rtcEl && rtcEl.textContent) RTC = JSON.parse(rtcEl.textContent) || {};
  } catch (e) {}
  if (BOOT && BOOT.runtime && typeof BOOT.runtime === 'object') {
    RTC = Object.assign({}, RTC, BOOT.runtime);
  }

  var dockMeta = document.querySelector('.fleet-filter-dock[data-dlf-prefix]');
  function metaAttr(el, name) {
    if (!el || !el.getAttribute) return '';
    var s = el.getAttribute(name);
    return s != null && String(s).trim() ? String(s).trim() : '';
  }
  var prefixFromHtml = metaAttr(dockMeta, 'data-dlf-prefix');
  var resultsPanelFromHtml = metaAttr(dockMeta, 'data-dlf-results-panel');
  var listIdFromHtml = metaAttr(dockMeta, 'data-dlf-list-id');
  if (prefixFromHtml) RTC.domPrefix = prefixFromHtml;
  if (resultsPanelFromHtml) RTC.resultsPanelId = resultsPanelFromHtml;
  if (listIdFromHtml) RTC.listId = listIdFromHtml;

  function domIdsFromPrefix(p) {
    var prefix = p != null && String(p) ? String(p) : 'fleet';
    return {
      searchForm: prefix + 'SearchForm',
      filterForm: prefix + 'FilterForm',
      mount: prefix + 'DynamicFiltersMount',
      configJson: prefix + 'FilterConfigJson',
      activeFiltersJson: prefix + 'ActiveFiltersJson',
      persistedOverridesJson: prefix + 'PersistedOverridesJson',
      hiddenFilterKeysJson: prefix + 'HiddenFilterKeysJson',
      filterDock: prefix + 'FilterDock',
      floatToggle: prefix + 'FilterFloatToggle',
      sideToggle: prefix + 'FilterSideToggle',
      inlineToggle: prefix + 'FilterInlineToggle',
      dragBar: prefix + 'FilterDragBar',
      addFilterDropdown: prefix + 'AddFilterDropdown',
      restoreHiddenBtn: prefix + 'RestoreHiddenFiltersBtn',
      toggleBtn: prefix + 'FilterToggleBtn',
      dockBody: prefix + 'FilterDockBody',
      summary: prefix + 'FilterSummary',
      searchOpenBtn: prefix + 'FilterSearchOpenBtn',
      searchInput: prefix + 'FilterSearchInput',
      pills: prefix + 'FilterPills'
    };
  }

  var IDS = domIdsFromPrefix(RTC.domPrefix);
  var RESULTS_PANEL_ID = RTC.resultsPanelId || 'fleetResultsPanel';
  var LIST_ID = RTC.listId || 'fleet';
  var REFRESH_MODE = RTC.refreshMode === 'clientMemory' ? 'clientMemory' : 'serverPartial';
  var ADAPTER_NAME =
    RTC.adapterName != null && String(RTC.adapterName).trim() ? String(RTC.adapterName).trim() : '';
  var ADAPTER_OPTIONS =
    RTC.adapterOptions && typeof RTC.adapterOptions === 'object' && !Array.isArray(RTC.adapterOptions)
      ? Object.assign({}, RTC.adapterOptions)
      : {};
  var ADAPTER_REGISTRY =
    typeof window !== 'undefined' &&
    window.DynamicListPageAdapters &&
    typeof window.DynamicListPageAdapters === 'object'
      ? window.DynamicListPageAdapters
      : {};
  var PAGE_ADAPTER = null;
  if (BOOT && BOOT.adapter && typeof BOOT.adapter === 'object') {
    PAGE_ADAPTER = BOOT.adapter;
  } else if (ADAPTER_NAME && ADAPTER_REGISTRY[ADAPTER_NAME]) {
    PAGE_ADAPTER = ADAPTER_REGISTRY[ADAPTER_NAME];
  }
  var locPath =
    typeof window !== 'undefined' && window.location
      ? String(window.location.pathname || '/').replace(/\/+$/, '') || '/'
      : '';
  var PAGE_BASE = RTC.pageBasePath;
  var PARTIAL_PATH = RTC.partialPath;
  if (!PAGE_BASE) {
    if (LIST_ID === 'captains') PAGE_BASE = locPath === '/captains' ? '/captains' : '/captains/roster';
    else if (LIST_ID === 'classes') PAGE_BASE = '/classes';
    else PAGE_BASE = '/fleet';
  }
  if (!PARTIAL_PATH) {
    if (LIST_ID === 'captains')
      PARTIAL_PATH = PAGE_BASE === '/captains' ? '/captains/partial' : '/captains/roster/partial';
    else if (LIST_ID === 'classes') PARTIAL_PATH = '/classes/partial';
    else PARTIAL_PATH = '/fleet/partial';
  }

  /**
   * Relative paths break partial refresh: e.g. partialPath "partial" on /classes resolves to /partial?q=… (404).
   * Same class of bug as captains if runtime JSON overwrites absolute URLs with relative fragments.
   */
  function ensureLeadingSlashPath(p) {
    if (p == null || p === '') return p;
    var s = String(p).trim();
    if (s.charAt(0) === '/') return s;
    return '/' + s.replace(/^\/+/, '');
  }
  PAGE_BASE = ensureLeadingSlashPath(PAGE_BASE);
  var _pb = PAGE_BASE || locPath;
  if (PARTIAL_PATH && String(PARTIAL_PATH).trim().charAt(0) !== '/') {
    var base = (_pb || '').replace(/\/+$/, '');
    PARTIAL_PATH = (base ? base + '/' : '/') + String(PARTIAL_PATH).trim().replace(/^\/+/, '');
  } else {
    PARTIAL_PATH = ensureLeadingSlashPath(PARTIAL_PATH);
  }
  /** Wrong absolute e.g. /partial (from bad runtime JSON) must not stay — always under PAGE_BASE. */
  if (PAGE_BASE && String(PAGE_BASE).charAt(0) === '/') {
    var pbNorm = PAGE_BASE.replace(/\/+$/, '');
    var expectedPartial = pbNorm + '/partial';
    var ppNorm = PARTIAL_PATH ? String(PARTIAL_PATH).trim() : '';
    if (
      !ppNorm ||
      ppNorm === 'partial' ||
      ppNorm === '/partial' ||
      (ppNorm.charAt(0) === '/' && ppNorm.indexOf(pbNorm + '/') !== 0)
    ) {
      PARTIAL_PATH = expectedPartial;
    }
  }

  var searchForm = document.getElementById(IDS.searchForm);
  if (!searchForm || !DF) return;

  var filterForm = document.getElementById(IDS.filterForm);
  var mountEl = document.getElementById(IDS.mount);
  var configScript = document.getElementById(IDS.configJson);
  var activeScript = document.getElementById(IDS.activeFiltersJson);
  var persistedScript = document.getElementById(IDS.persistedOverridesJson);
  var hiddenScript = document.getElementById(IDS.hiddenFilterKeysJson);

  var filterConfig = [];
  try {
    if (configScript && configScript.textContent) {
      filterConfig = JSON.parse(configScript.textContent);
    }
  } catch (e) {
    console.warn('dynamic list filter config JSON parse failed', e);
  }

  var activeFiltersState = {};
  try {
    if (activeScript && activeScript.textContent) {
      activeFiltersState = JSON.parse(activeScript.textContent) || {};
    }
  } catch (e) {
    console.warn('dynamic list active filters JSON parse failed', e);
  }

  var persistedOverridesState = {};
  try {
    if (persistedScript && persistedScript.textContent) {
      persistedOverridesState = JSON.parse(persistedScript.textContent) || {};
    }
  } catch (e) {
    console.warn('dynamic list persisted overrides JSON parse failed', e);
  }

  var hiddenFilterKeysState = [];
  try {
    if (hiddenScript && hiddenScript.textContent) {
      var _hk = JSON.parse(hiddenScript.textContent);
      hiddenFilterKeysState = Array.isArray(_hk) ? _hk : [];
    }
  } catch (e) {
    console.warn('dynamic list hidden filter keys JSON parse failed', e);
  }

  var fleetDebounceTimer;

  function isFleetAdminMode() {
    return document.body.classList.contains('admin-mode');
  }

  function persistFleetFilterAdmin(patch) {
    patch = patch || {};
    var body = { listId: LIST_ID };
    if (Object.prototype.hasOwnProperty.call(patch, 'overrides')) {
      body.overrides = patch.overrides;
    }
    if (Object.prototype.hasOwnProperty.call(patch, 'hiddenKeys')) {
      body.hiddenKeys = patch.hiddenKeys;
    }
    if (!Object.keys(body).filter(function (k) { return k !== 'listId'; }).length) return;
    var encoded = new URLSearchParams();
    encoded.set('payload', JSON.stringify(body));
    function persistRequest(method) {
      return fetch('/admin/fleet-filters/config', {
        method: method,
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          Accept: 'application/json'
        },
        credentials: 'same-origin',
        body: encoded.toString()
      }).then(function (r) {
        if ((r.status === 404 || r.status === 405) && method === 'PUT') {
          return persistRequest('POST');
        }
        return r;
      });
    }
    persistRequest('PUT')
      .then(function (r) {
        if (!r.ok) {
          return r.text().then(function (t) {
            var msg = r.statusText || 'Error';
            try {
              var j = JSON.parse(t);
              if (j && j.error) msg = j.error;
              else if (t && t.length < 400) msg = t;
            } catch (e) {
              if (t && t.length < 400) msg = t;
            }
            throw new Error(msg);
          });
        }
        return r.json();
      })
      .then(function () {
        window.location.reload();
      })
      .catch(function (err) {
        window.alert('Could not save filter settings: ' + (err.message || String(err)));
      });
  }

  function readFleetFiltersFromDom() {
    var qEl = searchForm.querySelector('input[name=q]');
    var qVal = qEl && qEl.value ? String(qEl.value).trim() : '';
    return { q: qVal, activeFilters: Object.assign({}, activeFiltersState) };
  }

  function allowedVisibleFilterKeys() {
    var m = {};
    for (var i = 0; i < filterConfig.length; i++) {
      m[filterConfig[i].key] = true;
    }
    return m;
  }

  /** Drop df keys for fields not in the current (visible) filter config — e.g. after admin hides a filter. */
  function sanitizeActiveFiltersToVisible(af) {
    var allow = allowedVisibleFilterKeys();
    var out = {};
    Object.keys(af || {}).forEach(function (k) {
      if (allow[k]) out[k] = af[k];
    });
    return out;
  }

  function activeFiltersFromUrlParams(p) {
    var merged = DF.mergeActiveFromRequest({
      df: p.get('df') || '',
      country: p.get('country') || '',
      type: p.get('type') || '',
      yearMin: p.get('yearMin') || '',
      yearMax: p.get('yearMax') || ''
    });
    return DF.stripNoopRangeFilters(sanitizeActiveFiltersToVisible(merged), filterConfig);
  }

  function parseFleetFiltersFromUrl() {
    var p = new URLSearchParams(window.location.search);
    return {
      q: p.get('q') || '',
      activeFilters: activeFiltersFromUrlParams(p),
      page: Math.max(1, parseInt(p.get('page'), 10) || 1)
    };
  }

  function buildFleetUrl(f, pageNum) {
    var p = new URLSearchParams();
    if (f.q) p.set('q', f.q);
    var keys = Object.keys(f.activeFilters || {});
    if (keys.length) {
      p.set('df', DF.serializeActiveFilters(f.activeFilters));
    }
    if (pageNum > 1) p.set('page', String(pageNum));
    var qs = p.toString();
    return qs ? PAGE_BASE + '?' + qs : PAGE_BASE;
  }

  function updateFleetFilterSummary() {
    var el = document.getElementById(IDS.summary);
    if (!el) return;
    var f = readFleetFiltersFromDom();
    var parts = [];
    if (f.q) parts.push('“' + f.q + '”');
    var summ = DF.summarizeActiveFilters(f.activeFilters, filterConfig);
    if (summ) parts.push(summ);
    el.textContent = parts.length ? parts.join(' · ') : (RTC.summaryAllLabel || 'Showing all');
  }

  function syncFleetSearchPlaceholder() {
    var dock = document.getElementById(IDS.filterDock);
    var input = document.getElementById(IDS.searchInput);
    if (!dock || !input) return;
    var min = dock.classList.contains('fleet-filter-dock--minimized');
    var full = input.dataset.placeholderFull;
    var short = input.dataset.placeholderShort || 'Search';
    if (full !== undefined && full !== '') input.placeholder = min ? short : full;
  }

  function isFleetSideMode() {
    var dock = document.getElementById(IDS.filterDock);
    return !!(dock && dock.classList.contains('fleet-filter-dock--side'));
  }
  function isFleetFloatingExpandedMode() {
    var dock = document.getElementById(IDS.filterDock);
    return !!(
      dock &&
      dock.classList.contains('fleet-filter-dock--floating') &&
      !dock.classList.contains('fleet-filter-dock--minimized')
    );
  }
  function isFleetSearchAlwaysExpandedMode() {
    return isFleetSideMode() || isFleetFloatingExpandedMode();
  }

  function syncFleetSearchHasQuery() {
    var dock = document.getElementById(IDS.filterDock);
    var wrap = dock ? dock.querySelector('.fleet-filter-dock__search-wrap') : document.querySelector('.fleet-filter-dock__search-wrap');
    var input = document.getElementById(IDS.searchInput);
    if (!wrap || !input) return;
    var v = input.value && String(input.value).trim();
    wrap.classList.toggle('fleet-filter-search--has-query', !!v);
    if (isFleetSearchAlwaysExpandedMode()) {
      wrap.classList.add('fleet-filter-search--expanded');
    }
  }

  function collapseFleetSearchExpandedIfIdle() {
    if (isFleetSearchAlwaysExpandedMode()) return;
    var dock = document.getElementById(IDS.filterDock);
    var wrap = dock ? dock.querySelector('.fleet-filter-dock__search-wrap') : document.querySelector('.fleet-filter-dock__search-wrap');
    var input = document.getElementById(IDS.searchInput);
    if (!wrap || !input) return;
    if (input.value && String(input.value).trim()) return;
    if (wrap.contains(document.activeElement)) return;
    wrap.classList.remove('fleet-filter-search--expanded');
  }

  /**
   * Options for DynamicFilters.mountDynamicFilters.
   * When Admin mode is on, each filter shows pencil + × to persist types (same JSON as former /admin/fleet-filters).
   */
  function getFleetDfMountOptions() {
    return {
      debounceTextMs: 250,
      allowFilterTypeEditing: false,
      adminPersist: {
        enabled: isFleetAdminMode(),
        persistedMap: persistedOverridesState,
        hiddenKeys: hiddenFilterKeysState.slice(),
        persist: persistFleetFilterAdmin
      },
      onChange: function (next) {
        activeFiltersState = Object.assign({}, next);
        pushFleetUrlAndRefresh(true);
      }
    };
  }

  function applyFleetFiltersToDom(f) {
    var qEl = searchForm.querySelector('input[name=q]');
    if (qEl) qEl.value = f.q || '';
    activeFiltersState = Object.assign({}, f.activeFilters || {});
    if (mountEl && filterConfig.length) {
      DF.mountDynamicFilters(mountEl, filterConfig, activeFiltersState, getFleetDfMountOptions());
    }
    updateFleetFilterSummary();
    syncFleetSearchHasQuery();
    if (!(f.q && String(f.q).trim())) {
      var finp = document.getElementById(IDS.searchInput);
      if (!finp || document.activeElement !== finp) {
        var fdock = document.getElementById(IDS.filterDock);
        var fwrap = fdock ? fdock.querySelector('.fleet-filter-dock__search-wrap') : document.querySelector('.fleet-filter-dock__search-wrap');
        if (fwrap && !isFleetSearchAlwaysExpandedMode()) fwrap.classList.remove('fleet-filter-search--expanded');
      }
    }
  }

  function getActiveFleetPills(f) {
    var pills = [];
    if (f.q) pills.push({ key: 'q', label: 'Search', value: f.q });
    var af = f.activeFilters || {};
    if (DF && typeof DF.stripNoopRangeFilters === 'function') {
      try {
        af = DF.stripNoopRangeFilters(af, filterConfig || []);
      } catch (_) {}
    }
    var labelByKey = {};
    for (var i = 0; i < filterConfig.length; i++) {
      labelByKey[filterConfig[i].key] = filterConfig[i].label;
    }
    Object.keys(af).forEach(function (k) {
      var v = af[k];
      if (v == null || v === '') return;
      var lab = labelByKey[k] || k;
      if (v && typeof v === 'object' && !Array.isArray(v)) {
        var keys = Object.keys(v);
        if (keys.length === 0) return;
        var hasMeaningful = false;
        for (var j = 0; j < keys.length; j++) {
          var rv = v[keys[j]];
          if (rv != null && String(rv).trim() !== '') {
            hasMeaningful = true;
            break;
          }
        }
        if (!hasMeaningful) return;
        pills.push({ key: k, label: lab, value: JSON.stringify(v), raw: v });
      } else {
        pills.push({ key: k, label: lab, value: String(v), raw: v });
      }
    });
    return pills;
  }

  function renderFleetPills(f) {
    var wrap = document.getElementById(IDS.pills);
    if (!wrap) return;
    var pills = getActiveFleetPills(f);
    wrap.innerHTML = '';
    pills.forEach(function (pill) {
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'btn btn-sm btn-outline-secondary rounded-pill';
      btn.setAttribute('aria-label', 'Remove ' + (pill.label ? pill.label + ': ' : '') + pill.value);
      btn.textContent = (pill.label ? pill.label + ': ' : '') + pill.value + ' ×';
      btn.addEventListener('click', function (e) {
        if (e) {
          e.preventDefault();
          e.stopPropagation();
        }
        removeFleetFilterPill(pill.key);
      });
      wrap.appendChild(btn);
    });
    if (pills.length) {
      var clear = document.createElement('button');
      clear.type = 'button';
      clear.className = 'btn btn-sm btn-link text-muted p-0 align-self-center';
      clear.textContent = 'Clear all';
      clear.addEventListener('click', function (e) {
        if (e) {
          e.preventDefault();
          e.stopPropagation();
        }
        clearAllFleetFilters();
      });
      wrap.appendChild(clear);
    }
  }

  function removeFleetFilterPill(key) {
    if (key === 'q') {
      var qEl = searchForm.querySelector('input[name=q]');
      if (qEl) qEl.value = '';
    } else {
      delete activeFiltersState[key];
      if (mountEl && filterConfig.length) {
        DF.mountDynamicFilters(mountEl, filterConfig, activeFiltersState, getFleetDfMountOptions());
      }
    }
    pushFleetUrlAndRefresh(true);
  }

  function clearAllFleetFilters() {
    applyFleetFiltersToDom({ q: '', activeFilters: {} });
    pushFleetUrlAndRefresh(true);
  }

  function pushFleetUrlAndRefresh(resetPage) {
    var f = readFleetFiltersFromDom();
    var page = resetPage ? 1 : parseFleetFiltersFromUrl().page;
    var url = buildFleetUrl(f, page);
    var cur = window.location.pathname + window.location.search;
    if (url !== cur) history.pushState({}, '', url);
    renderFleetPills(f);
    updateFleetFilterSummary();
    syncFleetSearchHasQuery();
    if (!f.q) collapseFleetSearchExpandedIfIdle();
    refreshFleetTableFromUrl(url);
  }

  function defaultClientSearch(rows, q) {
    if (!q) return rows;
    var qq = String(q || '').trim().toLowerCase();
    if (!qq) return rows;
    return (rows || []).filter(function (row) {
      var flat = DF.flattenEntity(row);
      var keys = Object.keys(flat || {});
      for (var i = 0; i < keys.length; i++) {
        var v = flat[keys[i]];
        if (
          String(v == null ? '' : v)
            .toLowerCase()
            .indexOf(qq) >= 0
        ) {
          return true;
        }
      }
      return false;
    });
  }

  function notifyAdapterError(err, ctx) {
    if (!PAGE_ADAPTER || typeof PAGE_ADAPTER.onError !== 'function') return;
    try {
      PAGE_ADAPTER.onError(err, ctx || {});
    } catch (_) {}
  }

  function createServerPartialController() {
    return {
      refreshFromUrl: function (url) {
        var panel = document.getElementById(RESULTS_PANEL_ID);
        if (!panel) return;
        var scrollY = window.scrollY;
        var u = new URL(url, window.location.origin);
        var partialUrl = PARTIAL_PATH + u.search;
        fetch(partialUrl, {
          headers: { Accept: 'text/html' },
          credentials: 'same-origin',
          cache: 'no-store'
        })
          .then(function (r) {
            return r.text();
          })
          .then(function (html) {
            panel.innerHTML = html;
            try {
              window.dispatchEvent(
                new CustomEvent('dlf:results-updated', {
                  detail: { panel: panel, url: partialUrl, listId: LIST_ID }
                })
              );
            } catch (_) {}
            requestAnimationFrame(function () {
              window.scrollTo(0, scrollY);
            });
          })
          .catch(function (e) {
            console.error('Dynamic list partial refresh failed', e);
            notifyAdapterError(e, { mode: 'serverPartial', listId: LIST_ID, url: partialUrl });
          });
      }
    };
  }

  function createClientMemoryController() {
    return {
      refreshFromUrl: function (url) {
        if (!PAGE_ADAPTER || typeof PAGE_ADAPTER.getRows !== 'function' || typeof PAGE_ADAPTER.renderClientResults !== 'function') {
          console.warn(
            'Dynamic list clientMemory mode needs adapter.getRows + adapter.renderClientResults. Falling back to serverPartial.'
          );
          return createServerPartialController().refreshFromUrl(url);
        }
        try {
          var u = new URL(url, window.location.origin);
          var p = new URLSearchParams(u.search);
          var q = p.get('q') || '';
          var page = Math.max(1, parseInt(p.get('page'), 10) || 1);
          var pageSize = Math.min(500, Math.max(1, parseInt(String(RTC.pageSize), 10) || 100));
          var rows = PAGE_ADAPTER.getRows({
            listId: LIST_ID,
            adapterName: ADAPTER_NAME,
            adapterOptions: ADAPTER_OPTIONS,
            runtime: RTC
          });
          rows = Array.isArray(rows) ? rows : [];
          var fromUrlFilters = activeFiltersFromUrlParams(p);
          var byFilter = DF.applyFilters(rows, fromUrlFilters, filterConfig);
          var bySearch = defaultClientSearch(byFilter, q);
          var start = (page - 1) * pageSize;
          var pageRows = bySearch.slice(start, start + pageSize);
          PAGE_ADAPTER.renderClientResults(pageRows, {
            listId: LIST_ID,
            total: bySearch.length,
            page: page,
            pageSize: pageSize,
            query: q,
            activeFilters: fromUrlFilters,
            runtime: RTC,
            adapterName: ADAPTER_NAME,
            adapterOptions: ADAPTER_OPTIONS,
            allRows: bySearch
          });
          try {
            window.dispatchEvent(
              new CustomEvent('dlf:results-updated', {
                detail: { panel: document.getElementById(RESULTS_PANEL_ID), listId: LIST_ID, mode: 'clientMemory' }
              })
            );
          } catch (_) {}
        } catch (e) {
          console.error('Dynamic list clientMemory refresh failed', e);
          notifyAdapterError(e, { mode: 'clientMemory', listId: LIST_ID, url: url });
        }
      }
    };
  }

  var resultsController = REFRESH_MODE === 'clientMemory' ? createClientMemoryController() : createServerPartialController();

  function refreshFleetTableFromUrl(url) {
    resultsController.refreshFromUrl(url);
  }

  searchForm.addEventListener('submit', function (e) {
    e.preventDefault();
    pushFleetUrlAndRefresh(true);
  });

  if (filterForm) {
    filterForm.addEventListener('submit', function (e) {
      e.preventDefault();
    });
  }

  var qInput = searchForm.querySelector('input[name=q]');
  if (qInput) {
    qInput.addEventListener('input', function () {
      syncFleetSearchHasQuery();
      clearTimeout(fleetDebounceTimer);
      fleetDebounceTimer = setTimeout(function () {
        pushFleetUrlAndRefresh(true);
      }, 450);
    });
  }

  window.addEventListener('popstate', function () {
    var parsed = parseFleetFiltersFromUrl();
    applyFleetFiltersToDom({ q: parsed.q, activeFilters: parsed.activeFilters });
    renderFleetPills(readFleetFiltersFromDom());
    refreshFleetTableFromUrl(window.location.pathname + window.location.search);
  });

  // When navigating back/forward, browsers may restore the old DOM from bfcache.
  // If an entity was deleted/updated in another tab/page, the table can look stale.
  function isBackForwardNavigation(ev) {
    try {
      if (ev && ev.persisted) return true;
      var nav = performance && performance.getEntriesByType ? performance.getEntriesByType('navigation') : [];
      if (nav && nav[0] && nav[0].type === 'back_forward') return true;
    } catch (_) {}
    return false;
  }
  window.addEventListener('pageshow', function (e) {
    if (!isBackForwardNavigation(e)) return;
    var parsed = parseFleetFiltersFromUrl();
    applyFleetFiltersToDom({ q: parsed.q, activeFilters: parsed.activeFilters });
    renderFleetPills(readFleetFiltersFromDom());
    refreshFleetTableFromUrl(window.location.pathname + window.location.search);
  });

  document.addEventListener('click', function (e) {
    var a = e.target.closest('nav[data-dlf-pagination] a.page-link');
    if (!a) return;
    var href = a.getAttribute('href');
    if (!href || href === '#' || a.closest('.disabled')) return;
    var u;
    try {
      u = new URL(href, window.location.origin);
    } catch (err) {
      return;
    }
    if (u.pathname !== PAGE_BASE) return;
    e.preventDefault();
    history.pushState({}, '', u.pathname + u.search);
    var p = new URLSearchParams(u.search);
    applyFleetFiltersToDom({ q: p.get('q') || '', activeFilters: activeFiltersFromUrlParams(p) });
    renderFleetPills(readFleetFiltersFromDom());
    refreshFleetTableFromUrl(u.pathname + u.search);
  });

  var fleetDock = document.getElementById(IDS.filterDock);
  var fleetToggleBtn = document.getElementById(IDS.toggleBtn);
  var suppressDockToggleClickUntil = 0;
  function setDockMinimized(min) {
    if (!fleetDock || !fleetToggleBtn) return;
    if (min && fleetDock.classList.contains('fleet-filter-dock--side')) return;
    fleetDock.classList.toggle('fleet-filter-dock--minimized', min);
    fleetToggleBtn.setAttribute('aria-expanded', min ? 'false' : 'true');
    fleetToggleBtn.title =
      min && fleetDock.classList.contains('fleet-filter-dock--floating')
        ? 'Open filters'
        : min
          ? 'Expand filters'
          : 'Hide filters';
    var toggleIcon = fleetToggleBtn.querySelector('.fleet-filter-toggle-icon');
    if (toggleIcon) {
      if (min && fleetDock.classList.contains('fleet-filter-dock--floating')) {
        toggleIcon.className = 'bi bi-funnel-fill fleet-filter-toggle-icon flex-shrink-0';
      } else {
        toggleIcon.className = 'bi bi-chevron-down fleet-filter-toggle-icon flex-shrink-0';
      }
    }
    var dockBody = document.getElementById(IDS.dockBody);
    if (dockBody) {
      if (min) dockBody.setAttribute('aria-hidden', 'true');
      else dockBody.removeAttribute('aria-hidden');
    }
    if (min) {
      var asideSearch = fleetDock
        ? fleetDock.querySelector('.fleet-filter-dock__search-wrap')
        : document.querySelector('.fleet-filter-dock__search-wrap');
      if (asideSearch) asideSearch.classList.remove('fleet-filter-search--expanded');
    } else if (isFleetFloatingExpandedMode() || isFleetSideMode()) {
      var asideSearchOpen = fleetDock
        ? fleetDock.querySelector('.fleet-filter-dock__search-wrap')
        : document.querySelector('.fleet-filter-dock__search-wrap');
      if (asideSearchOpen) asideSearchOpen.classList.add('fleet-filter-search--expanded');
    }
    syncFleetSearchPlaceholder();
    try {
      sessionStorage.setItem('dfDockMinimized:' + LIST_ID, min ? '1' : '0');
    } catch (e) {}
    if (typeof window.__dlfDockFloatingSyncMin === 'function') {
      window.__dlfDockFloatingSyncMin();
    }
  }
  if (fleetToggleBtn && fleetDock) {
    try {
      if (sessionStorage.getItem('dfDockMinimized:' + LIST_ID) === '1') {
        setDockMinimized(true);
      }
    } catch (e) {}
    fleetToggleBtn.addEventListener('click', function () {
      if (Date.now() < suppressDockToggleClickUntil) return;
      if (fleetDock.classList.contains('fleet-filter-dock--side')) return;
      setDockMinimized(!fleetDock.classList.contains('fleet-filter-dock--minimized'));
    });
  }

  var filterDockEl = fleetDock;

  (function initFleetFilterFloatingWindow() {
    var TOPBAR = 56;
    var SIDE_MIN_WIDTH = 992;
    var PAD = 8;
    var MIN_W = 280;
    var MIN_H = 220;
    var DEF_W = 448;
    var DEF_H = 520;
    var dock = fleetDock;
    var floatBtn = document.getElementById(IDS.floatToggle);
    var inlineBtn = document.getElementById(IDS.inlineToggle);
    var dragBar = document.getElementById(IDS.dragBar);
    var sideBtn = document.getElementById(IDS.sideToggle);
    if (!dock || !floatBtn || !inlineBtn || !dragBar || !sideBtn) return;

    var FLOAT_KEY = 'dfDockFloat:' + LIST_ID;
    var LAYOUT_KEY = 'dfDockLayout:' + LIST_ID;
    var SIDE_KEY = 'dfDockSide:' + LIST_ID;
    var sideSuspendedByViewport = false;

    function readLayout() {
      try {
        var raw = localStorage.getItem(LAYOUT_KEY);
        if (!raw) return null;
        var o = JSON.parse(raw);
        if (!o || typeof o !== 'object') return null;
        return {
          left: Number(o.left),
          top: Number(o.top),
          width: Number(o.width),
          height: Number(o.height)
        };
      } catch (e) {
        return null;
      }
    }
    function writeLayout(box) {
      try {
        localStorage.setItem(
          LAYOUT_KEY,
          JSON.stringify({
            left: box.left,
            top: box.top,
            width: box.width,
            height: box.height
          })
        );
      } catch (e) {}
    }
    function readFloatPref() {
      try {
        return localStorage.getItem(FLOAT_KEY) === '1';
      } catch (e) {
        return false;
      }
    }
    function writeFloatPref(on) {
      try {
        localStorage.setItem(FLOAT_KEY, on ? '1' : '0');
      } catch (e) {}
    }
    function readSidePref() {
      try {
        var v = localStorage.getItem(SIDE_KEY);
        if (v == null) return true; // default to side layout unless user explicitly changed it
        return v === '1';
      } catch (e) {
        return true;
      }
    }
    function writeSidePref(on) {
      try {
        localStorage.setItem(SIDE_KEY, on ? '1' : '0');
      } catch (e) {}
    }
    function canUseSideMode() {
      return window.innerWidth >= SIDE_MIN_WIDTH;
    }
    function syncSideButtonAvailability() {
      var canSide = canUseSideMode();
      sideBtn.disabled = !canSide;
      sideBtn.setAttribute('aria-disabled', canSide ? 'false' : 'true');
      sideBtn.setAttribute('title', canSide ? 'Side mode' : 'Side mode disabled on small screens');
    }
    function syncLayoutModeButtons() {
      var floating = dock.classList.contains('fleet-filter-dock--floating');
      var side = dock.classList.contains('fleet-filter-dock--side');
      var inline = !floating && !side;
      var setActive = function (btn, active) {
        if (!btn) return;
        btn.setAttribute('aria-pressed', active ? 'true' : 'false');
        btn.classList.toggle('fleet-filter-mode-btn--active', !!active);
      };
      setActive(inlineBtn, inline);
      setActive(sideBtn, side);
      setActive(floatBtn, floating);
    }
    function ensureSideLayoutWrap() {
      var resultsPanel = document.getElementById(RESULTS_PANEL_ID);
      if (!resultsPanel || !dock || !dock.parentNode) return null;
      var existing = dock.closest('.fleet-filter-layout');
      if (existing && existing.contains(resultsPanel)) return existing;
      if (dock.parentNode !== resultsPanel.parentNode) return null;
      var wrap = document.createElement('div');
      wrap.className = 'fleet-filter-layout';
      dock.parentNode.insertBefore(wrap, dock);
      wrap.appendChild(dock);
      wrap.appendChild(resultsPanel);
      return wrap;
    }
    function nearestContainerEl() {
      var el = dock;
      while (el && el !== document.body) {
        if (
          el.classList &&
          (el.classList.contains('container') || el.classList.contains('container-fluid'))
        ) {
          return el;
        }
        el = el.parentNode;
      }
      return null;
    }
    function setSideMode(on) {
      var wrap = ensureSideLayoutWrap();
      var containerEl = nearestContainerEl();
      var canSide = canUseSideMode();
      var enabled = !!(on && wrap && canSide);
      dock.classList.toggle('fleet-filter-dock--side', enabled);
      if (wrap) wrap.classList.toggle('fleet-filter-layout--side', enabled);
      if (containerEl) containerEl.classList.toggle('fleet-filter-container--fullwidth', enabled);
      if (fleetToggleBtn) {
        fleetToggleBtn.disabled = enabled;
        fleetToggleBtn.setAttribute('aria-disabled', enabled ? 'true' : 'false');
        fleetToggleBtn.setAttribute(
          'title',
          enabled ? 'Minimize is disabled in side layout' : 'Show or hide filters'
        );
      }
      if (enabled) {
        setDockMinimized(false);
      }
      var searchWrap = dock.querySelector('.fleet-filter-dock__search-wrap');
      if (searchWrap) {
        if (enabled) searchWrap.classList.add('fleet-filter-search--expanded');
        else collapseFleetSearchExpandedIfIdle();
      }
      sideBtn.setAttribute('aria-pressed', enabled ? 'true' : 'false');
      sideBtn.setAttribute(
        'title',
        enabled ? 'Return filters above results' : 'Show filters beside results'
      );
      if (canSide) writeSidePref(enabled);
      syncLayoutModeButtons();
      syncSideButtonAvailability();
    }
    function clampBox(box) {
      var w = window.innerWidth;
      var h = window.innerHeight;
      var maxW = w - PAD * 2;
      var maxH = h - PAD * 2;
      box.width = Math.min(Math.max(box.width, MIN_W), maxW);
      box.height = Math.min(Math.max(box.height, MIN_H), maxH);
      box.left = Math.min(Math.max(PAD, box.left), w - box.width - PAD);
      box.top = Math.min(Math.max(TOPBAR + PAD, box.top), h - box.height - PAD);
      return box;
    }
    function applyBox(box) {
      dock.style.left = Math.round(box.left) + 'px';
      dock.style.top = Math.round(box.top) + 'px';
      dock.style.width = Math.round(box.width) + 'px';
      if (dock.classList.contains('fleet-filter-dock--minimized')) {
        dock.style.height = 'auto';
      } else {
        dock.style.height = Math.round(box.height) + 'px';
      }
    }
    function clearBoxStyles() {
      dock.style.left = '';
      dock.style.top = '';
      dock.style.right = '';
      dock.style.bottom = '';
      dock.style.width = '';
      dock.style.height = '';
    }
    function defaultBoxFromDock() {
      var r = dock.getBoundingClientRect();
      var width = Math.max(MIN_W, Math.min(DEF_W, r.width || DEF_W));
      var height = Math.max(MIN_H, Math.min(DEF_H, Math.round(window.innerHeight * 0.55)));
      return clampBox({
        left: Math.min(Math.max(PAD, r.left), window.innerWidth - width - PAD),
        top: Math.max(TOPBAR + PAD, r.top),
        width: width,
        height: height
      });
    }
    function persistCurrentBox() {
      if (!dock.classList.contains('fleet-filter-dock--floating')) return;
      var left = parseFloat(dock.style.left);
      var top = parseFloat(dock.style.top);
      var width = parseFloat(dock.style.width);
      var prev = readLayout();
      var height;
      if (dock.classList.contains('fleet-filter-dock--minimized')) {
        height = prev && !isNaN(prev.height) ? prev.height : DEF_H;
      } else {
        height = parseFloat(dock.style.height);
        if (isNaN(height)) height = prev && !isNaN(prev.height) ? prev.height : DEF_H;
      }
      if ([left, top, width, height].some(function (x) { return isNaN(x); })) return;
      writeLayout(clampBox({ left: left, top: top, width: width, height: height }));
    }
    function setFloating(on) {
      var icon = floatBtn.querySelector('i');
      if (on) {
        if (dock.classList.contains('fleet-filter-dock--side')) {
          setSideMode(false);
        }
        dock.classList.add('fleet-filter-dock--floating');
        var box = readLayout();
        if (!box || [box.left, box.top, box.width, box.height].some(function (x) { return isNaN(x); })) {
          box = defaultBoxFromDock();
        } else {
          box = clampBox({
            left: box.left,
            top: box.top,
            width: box.width,
            height: box.height
          });
        }
        writeLayout({
          left: box.left,
          top: box.top,
          width: box.width,
          height: box.height
        });
        applyBox(box);
        writeFloatPref(true);
        dragBar.classList.add('fleet-filter-dock__drag-bar--active');
        floatBtn.setAttribute('aria-pressed', 'true');
        floatBtn.setAttribute('title', 'Dock panel — return to page flow');
        if (icon) icon.className = 'bi bi-layout-sidebar-inset-reverse';
      } else {
        dock.classList.remove('fleet-filter-dock--floating', 'fleet-filter-dock--dragging');
        clearBoxStyles();
        writeFloatPref(false);
        dragBar.classList.remove('fleet-filter-dock__drag-bar--active');
        floatBtn.setAttribute('aria-pressed', 'false');
        floatBtn.setAttribute('title', 'Undock panel — move and resize like a window');
        if (icon) icon.className = 'bi bi-window-stack';
      }
      if (fleetDock && fleetDock.classList.contains('fleet-filter-dock--minimized')) {
        setDockMinimized(true);
      }
      syncLayoutModeButtons();
    }
    function toggleFloating() {
      setFloating(!dock.classList.contains('fleet-filter-dock--floating'));
    }

    dragBar.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      if (!dock.classList.contains('fleet-filter-dock--floating')) return;
      if (dock.classList.contains('fleet-filter-dock--minimized')) return;
      e.preventDefault();
      var startX = e.clientX;
      var startY = e.clientY;
      var curLeft = parseFloat(dock.style.left) || 0;
      var curTop = parseFloat(dock.style.top) || 0;
      var curW = parseFloat(dock.style.width) || DEF_W;
      var curH = dock.classList.contains('fleet-filter-dock--minimized')
        ? MIN_H
        : parseFloat(dock.style.height) || DEF_H;
      dock.classList.add('fleet-filter-dock--dragging');
      document.body.classList.add('fleet-filter-dock-dragging');
      function onMove(ev) {
        var dx = ev.clientX - startX;
        var dy = ev.clientY - startY;
        var box = clampBox({
          left: curLeft + dx,
          top: curTop + dy,
          width: curW,
          height: curH
        });
        dock.style.left = box.left + 'px';
        dock.style.top = box.top + 'px';
      }
      function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        dock.classList.remove('fleet-filter-dock--dragging');
        document.body.classList.remove('fleet-filter-dock-dragging');
        persistCurrentBox();
      }
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });

    dock.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      if (!dock.classList.contains('fleet-filter-dock--floating')) return;
      if (!dock.classList.contains('fleet-filter-dock--minimized')) return;
      if (!dock.contains(e.target)) return;
      e.preventDefault();
      e.stopPropagation();

      var rect = dock.getBoundingClientRect();
      var startX = e.clientX;
      var startY = e.clientY;
      var moved = false;
      var curLeft = rect.left;
      var curTop = rect.top;
      var curW = rect.width;
      var curH = Math.max(MIN_H, parseFloat(dock.style.height) || DEF_H);

      dock.style.left = Math.round(curLeft) + 'px';
      dock.style.top = Math.round(curTop) + 'px';
      dock.style.right = 'auto';
      dock.style.bottom = 'auto';
      dock.classList.add('fleet-filter-dock--dragging');
      document.body.classList.add('fleet-filter-dock-dragging');

      function onMove(ev) {
        var dx = ev.clientX - startX;
        var dy = ev.clientY - startY;
        if (!moved && (Math.abs(dx) > 3 || Math.abs(dy) > 3)) moved = true;
        var box = clampBox({
          left: curLeft + dx,
          top: curTop + dy,
          width: curW,
          height: curH
        });
        dock.style.left = box.left + 'px';
        dock.style.top = box.top + 'px';
      }
      function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        dock.classList.remove('fleet-filter-dock--dragging');
        document.body.classList.remove('fleet-filter-dock-dragging');
        if (moved) {
          suppressDockToggleClickUntil = Date.now() + 280;
        }
      }
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });

    var handles = dock.querySelectorAll('.fleet-filter-dock__resize');
    for (var hi = 0; hi < handles.length; hi++) {
      (function (el) {
        el.addEventListener('mousedown', function (e) {
          if (e.button !== 0 || !dock.classList.contains('fleet-filter-dock--floating')) return;
          if (dock.classList.contains('fleet-filter-dock--minimized')) return;
          e.preventDefault();
          e.stopPropagation();
          var startX = e.clientX;
          var startY = e.clientY;
          var startW = dock.offsetWidth;
          var startH = dock.offsetHeight;
          var isE = el.classList.contains('fleet-filter-dock__resize--e') || el.classList.contains('fleet-filter-dock__resize--se');
          var isS = el.classList.contains('fleet-filter-dock__resize--s') || el.classList.contains('fleet-filter-dock__resize--se');
          document.body.classList.add('fleet-filter-dock-resizing');
          function onMove(ev) {
            var dx = ev.clientX - startX;
            var dy = ev.clientY - startY;
            var w = startW + (isE ? dx : 0);
            var h = startH + (isS ? dy : 0);
            var left0 = parseFloat(dock.style.left) || 0;
            var top0 = parseFloat(dock.style.top) || 0;
            var box = clampBox({ left: left0, top: top0, width: w, height: h });
            dock.style.width = box.width + 'px';
            dock.style.height = box.height + 'px';
            dock.style.left = box.left + 'px';
            dock.style.top = box.top + 'px';
          }
          function onUp() {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.classList.remove('fleet-filter-dock-resizing');
            persistCurrentBox();
          }
          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        });
      })(handles[hi]);
    }

    floatBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      toggleFloating();
    });
    inlineBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      if (dock.classList.contains('fleet-filter-dock--floating')) {
        setFloating(false);
      }
      setSideMode(false);
    });
    sideBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      if (!canUseSideMode()) return;
      if (dock.classList.contains('fleet-filter-dock--floating')) {
        setFloating(false);
      }
      setSideMode(!dock.classList.contains('fleet-filter-dock--side'));
    });

    window.addEventListener('resize', function () {
      var canSideNow = canUseSideMode();
      syncSideButtonAvailability();
      if (!canSideNow && dock.classList.contains('fleet-filter-dock--side')) {
        sideSuspendedByViewport = true;
        setSideMode(false);
      } else if (
        canSideNow &&
        !dock.classList.contains('fleet-filter-dock--floating') &&
        !dock.classList.contains('fleet-filter-dock--side') &&
        (sideSuspendedByViewport || readSidePref())
      ) {
        setSideMode(true);
        sideSuspendedByViewport = false;
      } else if (canSideNow) {
        sideSuspendedByViewport = false;
      }
      if (!dock.classList.contains('fleet-filter-dock--floating')) return;
      var ly = readLayout();
      if (!ly) return;
      var b = clampBox({
        left: ly.left,
        top: ly.top,
        width: ly.width,
        height: ly.height
      });
      applyBox(b);
      writeLayout(b);
    });

    window.__dlfDockFloatingSyncMin = function () {
      if (!dock.classList.contains('fleet-filter-dock--floating')) return;
      if (dock.classList.contains('fleet-filter-dock--minimized')) {
        dock.style.height = 'auto';
      } else {
        var ly = readLayout();
        if (ly && !isNaN(ly.height)) dock.style.height = Math.round(ly.height) + 'px';
      }
    };

    if (readFloatPref()) {
      setFloating(true);
    }
    if (readSidePref() && !dock.classList.contains('fleet-filter-dock--floating')) {
      setSideMode(true);
    }
    syncSideButtonAvailability();
    syncLayoutModeButtons();
  })();

  var searchWrapAside = filterDockEl
    ? filterDockEl.querySelector('.fleet-filter-dock__search-wrap')
    : document.querySelector('.fleet-filter-dock__search-wrap');
  var fleetSearchInputEl = document.getElementById(IDS.searchInput);
  var fleetSearchOpenBtn = document.getElementById(IDS.searchOpenBtn);
  if (fleetSearchOpenBtn && searchWrapAside && fleetSearchInputEl) {
    fleetSearchOpenBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      searchWrapAside.classList.add('fleet-filter-search--expanded');
      fleetSearchInputEl.focus();
    });
    fleetSearchInputEl.addEventListener('blur', function () {
      if (isFleetSearchAlwaysExpandedMode()) return;
      if (fleetSearchInputEl.value && String(fleetSearchInputEl.value).trim()) return;
      requestAnimationFrame(function () {
        requestAnimationFrame(function () {
          if (!searchWrapAside) return;
          if (searchWrapAside.contains(document.activeElement)) return;
          if (fleetSearchInputEl.value && String(fleetSearchInputEl.value).trim()) return;
          searchWrapAside.classList.remove('fleet-filter-search--expanded');
        });
      });
    });
  }
  document.addEventListener('click', function (e) {
    if (!searchWrapAside) return;
    if (isFleetSearchAlwaysExpandedMode()) return;
    if (searchWrapAside.classList.contains('fleet-filter-search--has-query')) return;
    if (!searchWrapAside.classList.contains('fleet-filter-search--expanded')) return;
    if (searchWrapAside.contains(e.target)) return;
    searchWrapAside.classList.remove('fleet-filter-search--expanded');
  });

  document.addEventListener('click', function (e) {
    if (!fleetDock) return;
    if (fleetDock.classList.contains('fleet-filter-dock--minimized')) return;
    if (fleetDock.classList.contains('fleet-filter-dock--side')) return;
    if (
      fleetDock.classList.contains('fleet-filter-dock--dragging') ||
      document.body.classList.contains('fleet-filter-dock-dragging') ||
      document.body.classList.contains('fleet-filter-dock-resizing')
    ) {
      return;
    }
    var insideDock = false;
    try {
      var path = e.composedPath ? e.composedPath() : null;
      if (Array.isArray(path) && path.indexOf(fleetDock) >= 0) insideDock = true;
    } catch (_) {}
    if (!insideDock && fleetDock.contains(e.target)) insideDock = true;
    if (insideDock) return;
    setDockMinimized(true);
  });

  window.addEventListener('adminModeChanged', function () {
    if (!mountEl || !filterConfig.length) return;
    DF.mountDynamicFilters(mountEl, filterConfig, activeFiltersState, getFleetDfMountOptions());
    updateFleetFilterSummary();
  });

  var restoreHiddenBtn = document.getElementById(IDS.restoreHiddenBtn);
  if (restoreHiddenBtn) {
    restoreHiddenBtn.addEventListener('click', function () {
      persistFleetFilterAdmin({ hiddenKeys: [] });
    });
  }

  document.addEventListener('click', function (e) {
    var item = e.target && e.target.closest && e.target.closest('.fleet-add-filter-item');
    if (!item || !fleetDock || !fleetDock.contains(item)) return;
    var key = item.getAttribute('data-filter-key');
    if (!key) return;
    e.preventDefault();
    var next = hiddenFilterKeysState.filter(function (k) {
      return k !== key;
    });
    persistFleetFilterAdmin({ hiddenKeys: next });
  });

  if (mountEl && filterConfig.length) {
    (function syncAddressBarToVisibleFilters() {
      var parsed = parseFleetFiltersFromUrl();
      var f = readFleetFiltersFromDom();
      var canon = buildFleetUrl(f, parsed.page);
      var cur = window.location.pathname + window.location.search;
      if (canon !== cur) {
        history.replaceState({}, '', canon);
      }
    })();
    DF.mountDynamicFilters(mountEl, filterConfig, activeFiltersState, getFleetDfMountOptions());
  }

  syncFleetSearchHasQuery();
  syncFleetSearchPlaceholder();
  renderFleetPills(readFleetFiltersFromDom());
  updateFleetFilterSummary();
})();
