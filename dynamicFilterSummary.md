# Dynamic Filter System Summary (NavalArchive)

This document describes the current dynamic-filter architecture in this repository after the hard-cut migration to the unified list contract.

## Scope

This summary focuses on:

- Unified list API contract in `NavalArchive.Api`
- Web/BFF dynamic list registry and route wiring in `NavalArchive.Web`
- Dynamic filter client lifecycle and runtime contract
- Current page implementations (`/fleet`, `/classes`, `/captains`, `/captains/roster`, `/gallery`, `/logs`)
- Admin endpoints for filter config persistence

It does not attempt to document every unrelated endpoint in the project.

---

## 1) High-level Architecture

### Main components

- `NavalArchive.Api`:
  - Owns unified endpoint `GET /api/lists/{entity}`
  - Builds `filterConfig`, applies `df` filters, applies `q` search, paginates rows
- `NavalArchive.Web`:
  - Registers dynamic list pages from `config/entity-types.json`
  - Calls API unified endpoint and renders EJS full page + partials
  - Persists admin filter overrides and hidden keys on disk
- Browser client:
  - Renders dynamic filter UI from server-provided JSON
  - Updates URL state (`q`, `df`, `page`, `pageSize`)
  - Refreshes results via partial fetch (`serverPartial`) or adapter rendering (`clientMemory`)

### Data flow (serverPartial mode)

1. User opens page, for example `/classes`.
2. Web route calls API `GET /api/lists/classes`.
3. API returns `items`, `filterConfig`, `activeFilters`, pagination metadata.
4. Web renders EJS with runtime config and filter JSON scripts.
5. User changes a filter.
6. Client updates URL query and fetches `/classes/partial?...`.
7. Web calls API again and renders partial template.
8. Client swaps results panel HTML in place.

---

## 2) Unified API Contract

## Endpoint

- `GET /api/lists/{entity}`

### Supported entities

- `ships` (and alias `ship`)
- `classes` (and alias `class`)
- `captains` (and alias `captain`)
- `logs` (and alias `log`)

### Query parameters

- `q` (string): free text search across flattened row values
- `df` (string): JSON object encoded as query string for active filters
- `page` (int, default `1`)
- `pageSize` (int, default `100`, clamped `1..500`)
- `profile` (string, optional): view/profile variant (for example gallery profile on ships)

### Response shape

```json
{
  "items": [ { "...": "..." } ],
  "total": 123,
  "page": 1,
  "pageSize": 100,
  "filterConfig": [
    {
      "key": "class.country",
      "label": "Class · Country",
      "type": "radio|dropdown|text|range",
      "rangeMode": "number|date|null",
      "options": ["..."],
      "min": 0,
      "max": 100,
      "dateMin": "yyyy-MM-dd",
      "dateMax": "yyyy-MM-dd"
    }
  ],
  "activeFilters": { "...": "..." },
  "runtimeHints": {
    "entity": "ships",
    "profile": "gallery"
  }
}
```

### Error behavior

- Unsupported `entity` returns `400 BadRequest` from `ListsController`.
- Invalid `df` JSON is ignored safely (treated as empty filters).

---

## 3) API Filter Generation and Matching Logic

Implemented in `NavalArchive.Api/Services/DynamicListService.cs`.

### Row loading

- `ships`: includes class and captain nested objects
- `classes`: class rows + ship count
- `captains`: captain rows + ship count
- `logs`: captain logs from `LogsDbContext`, with `excerpt` derived from `entry`

### Search behavior (`q`)

- Rows are flattened to key-value map (nested keys become dot paths, for example `class.country`).
- Match is case-insensitive contains on any flattened value.

### Filter config inference

- Boolean columns -> `radio` with `["true","false"]`
- ISO/date-like values -> `range` with `rangeMode: "date"` and bounds
- Numeric columns -> `range` with `rangeMode: "number"` and numeric bounds
- Low-cardinality string columns -> `radio` or `dropdown`
- High-cardinality string columns -> `text`

### Active filter application

