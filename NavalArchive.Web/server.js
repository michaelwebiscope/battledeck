const express = require('express');
const axios = require('axios');
const path = require('path');
const fs = require('fs');

// New Relic browser agent: try to load (only present when deployed with -newrelic)
let newrelic = null;
try { newrelic = require('newrelic'); } catch (e) { /* not installed */ }

const app = express();
const PORT = process.env.PORT || 3000;
const API_BASE = process.env.API_URL || 'http://localhost:5000';

function imageUploadHandler(entity) {
  return async (req, res) => {
    try {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = Buffer.concat(chunks);
      const r = await axios({
        method: 'POST',
        url: `${API_BASE}/api/images/${entity}/${req.params.id}/upload`,
        data: body,
        headers: { 'Content-Type': req.headers['content-type'] || 'image/jpeg' },
        maxBodyLength: 10 * 1024 * 1024,
        timeout: 120000,
        validateStatus: () => true
      });
      res.status(r.status).json(r.data ?? {});
    } catch (err) {
      console.error('Image upload proxy error:', err.message);
      res.status(502).json({ error: err.message });
    }
  };
}
// All backend communication goes through API (Gateway, Video, etc.)
// Menu: use local file (no GitHub fetch). Override with MENU_URL for remote JSON.
const MENU_PATH = path.join(__dirname, 'public', 'menu.json');
const ENTITY_TYPES_PATH = path.join(__dirname, 'config', 'entity-types.json');

let entityTypes = {};
try {
  if (fs.existsSync(ENTITY_TYPES_PATH)) {
    entityTypes = JSON.parse(fs.readFileSync(ENTITY_TYPES_PATH, 'utf8'));
  }
} catch (e) {
  console.warn('Entity types config not loaded:', e.message);
}

// Fallback menu: flat list or grouped { label, items: [{ href, label }] } - uses entity types when available
const defaultNavItems = [
  { href: '/', label: 'Home' },
  { label: 'Explore', items: [
    { href: '/fleet', label: (entityTypes.ship && entityTypes.ship.listTitle) || 'Fleet Roster' },
    { href: '/compare', label: 'Compare' },
    { href: '/classes', label: (entityTypes.class && entityTypes.class.listTitle) || 'Ship Classes' },
    { href: '/captains', label: (entityTypes.captain && entityTypes.captain.listTitle) || 'Captains' },
    { href: '/timeline', label: 'Timeline' },
    { href: '/stats', label: 'Statistics' },
    { href: '/gallery', label: 'Photo Gallery' },
    { href: '/logs', label: 'Daily Logs' }
  ]},
  { href: '/simulation', label: 'Live Battle' },
  { label: 'Support', items: [
    { href: '/donate', label: 'Donate' },
    { href: '/payment-account', label: 'Payment Account' },
    { href: '/login', label: 'Login' }
  ]},
  { href: '/members', label: 'Member' },
  { href: '/checkout', label: 'Checkout' },
  { href: '/trace', label: 'Trace' }
];

let cachedNavItems = null;
let cacheExpiry = 0;
const MENU_CACHE_MS = 5 * 60 * 1000; // 5 min

function isValidNavItem(i) {
  if (i.href && i.label) return true;
  if (i.label && Array.isArray(i.items) && i.items.length > 0 && i.items.every((c) => c.href && c.label)) return true;
  return false;
}

async function fetchNavItems() {
  if (cachedNavItems && Date.now() < cacheExpiry) return cachedNavItems;
  const menuUrl = process.env.MENU_URL;
  try {
    let data;
    if (menuUrl) {
      const r = await axios.get(menuUrl, { timeout: 5000, validateStatus: (s) => s === 200 });
      data = r.data;
    } else if (fs.existsSync(MENU_PATH)) {
      data = JSON.parse(fs.readFileSync(MENU_PATH, 'utf8'));
    } else {
      return defaultNavItems;
    }
    const items = Array.isArray(data) ? data : (data?.items || data?.menu || []);
    if (items.length > 0 && items.every(isValidNavItem)) {
      cachedNavItems = items;
      cacheExpiry = Date.now() + MENU_CACHE_MS;
      return cachedNavItems;
    }
  } catch (err) {
    console.error('Menu load failed:', err.message);
  }
  return defaultNavItems;
}

app.set('view engine', 'ejs');
app.set('views', path.join(__dirname, 'views'));
app.use(express.static(path.join(__dirname, 'public')));

// Image upload routes MUST run before express.json/urlencoded so raw body is not consumed
app.post('/api/images/ship/:id/upload', imageUploadHandler('ship'));
app.post('/api/images/captain/:id/upload', imageUploadHandler('captain'));

app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Health check - no dependencies, before nav middleware
app.get('/health', (req, res) => res.status(200).send('OK'));

// Entity types config (data-driven UI, no hardcoded entity names)
app.get('/api/entity-types', (req, res) => {
  res.json(entityTypes);
});

// Dynamic nav menu from local menu.json (or MENU_URL if set), cached 5 min
app.use(async (req, res, next) => {
  try {
    res.locals.navItems = await fetchNavItems();
  } catch (e) {
    res.locals.navItems = defaultNavItems;
  }
  res.locals.currentPath = req.path;
  res.locals.toEntity = toEntity;
  res.locals.entityTypes = entityTypes;
  res.locals.nrBrowserHeader = newrelic ? newrelic.getBrowserTimingHeader() : '';
  res.locals.imageSearchPrefix = Object.fromEntries(
    Object.entries(entityTypes).filter(([, c]) => c.hasImage).map(([k, c]) => [k, c.displayName?.toLowerCase() || k])
  );
  next();
});

// Axios instance for API calls
const api = axios.create({
  baseURL: API_BASE,
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' }
});

/** Normalize API data to entity format using entity type config. No hardcoded entity names. */
function toEntity(item, type = 'ship') {
  if (!item) return null;
  const cfg = entityTypes[type] || {};
  const id = item.id ?? item.Id;
  const base = cfg.detailRoute || '/' + type + 's';
  const gallery = cfg.gallerySegment || 'image';
  const imageVersion = item.imageVersion ?? item.ImageVersion ?? 0;
  const fallbackUrl = item.imageUrl ?? item.ImageUrl ?? '';
  const viewImageUrl = gallery ? `/gallery/${gallery}/${id}?v=${imageVersion}` : fallbackUrl;
  let subtitle = '';
  if (cfg.subtitleField && item[cfg.subtitleField]) {
    subtitle = (cfg.subtitleFormat || '{value}').replace('{value}', item[cfg.subtitleField]);
  } else if (cfg.subtitleFields && Array.isArray(cfg.subtitleFields)) {
    const parts = cfg.subtitleFields.map(f => {
      const v = item[f];
      if (f === 'serviceYears' && v) return v + ' years service';
      return v;
    }).filter(Boolean);
    subtitle = parts.join(' · ');
  }
  return {
    ...item,
    id,
    name: item.name ?? item.Name ?? '',
    description: item.description ?? item.Description ?? '',
    subtitle,
    imageUrl: viewImageUrl,
    imageVersion,
    imageGallery: gallery,
    type,
    typeConfig: cfg,
    detailUrl: `${base}/${id}`
  };
}

// Trace: Web -> API -> Gateway. All backend calls go through API.
app.get('/api/trace', async (req, res) => {
  try {
    const r = await axios.get(`${API_BASE}/api/trace`, { timeout: 15000, validateStatus: () => true });
    res.json(r.data ?? { error: 'No response' });
  } catch (err) {
    console.error('Trace error:', err.message);
    res.status(502).json({ error: 'Trace chain unavailable. Ensure API is running.' });
  }
});

// Video streaming: Web -> API -> Video service. All backend calls go through API.
app.get('/api/videos/:shipId', async (req, res) => {
  try {
    const headers = {};
    if (req.headers.range) headers.Range = req.headers.range;
    const r = await axios({
      method: 'GET',
      url: `${API_BASE}/api/videos/${req.params.shipId}`,
      headers,
      responseType: 'stream',
      validateStatus: () => true
    });
    res.status(r.status);
    if (r.headers['content-type']) res.set('Content-Type', r.headers['content-type']);
    if (r.headers['content-length']) res.set('Content-Length', r.headers['content-length']);
    if (r.headers['accept-ranges']) res.set('Accept-Ranges', r.headers['accept-ranges']);
    if (r.headers['content-range']) res.set('Content-Range', r.headers['content-range']);
    r.data.pipe(res);
  } catch (err) {
    console.error('Video proxy error:', err.message);
    res.status(502).send('Video unavailable');
  }
});


