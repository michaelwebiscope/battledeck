# Dynamic Filters Integration

This module is designed so pages can use dynamic filters without editing filter core logic.

## One-line include pattern

Use existing shared includes in the page template:

1. Head styles:
   - `partials/dynamic-list-filters-head`
2. Panel + runtime JSON:
   - `partials/dynamic-list-filters`
3. Scripts:
   - `partials/dynamic-list-filters-scripts`

For convention-compliant pages (same IDs/data attributes), this is enough.

## Runtime contract

`dynamicListRuntime` supports:

- `listId`: storage id for filter persistence
- `pageBasePath`: full page route
- `partialPath`: partial HTML route for server refresh mode
- `domPrefix`: DOM id prefix (`<prefix>SearchForm`, etc.)
- `resultsPanelId`: container replaced after refresh
- `paginationAriaLabel`: accessibility label for pagination nav
- `summaryAllLabel`: text when no filters are active
- `refreshMode`: `serverPartial` (default) or `clientMemory`
- `adapterName`: optional adapter key in `window.DynamicListPageAdapters`
- `adapterOptions`: optional object passed through to adapter

## Global API list contract

Dynamic list pages now use a unified API endpoint in `NavalArchive.Api`:

- `GET /api/lists/{entity}`
- Supported entities: `ships`, `classes`, `captains`
- Query params: `q`, `df`, `page`, `pageSize`, optional `profile`

Response shape:

- `items`
- `total`
- `page`
- `pageSize`
- `filterConfig`
- `activeFilters`

The web layer consumes this payload and renders page/partial templates without per-page filter logic.

## Refresh modes

- `serverPartial`:
  - Current default behavior.
  - Browser fetches `partialPath + querystring`, then replaces `resultsPanelId` inner HTML.

- `clientMemory`:
  - No partial fetch.
  - Requires adapter with `getRows()` and `renderClientResults()`.
  - Uses `DynamicFilters.applyFilters` plus search text on flattened entity fields.

## Optional adapter API

Register adapter globally:

```js
window.DynamicListPageAdapters = window.DynamicListPageAdapters || {};
window.DynamicListPageAdapters.myTableAdapter = {
  getRows: function (ctx) { return window.__rows || []; },
  renderClientResults: function (rows, ctx) {
    // page-specific DOM rendering
  },
  onError: function (err, ctx) {
    console.error('dynamic filter adapter error', err, ctx);
  }
};
```

Supported methods:

- `getRows(ctx)` -> `any[]` (required for `clientMemory`)
- `renderClientResults(rows, ctx)` (required for `clientMemory`)
- `onError(err, ctx)` (optional)

`ctx` includes list/runtime metadata (`listId`, `adapterName`, `adapterOptions`, pagination info).

## Migration checklist for a new page

1. Add the three partial includes (head, panel, scripts).
2. Ensure the page has a results container id matching runtime `resultsPanelId`.
3. Ensure pagination links are wrapped in `nav[data-dlf-pagination]`.
4. Set runtime:
   - `refreshMode: 'serverPartial'` for server-rendered partial flow.
   - `refreshMode: 'clientMemory'` + `adapterName` for custom in-page rendering.
5. Do not edit `public/js/dynamicFilters.js` or `public/js/dynamic-list-filters.js` for page-specific behavior; use runtime + adapter instead.