- Active keys are sanitized against allowed `filterConfig` keys
- No-op range filters are stripped (full min/max bounds)
- Matching rules:
  - `text`: case-insensitive contains
  - `radio` / `dropdown`: case-insensitive equals
  - `range:number`: min/max numeric comparison
  - `range:date`: from/to date comparison

---

## 4) Web Dynamic List Registry (Entity-driven)

Implemented in `NavalArchive.Web/server.js` and powered by `NavalArchive.Web/config/entity-types.json`.

### Registry source of truth

- `entity-types.json` `dynamicList` blocks define paths, templates, partials, and runtime defaults.
- Optional overrides can be patched in `config/dynamic-lists.json` (`overrides` only).

### Route registration model

For each dynamic list definition, Web auto-registers:

- full page route: `path`
- partial route: `partialPath`

Each extra view in `dynamicList.views` also gets the same full + partial pattern.

### Runtime normalization safeguards

Web and EJS enforce:

- leading slash normalization
- partial path canonicalization to `pageBasePath + "/partial"`

This prevents broken relative `/partial` fetches.

---

## 5) Current Dynamic List Pages

From `entity-types.json`:

- Ship entity:
  - `/fleet` + `/fleet/partial` -> `fleet.ejs` + `partials/fleet-results-panel.ejs`, profile `fleet`
  - `/gallery` + `/gallery/partial` -> `gallery.ejs` + `partials/gallery-results-panel.ejs`, profile `gallery`
- Class entity:
  - `/classes` + `/classes/partial` -> `classes.ejs` + `partials/classes-results-panel.ejs`
- Captain entity:
  - `/captains/roster` + `/captains/roster/partial` -> `captains-roster.ejs` + `partials/captains-roster-panel.ejs`
  - `/captains` + `/captains/partial` -> `captains.ejs` + `partials/captains-gallery-panel.ejs`
- Log entity:
  - `/logs` + `/logs/partial` -> `logs.ejs` + `partials/logs-results-panel.ejs`

### Hard-cut status

- Dynamic pages now consume unified `GET /api/lists/{entity}` path through Web dynamic spec.
- Legacy logs search routes were removed from Web:
  - removed `GET /logs/search.json`
  - removed `POST /logs/search`
- Kept detail endpoint:
  - `GET /logs/day.json` (proxies to API `/api/logs/day`) for day-log modal.

---

## 6) Browser Runtime Contract

Runtime JSON is emitted by `partials/dynamic-list-filters.ejs` in script tag `#dynamicListRuntimeConfig`.

### Runtime keys

- `listId`
- `pageBasePath`
- `partialPath`
- `domPrefix`
- `resultsPanelId`
- `paginationAriaLabel`
- `summaryAllLabel`
- `refreshMode` (`serverPartial` or `clientMemory`)
- `adapterName`
- `adapterOptions`

### Bootstrap and main scripts

Included by `partials/dynamic-list-filters-scripts.ejs` in this order:

1. `dynamicFilters.js`
2. `dlf-bootstrap.js`
3. `dynamic-list-filters.js`

`dlf-bootstrap.js` resolves adapter and dispatches `dlf:bootstrap-ready`.

---

## 7) Client Behavior (`dynamic-list-filters.js`)

### Responsibilities

- Parse runtime config and merge bootstrap data
- Resolve DOM IDs from `domPrefix`
- Read server-emitted JSON:
  - `<prefix>FilterConfigJson`
  - `<prefix>ActiveFiltersJson`
  - `<prefix>PersistedOverridesJson`
  - `<prefix>HiddenFilterKeysJson`
- Mount dynamic filter controls using `window.DynamicFilters`
- Keep URL in sync with current search/filters
- Handle partial refresh and pagination click interception
- Support floating/undocked filter panel UX

### Refresh modes

- `serverPartial` (default):
  - Fetches `partialPath` with current query string
  - Replaces `resultsPanelId` HTML
- `clientMemory`:
  - Uses adapter `getRows()` + `renderClientResults()`
  - No partial route fetch required

### Optional adapter contract

Global registry:

- `window.DynamicListPageAdapters[name] = adapter`

Adapter methods:

- `getRows(ctx)` required for `clientMemory`
- `renderClientResults(rows, ctx)` required for `clientMemory`
- `onError(err, ctx)` optional