// Explicit POST proxy for captain delete (workaround for 405 on DELETE from IIS/WebDAV)
app.post('/api/captains/delete/:id', async (req, res) => {
  try {
    const r = await api.post('/api/captains/delete/' + req.params.id, {}, { validateStatus: () => true });
    res.status(r.status).json(r.data ?? {});
  } catch (err) {
    console.error('API proxy error (POST captain delete):', err.message);
    res.status(502).json({ error: err.message });
  }
});

// Chain: App -> API -> Cart -> Card -> Payment (all requests go through API)
// Proxy /api/* to API for client-side fetches (local dev; on IIS, /api/* is rewritten to API)
app.use('/api', async (req, res) => {
  try {
    const url = `${API_BASE}/api${req.url}`;
      const headers = { 'Content-Type': 'application/json' };
      if (req.headers['x-api-key']) headers['X-API-Key'] = req.headers['x-api-key'];
      const opts = {
        method: req.method,
        url,
        headers,
        responseType: 'json',
        validateStatus: () => true
      };
    if (req.method !== 'GET' && req.method !== 'HEAD' && req.body && Object.keys(req.body).length) opts.data = req.body;
      const r = await axios(opts);
      res.status(r.status).json(r.data ?? {});
    } catch (err) {
    console.error('API proxy error:', err.message);
    res.status(502).json({ error: 'API unavailable' });
    }
});

// --- Routes ---

app.get('/trace', (req, res) => {
  res.render('trace', { title: 'Distributed Trace' });
});

app.get('/', async (req, res) => {
  // Fetch USS Enterprise (id 9) from API so we use gallery proxy - more reliable than direct Wikimedia URL
  let featuredShip = {
    id: 9,
      name: 'USS Enterprise (CV-6)',
      description: 'The most decorated ship of the Second World War. "The Big E" earned 20 battle stars and participated in nearly every major Pacific engagement.',
      year: 1938,
    imageUrl: '/gallery/image/9',
    imageVersion: 0
  };
  try {
    const shipRes = await api.get('/api/ships/9').catch(() => null);
    if (shipRes?.data) {
      const s = shipRes.data;
      featuredShip = {
        id: s.id || 9,
        name: s.name || featuredShip.name,
        description: s.description || featuredShip.description,
        year: s.yearCommissioned ?? featuredShip.year,
        imageUrl: '/gallery/image/' + (s.id || 9) + '?v=' + (s.imageVersion ?? 0),
        imageVersion: s.imageVersion ?? 0
      };
    }
  } catch (_) { /* use defaults */ }
  res.render('home', {
    title: 'Home',
    featuredShip
  });
});

app.get('/fleet', async (req, res) => {
  try {
    const params = { page: req.query.page || 1, pageSize: req.query.pageSize || 100 };
    if (req.query.country) params.country = req.query.country;
    if (req.query.type) params.type = req.query.type;
    if (req.query.yearMin) params.yearMin = req.query.yearMin;
    if (req.query.yearMax) params.yearMax = req.query.yearMax;
    const response = await api.get('/api/ships', { params });
    const data = response.data || {};
    const ships = data.items || [];
    const classesRes = await api.get('/api/classes').catch(() => ({ data: [] }));
    res.render('fleet', {
      title: 'Fleet Roster',
      ships,
      total: data.total || ships.length,
      page: data.page || 1,
      pageSize: data.pageSize || 100,
      searchQuery: '',
      classes: classesRes.data,
      filters: { country: req.query.country, type: req.query.type, yearMin: req.query.yearMin, yearMax: req.query.yearMax }
    });
  } catch (err) {
    console.error('Fleet API error:', err.message);
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: [],
      total: 0,
      page: 1,
      pageSize: 100,
      classes: [],
      filters: {},
      error: `Unable to load fleet data. Ensure the API is running at ${API_BASE}.`,
      searchQuery: ''
    });
  }
});

app.get('/fleet/search', async (req, res) => {
  try {
    const q = req.query.q || '';
    const response = q ? await api.get('/api/ships/search', { params: { q } }) : { data: [] };
    const ships = Array.isArray(response.data) ? response.data : [];
    const classesRes = await api.get('/api/classes').catch(() => ({ data: [] }));
    res.render('fleet', {
      title: 'Fleet Roster',
      ships,
      total: ships.length,
      page: 1,
      pageSize: ships.length,
      searchQuery: q,
      classes: classesRes.data,
      filters: {}
    });
  } catch (err) {
    console.error('Search API error:', err.message);
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: [],
      total: 0,
      page: 1,
      pageSize: 100,
      classes: [],
      filters: {},
      searchQuery: req.query.q || ''
    });
  }
});

app.get('/compare', async (req, res) => {
  const id1 = parseInt(req.query.id1, 10);
  const id2 = parseInt(req.query.id2, 10);
  if (!id1 || !id2 || id1 === id2) {
    try {
      const shipsRes = await api.get('/api/ships/choices', { params: { limit: 2000 } });
      const ships = Array.isArray(shipsRes.data) ? shipsRes.data : [];
      return res.render('compare', {
        title: 'Compare Ships',
        ship1: id1 ? (await api.get(`/api/ships/${id1}`).catch(() => null))?.data : null,
        ship2: null,
        ships,
        preselectedId: id1 || null
      });
    } catch (err) {
      return res.redirect('/fleet');
    }
  }
  try {
    const [r1, r2] = await Promise.all([
      api.get(`/api/ships/${id1}`),
      api.get(`/api/ships/${id2}`)
    ]);
    res.render('compare', {
      title: 'Compare Ships',
      ship1: r1.data,
      ship2: r2.data,
      ships: [],
      preselectedId: null
    });
  } catch (err) {
    res.redirect('/fleet');
  }
});

app.get('/discover', async (req, res) => {
  try {
    const response = await api.get('/api/ships/random');
    res.redirect(`/ships/${response.data.id}`);
  } catch (err) {
    res.redirect('/fleet');
  }
});

app.get('/ships/:id', async (req, res) => {
  try {
    const [shipRes, classesRes] = await Promise.all([
      api.get(`/api/ships/${req.params.id}`),
      api.get('/api/classes').catch(() => ({ data: [] }))
    ]);
    const ship = shipRes.data;
    const classes = Array.isArray(classesRes.data) ? classesRes.data : [];
    res.render('ship', {
      title: ship.name,
      ship,
      classes
    });
  } catch (err) {
    if (err.response?.status === 404) {
      return res.status(404).render('error', { title: 'Not Found', message: 'Ship not found.' });
    }
    console.error('Ship API error:', err.message);
    res.status(500).render('error', { title: 'Error', message: 'Unable to load ship details.' });
  }
});

app.get('/classes', async (req, res) => {
  try {
    const response = await api.get('/api/classes');
    const classes = Array.isArray(response.data) ? response.data : [];
    res.render('classes', {
      title: 'Ship Classes',
      classes
    });
  } catch (err) {
    console.error('Classes API error:', err.message);
    res.render('classes', {
      title: 'Ship Classes',
      classes: [],
      error: 'Unable to load ship classes.'
    });
  }
});

app.get('/classes/:id', async (req, res) => {
  try {
    const response = await api.get(`/api/classes/${req.params.id}`);
    res.render('class-detail', {
      title: response.data.name,
      shipClass: response.data
    });
  } catch (err) {
    if (err.response?.status === 404) {
      return res.status(404).render('error', { title: 'Not Found', message: 'Ship class not found.' });
    }
    console.error('Class API error:', err.message);
    res.status(500).render('error', { title: 'Error', message: 'Unable to load class details.' });
  }
});

