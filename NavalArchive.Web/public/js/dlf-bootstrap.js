/**
 * Dynamic List Filters bootstrap.
 * Reads runtime config JSON and resolves optional page adapter by name.
 * Exposes a normalized object consumed by dynamic-list-filters.js.
 */
(function () {
  if (typeof window === 'undefined' || typeof document === 'undefined') return;

  var runtime = {};
  try {
    var rtcEl = document.getElementById('dynamicListRuntimeConfig');
    if (rtcEl && rtcEl.textContent) runtime = JSON.parse(rtcEl.textContent) || {};
  } catch (_) {}

  var refreshMode = runtime.refreshMode === 'clientMemory' ? 'clientMemory' : 'serverPartial';
  var adapterName =
    runtime.adapterName != null && String(runtime.adapterName).trim()
      ? String(runtime.adapterName).trim()
      : '';
  var adapterOptions =
    runtime.adapterOptions &&
    typeof runtime.adapterOptions === 'object' &&
    !Array.isArray(runtime.adapterOptions)
      ? Object.assign({}, runtime.adapterOptions)
      : {};

  runtime = Object.assign({}, runtime, {
    refreshMode: refreshMode,
    adapterName: adapterName,
    adapterOptions: adapterOptions
  });

  var registry =
    window.DynamicListPageAdapters && typeof window.DynamicListPageAdapters === 'object'
      ? window.DynamicListPageAdapters
      : {};
  var adapter = null;
  if (adapterName && registry[adapterName] && typeof registry[adapterName] === 'object') {
    adapter = registry[adapterName];
  }

  window.__DlfBootstrap = {
    runtime: runtime,
    adapter: adapter,
    adapterName: adapterName,
    adapterOptions: adapterOptions
  };

  try {
    window.dispatchEvent(
      new CustomEvent('dlf:bootstrap-ready', {
        detail: {
          listId: runtime.listId || '',
          refreshMode: refreshMode,
          adapterName: adapterName
        }
      })
    );
  } catch (_) {}
})();