---

## 8) Page Integration Pattern (One-line-ish includes)

A dynamic-list page generally includes:

- `partials/dynamic-list-filters-head` in `<head>`
- `partials/dynamic-list-filters` before results panel
- `partials/dynamic-list-filters-scripts` near end of body

And must provide:

- a results container ID that matches `runtime.resultsPanelId`
- pagination `<nav data-dlf-pagination="1">` in partial template

For many pages this is effectively a low-touch/drop-in pattern, with page-specific markup only in result partial.

---

## 9) Admin Filter Configuration Endpoints (Web)

### GET config

- `GET /admin/fleet-filters/config?listId=<id>`
- Returns:
  - inferred keys and inferred types
  - saved type overrides
  - hidden keys

### Save config

- `PUT /admin/fleet-filters/config`
- `POST /admin/fleet-filters/config`

Payload supports:

- `listId`
- `overrides` object
- `hiddenKeys` array

Persisted files:

- Fleet:
  - `config/fleet-filter-overrides.json`
  - `config/fleet-filter-hidden.json`
- Captains:
  - `config/captain-filter-overrides.json`
  - `config/captain-filter-hidden.json`
- Other lists:
  - `config/dynamic-filters/<listId>-overrides.json`
  - `config/dynamic-filters/<listId>-hidden.json`

---

## 10) Endpoint Map (Dynamic-filter relevant)

### API

- `GET /api/lists/{entity}` unified list/filter endpoint
- `GET /api/logs/day` full day log detail (used by modal)

### Web

- Auto-registered list pages + partials from dynamic list registry:
  - `/fleet`, `/fleet/partial`
  - `/classes`, `/classes/partial`
  - `/captains`, `/captains/partial`
  - `/captains/roster`, `/captains/roster/partial`
  - `/gallery`, `/gallery/partial`
  - `/logs`, `/logs/partial`
- `GET /logs/day.json` proxy for modal detail fetch
- `GET/PUT/POST /admin/fleet-filters/config` admin config API

---

## 11) How to Add a New Dynamic-filter Entity/Page

1. Add entity metadata + `dynamicList` block in `config/entity-types.json`.
2. Define:
   - `path`
   - `partialPath`
   - page template
   - partial template
   - `domPrefix`, `resultsPanelId`, labels as needed
3. Ensure API supports entity in `DynamicListService.LoadRowsAsync`.
4. Page template:
   - include head partial, filter partial, scripts partial
   - include results panel wrapper with matching ID
5. Partial template:
   - render rows
   - render pagination with `data-dlf-pagination="1"`
6. Validate:
   - full route returns 200
   - partial route returns 200
   - changing filters updates URL and results

---

## 12) Troubleshooting Notes

- If partial fetch hits `/partial` instead of `/entity/partial`:
  - check runtime `pageBasePath` / `partialPath`
  - check template include merge order
  - ensure leading slash and canonicalization are preserved
- If filters show but no updates:
  - verify results wrapper ID equals `resultsPanelId`
  - verify partial route is registered and returns HTML
- If admin overrides do not apply:
  - verify `listId` normalization and on-disk JSON files
- If API returns empty `filterConfig`:
  - verify rows are loaded and flattenable values exist

---

## 13) Important Files Index

- API:
  - `NavalArchive.Api/Contracts/ListDtos.cs`
  - `NavalArchive.Api/Controllers/ListsController.cs`
  - `NavalArchive.Api/Services/DynamicListService.cs`
  - `NavalArchive.Api/Program.cs`
- Web:
  - `NavalArchive.Web/server.js`
  - `NavalArchive.Web/config/entity-types.json`
  - `NavalArchive.Web/config/dynamic-lists.json`
  - `NavalArchive.Web/public/js/dynamicFilters.js`
  - `NavalArchive.Web/public/js/dlf-bootstrap.js`
  - `NavalArchive.Web/public/js/dynamic-list-filters.js`
  - `NavalArchive.Web/views/partials/dynamic-list-filters.ejs`
  - `NavalArchive.Web/views/partials/dynamic-list-filters-scripts.ejs`
  - `NavalArchive.Web/views/partials/*-results-panel.ejs`