app.get('/captains', async (req, res) => {
  try {
    const response = await api.get('/api/captains');
    res.render('captains', {
      title: 'Captains',
      captains: response.data
    });
  } catch (err) {
    console.error('Captains API error:', err.message);
    res.render('captains', {
      title: 'Captains',
      captains: [],
      error: 'Unable to load captains.'
    });
  }
});

app.get('/captains/:id', async (req, res) => {
  try {
    const response = await api.get(`/api/captains/${req.params.id}`);
    res.render('captain-detail', {
      title: response.data.name,
      captain: response.data
    });
  } catch (err) {
    if (err.response?.status === 404) {
      return res.status(404).render('error', { title: 'Not Found', message: 'Captain not found.' });
    }
    console.error('Captain API error:', err.message);
    res.status(500).render('error', { title: 'Error', message: 'Unable to load captain details.' });
  }
});

app.get('/stats', async (req, res) => {
  try {
    const response = await api.get('/api/stats');
    res.render('stats', {
      title: 'Museum Statistics',
      stats: response.data
    });
  } catch (err) {
    console.error('Stats API error:', err.message);
    res.render('stats', {
      title: 'Museum Statistics',
      stats: null,
      error: 'Unable to load statistics.'
    });
  }
});

app.get('/timeline', async (req, res) => {
  try {
    const response = await api.get('/api/timeline');
    res.render('timeline', {
      title: 'Naval Timeline',
      events: response.data
    });
  } catch (err) {
    console.error('Timeline API error:', err.message);
    res.render('timeline', {
      title: 'Naval Timeline',
      events: [],
      error: 'Unable to load timeline.'
    });
  }
});

app.get('/gallery', async (req, res) => {
  try {
    const response = await api.get('/api/ships', { params: { page: 1, pageSize: 50 } });
    const data = response.data || {};
    const items = data.items || [];
    const ships = items.map(s => ({
      ...s,
      imageUrl: s.imageUrl ?? s.ImageUrl
    }));
    res.render('gallery', {
      title: 'Photo Gallery',
      ships
    });
  } catch (err) {
    console.error('Gallery API error:', err.message);
    res.render('gallery', {
      title: 'Photo Gallery',
      ships: []
    });
  }
});

// Placeholder when ship image unavailable (SVG - scales nicely)
const PLACEHOLDER_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300" viewBox="0 0 400 300"><rect fill="#6c757d" width="400" height="300"/><text fill="#fff" x="200" y="150" dominant-baseline="middle" text-anchor="middle" font-size="16" font-family="sans-serif">No image</text></svg>';

// Fetch external image with retries and browser-like headers (improves proxy reliability)
const IMAGE_PROXY_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
async function fetchExternalImage(url, maxRetries = 3) {
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      const r = await axios.get(url, {
        responseType: 'arraybuffer',
        timeout: 15000,
        maxContentLength: 10 * 1024 * 1024,
        headers: {
          'User-Agent': IMAGE_PROXY_UA,
          'Accept': 'image/webp,image/apng,image/*,*/*;q=0.8',
          'Accept-Language': 'en-US,en;q=0.9'
        },
        validateStatus: (s) => s === 200
      });
      if (r?.data && r.data.byteLength > 50) return { data: r.data, contentType: r.headers['content-type'] || 'image/jpeg' };
    } catch (err) {
      if (attempt === maxRetries) throw err;
      await new Promise((resolve) => setTimeout(resolve, 300 * attempt));
    }
  }
  return null;
}

app.get('/gallery/image/:id', async (req, res) => {
  try {
    const id = parseInt(req.params.id, 10);
    if (isNaN(id)) return res.status(400).send('Invalid id');

    const imgRes = await api.get(`/api/images/${id}`, { responseType: 'arraybuffer', validateStatus: (s) => s === 200 }).catch(() => null);
    if (imgRes?.data && imgRes.data.byteLength > 50) {
      const ct = imgRes.headers['content-type'] || 'image/jpeg';
      res.set('Content-Type', ct);
      res.set('Cache-Control', 'public, max-age=31536000, immutable');
      return res.send(Buffer.from(imgRes.data));
    }

    const shipRes = await api.get(`/api/ships/${id}`).catch(() => null);
    const imageUrl = shipRes?.data?.imageUrl ?? shipRes?.data?.ImageUrl;
    if (imageUrl && typeof imageUrl === 'string' && imageUrl.startsWith('http')) {
      try {
        const proxyRes = await fetchExternalImage(imageUrl);
        if (proxyRes?.data && proxyRes.data.byteLength > 50) {
          const ct = proxyRes.contentType || 'image/jpeg';
          res.set('Content-Type', ct);
          res.set('Cache-Control', 'public, max-age=31536000, immutable');
          return res.send(Buffer.from(proxyRes.data));
        }
      } catch (proxyErr) {
        console.error('Image proxy failed:', proxyErr.message);
      }
      // Proxy failed: if Accept prefers image (e.g. img src), return SVG; else HTML page with img
      const prefersImage = /image\//.test(req.get('Accept') || '');
      if (prefersImage) {
        res.set('Content-Type', 'image/svg+xml');
        res.set('Cache-Control', 'public, max-age=86400');
        return res.send(PLACEHOLDER_SVG);
      }
      res.set('Content-Type', 'text/html; charset=utf-8');
      const safeUrl = imageUrl.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
      const placeholderDataUri = 'data:image/svg+xml,' + encodeURIComponent(PLACEHOLDER_SVG);
      return res.send(`<!DOCTYPE html><html><head><title>Ship Image</title><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#111"><img src="${safeUrl}" alt="Ship" style="max-width:100%;height:auto;display:block" onerror="this.onerror=null;this.src='${placeholderDataUri}'" /></body></html>`);
    }

    res.set('Content-Type', 'image/svg+xml');
    res.set('Cache-Control', 'public, max-age=86400');
    res.send(PLACEHOLDER_SVG);
  } catch (err) {
    console.error('Gallery image error:', err.message);
    res.set('Content-Type', 'image/svg+xml');
    res.set('Cache-Control', 'public, max-age=86400');
    res.send(PLACEHOLDER_SVG);
  }
});

// Captain images: prefer DB (ImageData), else proxy imageUrl
app.get('/gallery/captain/:id', async (req, res) => {
  try {
    const id = parseInt(req.params.id, 10);
    if (isNaN(id)) return res.status(400).send('Invalid id');

    const imgRes = await api.get(`/api/images/captain/${id}`, { responseType: 'arraybuffer', validateStatus: (s) => s === 200 }).catch(() => null);
    if (imgRes?.data && imgRes.data.byteLength > 50) {
      const ct = imgRes.headers['content-type'] || 'image/jpeg';
      res.set('Content-Type', ct);
      res.set('Cache-Control', 'public, max-age=31536000, immutable');
      return res.send(Buffer.from(imgRes.data));
    }

    const capRes = await api.get(`/api/captains/${id}`).catch(() => null);
    const imageUrl = capRes?.data?.imageUrl ?? capRes?.data?.ImageUrl;
    if (imageUrl && typeof imageUrl === 'string' && imageUrl.startsWith('http')) {
      try {
        const proxyRes = await fetchExternalImage(imageUrl);
        if (proxyRes?.data && proxyRes.data.byteLength > 50) {
          const ct = proxyRes.contentType || 'image/jpeg';
          res.set('Content-Type', ct);
          res.set('Cache-Control', 'public, max-age=31536000, immutable');
          return res.send(Buffer.from(proxyRes.data));
        }
      } catch (proxyErr) {
        console.error('Captain image proxy failed:', proxyErr.message);
      }
    }

    res.set('Content-Type', 'image/svg+xml');
    res.set('Cache-Control', 'public, max-age=86400');
    res.send(PLACEHOLDER_SVG);
  } catch (err) {
    console.error('Captain image error:', err.message);
    res.set('Content-Type', 'image/svg+xml');
    res.set('Cache-Control', 'public, max-age=86400');
    res.send(PLACEHOLDER_SVG);
  }
});

// Debug: verify Web can reach API for images (for troubleshooting Fleet Roster placeholders)
app.get('/admin/images/debug', async (req, res) => {
  try {
    const audit = (await api.get('/api/images/audit').catch(() => null))?.data;
    const imgRes = await api.get('/api/images/1', { responseType: 'arraybuffer', validateStatus: (s) => s === 200 }).catch(() => null);
    res.json({
      apiBase: API_BASE,
      apiReachable: !!audit,
      audit: audit ? { ships: audit.ships, captains: audit.captains } : null,
      firstImageSize: imgRes?.data?.byteLength ?? null,
      firstImageOk: !!(imgRes?.data && imgRes.data.byteLength > 1000)
    });
  } catch (err) {
    res.status(500).json({ error: err.message, apiBase: API_BASE });
  }
});

app.post('/admin/images/test-keys', async (req, res) => {
  try {
    const body = {};
    if (req.body?.pexelsApiKey) body.pexelsApiKey = req.body.pexelsApiKey;
    if (req.body?.pixabayApiKey) body.pixabayApiKey = req.body.pixabayApiKey;
    if (req.body?.unsplashAccessKey) body.unsplashAccessKey = req.body.unsplashAccessKey;
    if (req.body?.googleApiKey) body.googleApiKey = req.body.googleApiKey;
    if (req.body?.googleCseId) body.googleCseId = req.body.googleCseId;
    if (req.body?.customKeys && typeof req.body.customKeys === 'object') body.customKeys = req.body.customKeys;
    const response = await api.post('/api/images/test-keys', body);
    res.json(response.data);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Normalize class name: max 2 words before "-class"
function normalizeClassToTwoWords(cn) {
  if (!cn || typeof cn !== 'string') return '';
  let s = cn.trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class');
  s = s.replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
  const suffix = s.match(/(?:-class| class)$/i);
  const base = (suffix ? s.slice(0, -suffix[0].length) : s).trim();
  const words = base.split(/\s+/).filter(Boolean);
  const capped = words.slice(0, 2).join(' ');
  return capped ? capped + '-class' : '';
}

// String similarity 0–1 (Levenshtein-based)
function stringSimilarity(a, b) {
  if (!a || !b) return 0;
  const sa = String(a).toLowerCase();
  const sb = String(b).toLowerCase();
  if (sa === sb) return 1;
  const len = Math.max(sa.length, sb.length);
  if (len === 0) return 1;
  const d = [];
  for (let i = 0; i <= sa.length; i++) d[i] = [i];
  for (let j = 0; j <= sb.length; j++) d[0][j] = j;
  for (let i = 1; i <= sa.length; i++) {
    for (let j = 1; j <= sb.length; j++) {
      d[i][j] = Math.min(
        d[i - 1][j] + 1,
        d[i][j - 1] + 1,
        d[i - 1][j - 1] + (sa[i - 1] === sb[j - 1] ? 0 : 1)
      );
    }
  }
  return 1 - d[sa.length][sb.length] / len;
}

// Merge similar class names (≥90%), normalize to max 2 words, merged groups first
function mergeAndNormalizeClassNames(names) {
  const filtered = names.map(n => normalizeClassToTwoWords(n)).filter(Boolean);
  const seen = new Set();
  const merged = [];
  const SIM_THRESH = 0.9;
  for (const n of filtered) {
    if (seen.has(n)) continue;
    const group = [n];
    for (const o of filtered) {
      if (o !== n && !seen.has(o) && stringSimilarity(n, o) >= SIM_THRESH) group.push(o);
    }
    group.forEach(c => seen.add(c));
    const canonical = group.length > 1 ? normalizeClassToTwoWords(group[0]) : n;
    if (canonical) merged.push({ name: canonical, groupSize: group.length });
  }
  merged.sort((a, b) => b.groupSize - a.groupSize);
  return merged.map(m => m.name);
}

// Extract suggested data from HTML (server-side, for immediate dropdown population)
function extractSuggestedFromHtml(html, url) {
  const stripTags = (s) => (s || '').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  const isUboat = (url || '').includes('uboat.net') || /warship_header|table_subtle|Commands listed for/i.test(html);

  // uboat.net-specific extraction (warship pages)
  if (isUboat) {
    const uboat = {};
    const h1Match = html.match(/<h1[^>]*class="[^"]*warship_header[^"]*"[^>]*>([^<]+)<\/h1>/i);
    if (h1Match) uboat.name = stripTags(h1Match[1]);
    const h3ClassMatch = html.match(/<h3[^>]*>([^<]*(?:of the|of)\s+([^<\s]+)\s+class[^<]*)<\/h3>/i);
    if (h3ClassMatch) uboat.className = (h3ClassMatch[2] || '').trim();
    if (!uboat.className) {
      const classRowMatch = html.match(/<strong>Class<\/strong><\/td>\s*<td[^>]*>\s*<a[^>]*href="[^"]*\/class\/[^"]*"[^>]*>([^<]+)<\/a>/i);
      if (classRowMatch) uboat.className = stripTags(classRowMatch[1]);
    }
    const historyMatch = html.match(/<strong[^>]*>History<\/strong>[\s\S]*?<\/td>\s*<td[^>]*>([\s\S]*?)<\/td>/i);
    if (historyMatch) {
      const firstP = historyMatch[1].match(/<p[^>]*>([\s\S]*?)<\/p>/);
      if (firstP) uboat.description = stripTags(firstP[1]).slice(0, 2000);
    }
    const commMatch = html.match(/<strong[^>]*>Commissioned<\/strong>[\s\S]*?<\/td>\s*<td[^>]*>([^<]*(?:\d{1,2}\s+\w+\s+)?(\d{4})[^<]*)<\/td>/i)
      || html.match(/<strong[^>]*>Launched<\/strong>[\s\S]*?<\/td>\s*<td[^>]*>([^<]*(?:\d{1,2}\s+\w+\s+)?(\d{4})[^<]*)<\/td>/i);
    if (commMatch) uboat.year = (commMatch[2] || stripTags(commMatch[1]).match(/\d{4}/)?.[0] || '').trim();
    const captainRe = /<a[^>]*href="[^"]*(?:\/allies\/commanders\/|\/boats\/commanders\/)[^"]*"[^>]*>([\s\S]*?)<\/a>/gi;
    const captains = [];
    let capM;
    while ((capM = captainRe.exec(html)) !== null) {
      const text = stripTags(capM[1]);
      if (text && text.length > 2 && text.length < 50 && !captains.includes(text) && !/^Allied Commanders$/i.test(text)) captains.push(text);
    }
    if (captains.length) uboat.captainNames = captains;
    const imgMatch = html.match(/<img[^>]+src="(\/media\/(?:allies|boats)\/[\s\S]*?\.(?:jpg|jpeg|png|gif|webp))"/i);
    if (imgMatch) {
      try {
        const base = new URL(url || 'https://uboat.net/').origin;
        uboat.imageUrl = (imgMatch[1].startsWith('http') ? imgMatch[1] : base + imgMatch[1]);
      } catch (_) { uboat.imageUrl = imgMatch[1]; }
    }
    if (uboat.className || uboat.captainNames?.length || uboat.year) {
      return {
        name: uboat.name,
        imageUrl: uboat.imageUrl,
        description: uboat.description,
        classNames: uboat.className ? mergeAndNormalizeClassNames([uboat.className]) : [],
        captainNames: uboat.captainNames || [],
        yearCommissioned: uboat.year || ''
      };
    }
  }

  const bodyMatch = html.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
  const body = bodyMatch ? bodyMatch[1] : html;
  const pageText = stripTags(body);
  const infoboxMatch = html.match(/<table[^>]*class="[^"]*infobox[^"]*"[^>]*>([\s\S]*?)<\/table>/i);
  let year = '';
  if (infoboxMatch) {
    const ib = stripTags(infoboxMatch[1]);
    const m = ib.match(/Commissioned[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/Launched[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/Completed[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/\b(1[89][0-9]{2}|20[0-2][0-9])\b/);
    if (m) year = m[2] || m[1] || '';
  }
  const classNames = [];
  const infoboxClassRe = /Class\s*(?:&\s*type)?\s*[:\|]\s*\[?\[?([^\]\n]*(?:-class| class)[^\]\n]*)[\]\]]?/i;
  if (infoboxMatch) {
    const ib = stripTags(infoboxMatch[1]);
    const ibClass = ib.match(infoboxClassRe);
    if (ibClass) {
      let cn = (ibClass[1] || '').trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class').replace(/^(?:The|the)\s+/i, '');
      cn = cn.replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
      if (cn && cn.length > 3 && cn.length < 50 && !/^v\s*t\s*e\s|^Class\s+type\s|^type\s+|^Badge\s|^General\s+characteristics/i.test(cn)) classNames.push(cn);
    }
  }
  const classRe = /\b([A-Za-z][A-Za-z\s\-]*(?:-class| class))\b|(?:^|\n)\s*Class\s*(?:&\s*type)?\s*[:\|]\s*\[?\[?([^\]\n]*(?:-class| class))[\]\]]?/gim;
  let m;
  while ((m = classRe.exec(pageText)) !== null) {
    let cn = (m[1] || m[2] || '').trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class').replace(/^(?:The|the)\s+/i, '');
    cn = cn.replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
    if (cn && cn.length > 3 && cn.length < 50 && !/^v\s*t\s*e\s|^Class\s+type\s|^type\s+|^Badge\s|^General\s+characteristics|^similar\s+to|^two\s+old|^specifically\s+the|^ships\s+/i.test(cn) && !classNames.includes(cn)) classNames.push(cn);
  }
  const captainRe = /(?:Captain|Admiral|Commander|Commanded by|Commanding officer|CO|Commanders?)[:\s]+([A-Z][a-zA-Z\.\-']+(?:\s+[A-Z][a-zA-Z\.\-']+)*)(?=,|\.\s+[a-z]|\.\s*$|\s+[a-z]|\s*\n|;|\||$)/g;
  const captainNames = [];
  while ((m = captainRe.exec(pageText)) !== null) {
    const cpn = m[1].trim().replace(/\s+/g, ' ').slice(0, 50);
    if (cpn && cpn.length > 2 && !captainNames.includes(cpn)) captainNames.push(cpn);
  }
  const deduped = captainNames.filter(n => !captainNames.some(o => o !== n && (o.indexOf(n) >= 0 || n.indexOf(o) >= 0) && o.length > n.length));
  return { classNames: mergeAndNormalizeClassNames(classNames), captainNames: deduped, yearCommissioned: year };
}

// Shared: sanitize fetched HTML and inject click handler for Add from URL
function sanitizeAndInjectClickHandler(html, baseUrl) {
  html = html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');
  html = html.replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, '');
  html = html.replace(/<iframe\b[^>]*>[\s\S]*?<\/iframe>/gi, '');
  var baseTag = baseUrl ? '<base href="' + baseUrl.replace(/"/g, '&quot;') + '">' : '';
  var headOpen = html.indexOf('<head>');
  if (headOpen >= 0) html = html.slice(0, headOpen + 6) + baseTag + html.slice(headOpen + 6);
  var clickScript = `
<script>
(function(){
  var target = window.parent !== window ? window.parent : (window.opener || window);
  var hoverEl = null;
  function clearHover(){ if(hoverEl){ hoverEl.style.outline=''; hoverEl.style.outlineOffset=''; hoverEl.style.boxShadow=''; hoverEl=null; } }
  document.addEventListener('mouseover', function(e) {
    clearHover();
    hoverEl = e.target;
    hoverEl.style.outline = '3px solid #22c55e';
    hoverEl.style.outlineOffset = '2px';
    hoverEl.style.boxShadow = '0 0 0 2px rgba(34,197,94,0.5)';
  }, true);
  document.addEventListener('mouseout', function(e) { clearHover(); }, true);
  function preventLinkNav(e) {
    var a = e.target;
    while (a && a !== document) {
      if (a.tagName === 'A' && a.getAttribute('href')) {
        e.preventDefault();
        e.stopPropagation();
        return;
      }
      a = a.parentNode;
    }
  }
  document.addEventListener('click', preventLinkNav, true);
  document.addEventListener('mousedown', preventLinkNav, true);
  function handleSelect(e) {
    e.preventDefault();
    e.stopPropagation();
    clearHover();
    var el = e.target;
    var text = (el.innerText || el.textContent || '').trim().slice(0, 500);
    var img = el.tagName === 'IMG' ? (el.src || el.currentSrc) : '';
    if (!img && el.querySelector && el.querySelector('img')) img = (el.querySelector('img').src || el.querySelector('img').currentSrc || '');
    if (!img && el.tagName === 'A' && /\\.(jpg|jpeg|png|gif|webp)$/i.test(el.href)) img = el.href;
    if (!img && el.dataset && el.dataset.src) img = el.dataset.src;
    try { if (img && img.startsWith('//')) img = location.protocol + img; } catch(_){}
    target.postMessage({ type: 'elementSelected', text: text, imageUrl: img || '', tagName: el.tagName, html: (el.outerHTML || '').slice(0, 300) }, '*');
  }
  document.addEventListener('click', handleSelect, true);
  document.addEventListener('mousedown', handleSelect, true);
  function sendSuggested() {
    var name = '', img = '', desc = '', year = '', classNames = [], captainNames = [];
    var isUboat = document.querySelector('h1.warship_header') || document.querySelector('table.table_subtle');
    if (isUboat) {
      var h1 = document.querySelector('h1.warship_header') || document.querySelector('h1');
      if (h1 && h1.innerText) name = h1.innerText.trim();
      var h3 = document.querySelector('h3');
      if (h3 && h3.innerText) {
        var m = h3.innerText.match(/(?:of the|of)\s+(\S+)\s+class/i);
        if (m && m[1]) classNames.push(m[1].trim());
      }
      if (!classNames.length) {
        var rows = document.querySelectorAll('table.table_subtle tr');
        for (var i = 0; i < rows.length; i++) {
          var tds = rows[i].querySelectorAll('td');
          if (tds.length >= 2 && (tds[0].innerText || '').trim() === 'Class') {
            var a = tds[1].querySelector('a');
            if (a && a.innerText) { classNames.push(a.innerText.trim()); break; }
          }
        }
      }
      for (var r = 0; r < (rows = document.querySelectorAll('table.table_subtle tr')).length; r++) {
        var cells = rows[r].querySelectorAll('td');
        if (cells.length >= 2) {
          var label = (cells[0].innerText || '').trim();
          if (label === 'History') {
            var p = cells[1].querySelector('p');
            if (p && p.innerText) desc = p.innerText.trim().slice(0, 2000);
          } else if (label === 'Commissioned' || label === 'Launched') {
            var ym = (cells[1].innerText || '').match(/(\d{4})/);
            if (ym) year = ym[1];
          }
        }
      }
      var cmdTables = document.querySelectorAll('table.table_subtle');
      for (var t = 0; t < cmdTables.length; t++) {
        var prev = cmdTables[t].previousElementSibling;
        if (prev && (prev.innerText || '').indexOf('Commands listed for') >= 0) {
          var links = cmdTables[t].querySelectorAll('a[href*="/commanders/"]');
          for (var l = 0; l < links.length; l++) {
            var txt = (links[l].innerText || '').trim();
            if (txt && txt.length > 2 && txt.length < 50 && captainNames.indexOf(txt) < 0) captainNames.push(txt);
          }
          break;
        }
      }
      var capImg = document.querySelector('p.caption img') || document.querySelector('div[align="center"] img');
      if (capImg && capImg.src) img = capImg.src;
      try { if (img && img.startsWith('//')) img = location.protocol + img; } catch(_){}
    }
    if (!isUboat) {
      var h1 = document.querySelector('h1');
      if (h1 && h1.innerText) name = h1.innerText.trim();
      if (!name) name = (document.title || '').replace(/ - Wikipedia$/, '').trim();
      var og = document.querySelector('meta[property="og:image"]');
      if (og && og.content) img = og.content;
      if (!img) { var firstImg = document.querySelector('figure img, .mw-parser-output img, main img, [role="main"] img'); if (firstImg && firstImg.src) img = firstImg.src; }
      try { if (img && img.startsWith('//')) img = location.protocol + img; } catch(_){}
      var p = document.querySelector('.mw-parser-output p, main p, [role="main"] p, #content p');
      if (p && p.innerText) desc = p.innerText.trim().slice(0, 2000);
    } else {
      if (!name) { var h1 = document.querySelector('h1'); if (h1 && h1.innerText) name = h1.innerText.trim(); }
      if (!name) name = (document.title || '').replace(/ - Wikipedia$/, '').trim();
      if (!img) { var og = document.querySelector('meta[property="og:image"]'); if (og && og.content) img = og.content; }
      if (!img) { var firstImg = document.querySelector('figure img, .mw-parser-output img, main img'); if (firstImg && firstImg.src) img = firstImg.src; }
      try { if (img && img.startsWith('//')) img = location.protocol + img; } catch(_){}
      if (!desc) { var p = document.querySelector('.mw-parser-output p, main p, #content p'); if (p && p.innerText) desc = p.innerText.trim().slice(0, 2000); }
    }
    var pageText = (document.body && document.body.innerText) ? document.body.innerText : '';
    if (!isUboat) {
    var infobox = document.querySelector('.infobox, .wikitable');
    if (infobox && infobox.innerText) {
      var ib = infobox.innerText;
      var m = ib.match(/Commissioned[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/Launched[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/Completed[:\s]*(\d{1,2}\s+\w+\s+)?(\d{4})/i) || ib.match(/\b(1[89][0-9]{2}|20[0-2][0-9])\b/);
      if (m) year = m[2] || m[1] || '';
      var ibClassRe = /Class\s*(?:&\s*type)?\s*[:\|]\s*\[?\[?([^\]\n]*(?:-class| class)[^\]\n]*)[\]\]]?/i;
      var ibMatch = ib.match(ibClassRe);
      if (ibMatch) {
        var cn = (ibMatch[1] || '').trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class').replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
        if (cn && cn.length > 3 && cn.length < 50 && !/^Badge\s|^General\s+characteristics/i.test(cn)) classNames.push(cn);
      }
    }
    var classRe = /\b([A-Za-z][A-Za-z\s\-]*(?:-class| class))\b|(?:^|\n)\s*Class\s*(?:&\s*type)?\s*[:\|]\s*\[?\[?([^\]\n]*(?:-class| class))[\]\]]?/gim;
    var classMatch;
    while ((classMatch = classRe.exec(pageText)) !== null) {
      var cn = (classMatch[1] || classMatch[2] || '').trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class').replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
      if (cn && cn.length > 3 && cn.length < 50 && !/^v\s*t\s*e\s|^Class\s+type\s|^type\s+|^Badge\s|^General\s+characteristics|^similar\s+to|^two\s+old|^specifically\s+the|^ships\s+/i.test(cn) && !classNames.includes(cn)) classNames.push(cn);
    }
    var captainRe = /(?:Captain|Admiral|Commander|Commanded by|Commanding officer|CO|Commanders?)[:\s]+([A-Z][a-zA-Z\.\-']+(?:\s+[A-Z][a-zA-Z\.\-']+)*)(?=,|\.\s+[a-z]|\.\s*$|\s+[a-z]|\s*\n|;|\||$)/g;
    var capMatch;
    while ((capMatch = captainRe.exec(pageText)) !== null) {
      var cpn = capMatch[1].trim().replace(/\s+/g, ' ').slice(0, 50);
      if (cpn && cpn.length > 2 && !captainNames.includes(cpn)) captainNames.push(cpn);
    }
    }
    captainNames = captainNames.filter(function(n) {
      return !captainNames.some(function(o) { return o !== n && (o.indexOf(n) >= 0 || n.indexOf(o) >= 0) && o.length > n.length; });
    });
    function norm2w(cn) {
      if (!cn) return '';
      var s = String(cn).trim().replace(/\s+/g, ' ').replace(/\s+-\s*class\s*$/i, '-class').replace(/\s+class\s*$/i, ' class');
      s = s.replace(/\s+(?:battleship|cruiser|destroyer|carrier|submarine|frigate|etc\.?)$/i, '').trim();
      var base = s.replace(/(?:-class| class)$/i, '').trim();
      var words = base.split(/\s+/).filter(Boolean);
      return words.slice(0, 2).join(' ') ? words.slice(0, 2).join(' ') + '-class' : '';
    }
    function sim(a, b) {
      if (!a || !b) return 0;
      var sa = String(a).toLowerCase(), sb = String(b).toLowerCase();
      if (sa === sb) return 1;
      var d = [], i, j;
      for (i = 0; i <= sa.length; i++) d[i] = [i];
      for (j = 0; j <= sb.length; j++) d[0][j] = j;
      for (i = 1; i <= sa.length; i++)
        for (j = 1; j <= sb.length; j++)
          d[i][j] = Math.min(d[i-1][j]+1, d[i][j-1]+1, d[i-1][j-1]+(sa[i-1]===sb[j-1]?0:1));
      return 1 - d[sa.length][sb.length] / Math.max(sa.length, sb.length);
    }
    function mergeClasses(names) {
      var out = [], seen = {};
      for (var i = 0; i < names.length; i++) {
        var n = norm2w(names[i]);
        if (!n || seen[n]) continue;
        var group = [n];
        for (var j = 0; j < names.length; j++) {
          var o = norm2w(names[j]);
          if (o && o !== n && !seen[o] && sim(n, o) >= 0.9) group.push(o);
        }
        for (var k = 0; k < group.length; k++) seen[group[k]] = true;
        out.push({ n: group[0], sz: group.length });
      }
      out.sort(function(a,b){ return b.sz - a.sz; });
      return out.map(function(x){ return x.n; });
    }
    classNames = mergeClasses(classNames);
    target.postMessage({ type: 'suggestedData', name: name, imageUrl: img, description: desc, yearCommissioned: year, classNames: classNames, captainNames: captainNames }, '*');
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', sendSuggested);
  else sendSuggested();
})();
</script>
`;
  var bodyClose = html.indexOf('</body>');
  if (bodyClose >= 0) {
    html = html.slice(0, bodyClose) + clickScript + html.slice(bodyClose);
  } else {
    html += clickScript;
  }
  return html;
}

// Fetch URL with User-Agent fallback: try default first, retry with browser-like UA if non-200
async function fetchUrlWithFallback(url) {
  const userAgents = [
    'Mozilla/5.0 (compatible; NavalArchive/1.0)',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
  ];
  let lastErr = null;
  for (const ua of userAgents) {
    try {
      const r = await axios.get(url, {
        timeout: 15000,
        responseType: 'text',
        headers: { 'User-Agent': ua },
        validateStatus: () => true
      });
      if (r.status === 200) return r;
      lastErr = new Error('Failed to fetch URL: ' + r.status);
    } catch (err) {
      lastErr = err;
    }
  }
  throw lastErr;
}

// Add from URL: fetch and proxy page for interactive element selection
app.post('/admin/add-from-url/fetch', async (req, res) => {
  try {
    const url = (req.body?.url || req.body)?.trim();
    if (!url || !/^https?:\/\//i.test(url)) {
      return res.status(400).json({ error: 'Valid URL required' });
    }
    const r = await fetchUrlWithFallback(url);
    let html = typeof r.data === 'string' ? r.data : '';
    var baseUrl = '';
    try { baseUrl = new URL(url).origin + '/'; } catch (_) {}
    const suggested = extractSuggestedFromHtml(html, url);
    html = sanitizeAndInjectClickHandler(html, baseUrl);
    res.json({ html, url, suggested });
  } catch (err) {
    res.status(502).json({ error: err.message || 'Fetch failed' });
  }
});

// Proxy route: load URL in iframe (same-origin) so injected script runs reliably
app.get('/admin/add-from-url/proxy', async (req, res) => {
  try {
    const url = (req.query?.url || '').trim();
    if (!url || !/^https?:\/\//i.test(url)) {
      return res.status(400).send('Valid URL required');
    }
    const r = await fetchUrlWithFallback(url);
    let html = typeof r.data === 'string' ? r.data : '';
    var baseUrl = '';
    try { baseUrl = new URL(url).origin + '/'; } catch (_) {}
    html = sanitizeAndInjectClickHandler(html, baseUrl);
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    res.setHeader('X-Frame-Options', 'SAMEORIGIN');
    res.send(html);
  } catch (err) {
    res.status(502).send('Fetch failed: ' + (err.message || 'Unknown error'));
  }
});

app.get('/admin/add-from-url', (req, res) => {
  res.render('add-from-url', { title: 'Add from URL' });
});

app.get('/admin/images', async (req, res) => {
  try {
    const [auditRes, shipsRes, captainsRes, sourcesRes] = await Promise.all([
      api.get('/api/images/audit'),
      api.get('/api/ships', { params: { page: 1, pageSize: 500 } }).catch(() => ({ data: {} })),
      api.get('/api/captains').catch(() => ({ data: [] })),
      api.get('/api/image-sources').catch(() => ({ data: [] }))
    ]);
    const shipsData = shipsRes.data || {};
    res.render('admin-images', {
      title: 'Image Audit',
      audit: auditRes.data,
      ships: shipsData.items || [],
      captains: captainsRes.data || [],
      imageSources: Array.isArray(sourcesRes.data) ? sourcesRes.data : []
    });
  } catch (err) {
    console.error('Image audit error:', err.message);
    res.render('admin-images', { title: 'Image Audit', audit: null, ships: [], captains: [], imageSources: [], error: err.message });
  }
});

app.get('/admin/images/search-frame', async (req, res) => {
  let imageSources = [];
  try {
    const r = await api.get('/api/image-sources');
    imageSources = Array.isArray(r.data) ? r.data : [];
  } catch (e) { /* use empty, fallback to All */ }
  res.render('admin-images-search-frame', {
    title: 'Search Image',
    entity: req.query.entity || 'ship',
    entityId: req.query.id || '',
    query: req.query.q || '',
    imageSources
  });
});

// Image sources: proxy to API (sources are entity, stored in DB)
app.get('/admin/images/sources', async (req, res) => {
  try {
    const r = await api.get('/api/image-sources');
    res.json(r.data || []);
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

app.put('/admin/images/sources', async (req, res) => {
  try {
    const sources = Array.isArray(req.body) ? req.body : (req.body?.sources || []);
    const r = await api.put('/api/image-sources', sources);
    res.json(r.data || []);
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

app.delete('/admin/images/sources/:id', async (req, res) => {
  try {
    await api.delete('/api/image-sources/' + encodeURIComponent(req.params.id));
    res.status(204).send();
  } catch (e) {
    res.status(e.response?.status || 500).json({ error: e.message });
  }
});

// In-memory job store for polling-based populate progress
const populateJobs = new Map();
const JOB_EXPIRE_MS = 30 * 60 * 1000; // 30 min

function cleanupOldJobs() {
  const now = Date.now();
  for (const [id, job] of populateJobs) {
    if (job.done && (now - (job.updatedAt || 0)) > JOB_EXPIRE_MS) populateJobs.delete(id);
  }
}
setInterval(cleanupOldJobs, 60000);

app.post('/admin/images/populate', async (req, res) => {
  try {
    const runSyncFirst = req.body?.runSyncFirst === true;
    const usePolling = req.body?.usePolling === true;
    const keys = {};
    if (req.body?.pexelsApiKey) keys.pexelsApiKey = req.body.pexelsApiKey;
    if (req.body?.pixabayApiKey) keys.pixabayApiKey = req.body.pixabayApiKey;
    if (req.body?.unsplashAccessKey) keys.unsplashAccessKey = req.body.unsplashAccessKey;
    if (req.body?.googleApiKey) keys.googleApiKey = req.body.googleApiKey;
    if (req.body?.googleCseId) keys.googleCseId = req.body.googleCseId;
    if (req.body?.shipSearchPrefix) keys.shipSearchPrefix = req.body.shipSearchPrefix;
    if (req.body?.captainSearchPrefix) keys.captainSearchPrefix = req.body.captainSearchPrefix;
    if (Array.isArray(req.body?.imageSources) && req.body.imageSources.length > 0) keys.imageSources = req.body.imageSources;
    if (req.body?.customKeys && typeof req.body.customKeys === 'object') keys.customKeys = req.body.customKeys;

    if (usePolling) {
      const jobId = 'populate-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
      populateJobs.set(jobId, { events: [], done: false, updatedAt: Date.now() });
      res.json({ jobId });

      (async () => {
        const job = populateJobs.get(jobId);
        if (!job) return;
        const add = (evt) => { job.events.push(evt); job.updatedAt = Date.now(); };
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
        const maxRetries = 3;
        const retryDelayMs = 10000;

        try {
          if (runSyncFirst) {
            for (let attempt = 1; attempt <= maxRetries; attempt++) {
              try {
                const syncRes = await api.post('/api/admin/sync?force=true', {}, { timeout: 120000 });
                add({ type: 'sync', message: syncRes.data?.message || 'Completed' });
                break;
              } catch (syncErr) {
                const msg = syncErr.response?.data?.message || syncErr.message;
                const is429 = syncErr.response?.status === 429;
                add({ type: 'sync', error: msg });
                if (attempt < maxRetries) {
                  add({ type: 'info', data: `Retry ${attempt}/${maxRetries - 1} in ${retryDelayMs / 1000}s${is429 ? ' (429 rate limit)' : ''}...` });
                  await sleep(retryDelayMs);
                } else {
                  add({ type: 'info', data: 'Continuing with cached data (sync failed).' });
                  break;
                }
              }
            }
          }

          for (let attempt = 1; attempt <= maxRetries; attempt++) {
            try {
              const streamRes = await axios({
                method: 'post',
                url: `${API_BASE}/api/images/populate/stream`,
                data: { ...keys },
                responseType: 'stream',
                timeout: 300000,
                headers: { 'Content-Type': 'application/json' }
              });
              let buf = '';
              await new Promise((resolve, reject) => {
                streamRes.data.on('data', (chunk) => {
                  buf += chunk.toString();
                  const parts = buf.split(/\r?\n\r?\n+/);
                  buf = parts.pop() || '';
                  for (const p of parts) {
                    const m = p.match(/^data:\s*(.+)/s);
                    if (m) {
                      try {
                        const evt = JSON.parse(m[1].trim());
                        if (evt && (evt.type || evt.Type)) add(evt);
                      } catch (e) { /* skip malformed */ }
                    }
                  }
                });
                streamRes.data.on('end', () => {
                  if (buf.trim()) {
                    const m = buf.match(/^data:\s*(.+)/s);
                    if (m) try { const evt = JSON.parse(m[1].trim()); if (evt && (evt.type || evt.Type)) add(evt); } catch (e) {}
                  }
                  resolve();
                });
                streamRes.data.on('error', reject);
              });
              break;
            } catch (streamErr) {
              const msg = streamErr.response?.data?.message || streamErr.message;
              const is429 = streamErr.response?.status === 429;
              add({ type: 'error', error: msg });
              if (attempt < maxRetries) {
                add({ type: 'info', data: `Retry ${attempt}/${maxRetries - 1} in ${retryDelayMs / 1000}s${is429 ? ' (429 rate limit)' : ''}...` });
                await sleep(retryDelayMs);
              } else {
                break;
              }
            }
          }
          job.done = true;
          job.updatedAt = Date.now();
        } catch (err) {
          add({ type: 'error', error: err.message });
          job.done = true;
          job.updatedAt = Date.now();
        }
      })();
      return;
    }

    const logLines = [];
    if (runSyncFirst) {
      try {
        const syncRes = await api.post('/api/admin/sync?force=true', {}, { timeout: 120000 });
        logLines.push('[Sync] ' + (syncRes.data?.message || 'Completed'));
      } catch (syncErr) {
        logLines.push('[Sync] Error: ' + (syncErr.response?.data?.message || syncErr.message));
      }
    }
    const response = await api.post('/api/images/populate', keys, { timeout: 300000 });
    const data = response.data || {};
    data.logLines = logLines;
    res.json(data);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.get('/admin/images/populate/status/:jobId', (req, res) => {
  const job = populateJobs.get(req.params.jobId);
  if (!job) return res.status(404).json({ error: 'Job not found' });
  res.json({ events: job.events, done: job.done });
});

// Trigger Java ImagePopulator (Wikipedia): API entity calls Java entity at localhost:5099/run
app.post('/admin/images/populate/wikipedia', async (req, res) => {
  try {
    const response = await api.post('/api/images/populate/wikipedia', {}, { timeout: 20000 });
    res.status(response.status).json(response.data || { message: 'Wikipedia populate started.' });
  } catch (err) {
    const status = err.response?.status ?? 503;
    const data = err.response?.data ?? { message: err.message || 'ImagePopulator unreachable.' };
    res.status(status).json(data);
  }
});

// Test terminal: emits fake progress events over ~12s so you can verify the terminal updates in the browser
app.post('/admin/images/populate/test-terminal', (req, res) => {
  const jobId = 'test-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
  const job = { events: [], done: false, updatedAt: Date.now() };
  populateJobs.set(jobId, job);
  res.json({ jobId });

  const add = (evt) => { job.events.push(evt); job.updatedAt = Date.now(); };
  const events = [
    { type: 'sync', message: 'Test sync completed (fake)' },
    { type: 'info', data: 'Processing 3 ships, 2 captains without cached images.' },
    { type: 'progress', data: '[Bismarck] Trying Unsplash...' },
    { type: 'progress', data: 'Unsplash: 5 results' },
    { type: 'ship', data: { id: 1, name: 'Bismarck', status: 'ok', reason: null, index: 1, total: 3, bytesStored: 45231 } },
    { type: 'progress', data: '[Tirpitz] Trying Unsplash...' },
    { type: 'progress', data: 'Unsplash: 4 results' },
    { type: 'ship', data: { id: 2, name: 'Tirpitz', status: 'ok', reason: null, index: 2, total: 3, bytesStored: 38921 } },
    { type: 'ship', data: { id: 3, name: 'Yamato', status: 'fail', reason: 'No ImageUrl', index: 3, total: 3, bytesStored: null } },
    { type: 'captain', data: { id: 1, name: 'Ernst Lindemann', status: 'ok', reason: null, index: 1, total: 2, bytesStored: 12400 } },
    { type: 'captain', data: { id: 2, name: 'Karl Topp', status: 'fail', reason: 'Duplicate image', index: 2, total: 2, bytesStored: null } },
    { type: 'done', data: null }
  ];
  let i = 0;
  const tick = () => {
    if (i < events.length) {
      add(events[i++]);
      setTimeout(tick, 1200);
    } else {
      job.done = true;
      job.updatedAt = Date.now();
    }
  };
  setTimeout(tick, 500);
});

// Verify which ship images actually load (no placeholder)
app.get('/admin/images/verify', async (req, res) => {
  try {
    const shipsRes = await api.get('/api/ships/choices', { params: { limit: 5000 } });
    const ships = Array.isArray(shipsRes.data) ? shipsRes.data : [];
    const baseUrl = `http://localhost:${PORT}`;
    const missing = [];
    for (let i = 0; i < ships.length; i++) {
      const ship = ships[i];
      const index = i + 1;
      try {
        const imgRes = await axios.get(`${baseUrl}/gallery/image/${ship.id}`, {
          responseType: 'arraybuffer',
          timeout: 8000,
          headers: { Accept: 'image/*' },
          validateStatus: () => true
        });
        const ct = (imgRes.headers['content-type'] || '').toLowerCase();
        const len = imgRes.data?.byteLength ?? 0;
        const status = imgRes.status;
        // Placeholder = SVG or tiny/empty response
        if (ct.includes('svg')) {
          missing.push({ id: ship.id, name: ship.name, reason: 'SVG placeholder (no real image)' });
        } else if (len < 200) {
          missing.push({ id: ship.id, name: ship.name, reason: `Response too small (${len} bytes)` });
        } else if (status !== 200) {
          missing.push({ id: ship.id, name: ship.name, reason: `HTTP ${status}` });
        }
      } catch (err) {
        const reason = err.code === 'ECONNABORTED' ? 'Timeout' : (err.response ? `HTTP ${err.response.status}` : err.message || 'Request failed');
        missing.push({ id: ship.id, name: ship.name, reason });
      }
    }
    res.json({
      total: ships.length,
      withImages: ships.length - missing.length,
      missing,
      missingCount: missing.length
    });
  } catch (err) {
    console.error('Image verify error:', err.message);
    res.status(500).json({ error: err.message, missing: [], total: 0, withImages: 0, missingCount: 0 });
  }
});

app.get('/logs', (req, res) => {
  res.render('logs', {
    title: 'Daily Logs',
    results: null,
    query: req.query.q || ''
  });
});

app.get('/logs/search.json', async (req, res) => {
  try {
    const query = req.query.q || '';
    const response = await api.post('/api/logs/search', { query });
    res.json(response.data);
  } catch (err) {
    res.status(500).json({ matches: 0, message: 'Search failed', excerpts: [] });
  }
});

app.get('/logs/day.json', async (req, res) => {
  try {
    const shipName = req.query.shipName || '';
    const logDate = req.query.logDate || '';
    const response = await api.get('/api/logs/day', { params: { shipName, logDate } });
    res.json(response.data);
  } catch (err) {
    if (err.response?.status === 404) {
      return res.status(404).json({ error: 'No log entries found' });
    }
    res.status(500).json({ error: err.message || 'Failed to load day log' });
  }
});

app.post('/logs/search', async (req, res) => {
  try {
    const query = req.body.query || '';
    const response = await api.post('/api/logs/search', { query });
    res.render('logs', {
      title: 'Daily Logs',
      results: response.data,
      query
    });
  } catch (err) {
    console.error('Logs search error:', err.message);
    res.render('logs', {
      title: 'Daily Logs',
      error: 'Search failed or timed out. Try a simpler query.',
      query: req.body.query || ''
    });
  }
});

app.get('/donate', (req, res) => {
  res.render('donate', { title: 'Donate' });
});

app.get('/payment-account', (req, res) => {
  res.render('payment-account', { title: 'Payment Account' });
});

app.get('/login', (req, res) => {
  res.render('login', { title: 'Login' });
});

app.get('/membership', (req, res) => {
  res.render('membership', { title: 'Membership' });
});

app.get('/members', (req, res) => {
  res.render('members', { title: 'Add Member' });
});

app.get('/cart', (req, res) => {
  const cardId = req.query.cardId || '';
  res.redirect('/members' + (cardId ? '?cardId=' + encodeURIComponent(cardId) : ''));
});

app.get('/checkout', (req, res) => {
  res.render('checkout', { title: 'Checkout' });
});

app.get('/verify-member', (req, res) => {
  res.redirect('/members');
});

app.get('/simulation', (req, res) => {
  res.render('simulation', {
    title: 'Live Battle'
  });
});

app.post('/admin/sync', async (req, res) => {
  try {
    const force = req.query.force === 'true' || req.body?.force === true;
    const response = await api.post(`/api/admin/sync${force ? '?force=true' : ''}`);
    res.json(response.data);
  } catch (err) {
    console.error('Sync error:', err.message);
    res.status(err.response?.status || 500).json({
      error: err.response?.data?.error || 'Sync failed'
    });
  }
});

app.post('/simulation/join', async (req, res) => {
  try {
    const userName = req.body.userName || 'Visitor';
    const response = await api.post('/api/simulation/join', {
      userName
    });
    res.json(response.data);
  } catch (err) {
    console.error('Simulation join error:', err.response?.status, err.message);
    res.status(err.response?.status || 500).json({
      error: err.response?.data?.title || 'Failed to join. Try again.'
    });
  }
});

// Global error handler
app.use((err, req, res, next) => {
  console.error('Unhandled error:', err);
  res.status(500).send('Internal Server Error');
});

// --- Start Server ---
// BIND_HOST=127.0.0.1 or API_AS_GATEWAY=true: only localhost can reach (API proxies to us). Frontend not directly exposed.
const BIND_HOST = process.env.BIND_HOST || (process.env.API_AS_GATEWAY === 'true' ? '127.0.0.1' : '0.0.0.0');

app.listen(PORT, BIND_HOST, () => {
  console.log(`Naval Archive Web running at http://${BIND_HOST}:${PORT}`);
  console.log(`API proxy target: ${API_BASE}`);
});
