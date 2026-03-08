const express = require('express');
const axios = require('axios');
const path = require('path');
const fs = require('fs');

const app = express();
const PORT = process.env.PORT || 3000;
const API_BASE = process.env.API_URL || 'http://localhost:5000';
// All backend communication goes through API (Gateway, Video, etc.)
const MENU_URL = process.env.MENU_URL || 'https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/NavalArchive.Web/public/menu.json';

// Fallback menu: flat list or grouped { label, items: [{ href, label }] }
const defaultNavItems = [
  { href: '/', label: 'Home' },
  { label: 'Explore', items: [
    { href: '/fleet', label: 'Fleet Roster' },
    { href: '/compare', label: 'Compare' },
    { href: '/classes', label: 'Ship Classes' },
    { href: '/captains', label: 'Captains' },
    { href: '/timeline', label: 'Timeline' },
    { href: '/stats', label: 'Statistics' },
    { href: '/gallery', label: 'Photo Gallery' },
    { href: '/logs', label: 'Daily Logs' }
  ]},
  { href: '/simulation', label: 'Live Battle' },
  { label: 'Support', items: [
    { href: '/donate', label: 'Donate' },
    { href: '/membership', label: 'Membership' }
  ]},
  { label: 'Member', items: [
    { href: '/members', label: 'Add Member' },
    { href: '/verify-member', label: 'Verify Member' }
  ]},
  { label: 'Cart', items: [
    { href: '/cart', label: 'View Cart' },
    { href: '/checkout', label: 'Checkout' }
  ]},
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
  try {
    const r = await axios.get(MENU_URL, { timeout: 5000, validateStatus: (s) => s === 200 });
    const items = Array.isArray(r.data) ? r.data : (r.data?.items || r.data?.menu || []);
    if (items.length > 0 && items.every(isValidNavItem)) {
      cachedNavItems = items;
      cacheExpiry = Date.now() + MENU_CACHE_MS;
      return cachedNavItems;
    }
  } catch (err) {
    console.error('Menu fetch failed:', err.message);
  }
  return defaultNavItems;
}

app.set('view engine', 'ejs');
app.set('views', path.join(__dirname, 'views'));
app.use(express.static(path.join(__dirname, 'public')));
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Health check - no dependencies, before nav middleware
app.get('/health', (req, res) => res.status(200).send('OK'));

// Dynamic nav menu from online (MENU_URL), cached 5 min
app.use(async (req, res, next) => {
  try {
    res.locals.navItems = await fetchNavItems();
  } catch (e) {
    res.locals.navItems = defaultNavItems;
  }
  res.locals.currentPath = req.path;
  res.locals.toEntity = toEntity;
  res.locals.imageSearchPrefix = { ship: 'battleship', captain: 'captain' };
  next();
});

// Axios instance for API calls
const api = axios.create({
  baseURL: API_BASE,
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' }
});

/** Normalize API data to entity format: { id, name, description, subtitle, imageUrl, imageVersion, imageGallery, type, detailUrl } */
function toEntity(item, type = 'ship') {
  if (!item) return null;
  const id = item.id ?? item.Id;
  const base = type === 'captain' ? '/captains' : '/ships';
  const gallery = type === 'captain' ? 'captain' : 'image';
  let subtitle = '';
  if (type === 'ship') subtitle = item.yearCommissioned ? `Commissioned ${item.yearCommissioned}` : '';
  if (type === 'captain') subtitle = [item.rank, item.serviceYears ? `${item.serviceYears} years service` : ''].filter(Boolean).join(' · ');
  return {
    id,
    name: item.name ?? item.Name ?? '',
    description: item.description ?? item.Description ?? '',
    subtitle,
    imageUrl: `/gallery/${gallery}/${id}?v=${item.imageVersion ?? item.ImageVersion ?? 0}`,
    imageVersion: item.imageVersion ?? item.ImageVersion ?? 0,
    imageGallery: gallery,
    type,
    detailUrl: `${base}/${id}`,
    ...item
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

// Image upload: stream raw body to API (bypass JSON proxy)
app.post('/api/images/ship/:id/upload', async (req, res) => {
  try {
    const id = req.params.id;
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = Buffer.concat(chunks);
    const r = await axios({
      method: 'POST',
      url: `${API_BASE}/api/images/ship/${id}/upload`,
      data: body,
      headers: { 'Content-Type': req.headers['content-type'] || 'image/jpeg' },
      maxBodyLength: 10 * 1024 * 1024,
      validateStatus: () => true
    });
    res.status(r.status).json(r.data ?? {});
  } catch (err) {
    console.error('Image upload proxy error:', err.message);
    res.status(502).json({ error: err.message });
  }
});

// Chain: App -> API -> Cart -> Card -> Payment (all requests go through API)
// Proxy /api/* to API for client-side fetches (local dev; on IIS, /api/* is rewritten to API)
app.use('/api', async (req, res) => {
  try {
    const url = `${API_BASE}/api${req.url}`;
    const opts = {
      method: req.method,
      url,
      headers: { 'Content-Type': 'application/json' },
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
    const response = await api.get(`/api/ships/${req.params.id}`);
    res.render('ship', {
      title: response.data.name,
      ship: response.data
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
    res.render('classes', {
      title: 'Ship Classes',
      classes: response.data
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

app.get('/gallery/image/:id', async (req, res) => {
  try {
    const id = parseInt(req.params.id, 10);
    if (isNaN(id)) return res.status(400).send('Invalid id');

    const imgRes = await api.get(`/api/images/${id}`, { responseType: 'arraybuffer', validateStatus: (s) => s === 200 }).catch(() => null);
    if (imgRes?.data && imgRes.data.byteLength > 50) {
      const ct = imgRes.headers['content-type'] || 'image/jpeg';
      res.set('Content-Type', ct);
      res.set('Cache-Control', 'no-cache, must-revalidate');
      return res.send(Buffer.from(imgRes.data));
    }

    const shipRes = await api.get(`/api/ships/${id}`).catch(() => null);
    const imageUrl = shipRes?.data?.imageUrl ?? shipRes?.data?.ImageUrl;
    if (imageUrl && typeof imageUrl === 'string' && imageUrl.startsWith('http')) {
      try {
        const proxyRes = await axios.get(imageUrl, {
          responseType: 'arraybuffer',
          timeout: 10000,
          maxContentLength: 10 * 1024 * 1024,
          headers: { 'User-Agent': 'Mozilla/5.0 (compatible; NavalArchive/1.0)', 'Accept': 'image/*' },
          validateStatus: (s) => s === 200
        });
        if (proxyRes?.data && proxyRes.data.byteLength > 50) {
          const ct = proxyRes.headers['content-type'] || 'image/jpeg';
          res.set('Content-Type', ct);
          res.set('Cache-Control', 'no-cache, must-revalidate');
          return res.send(Buffer.from(proxyRes.data));
        }
      } catch (proxyErr) {
        console.error('Image proxy failed:', proxyErr.message);
      }
      // Proxy failed: if Accept prefers image (e.g. img src), return SVG; else HTML page with img
      const prefersImage = /image\//.test(req.get('Accept') || '');
      if (prefersImage) {
        res.set('Content-Type', 'image/svg+xml');
        return res.send(PLACEHOLDER_SVG);
      }
      res.set('Content-Type', 'text/html; charset=utf-8');
      const safeUrl = imageUrl.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
      const placeholderDataUri = 'data:image/svg+xml,' + encodeURIComponent(PLACEHOLDER_SVG);
      return res.send(`<!DOCTYPE html><html><head><title>Ship Image</title><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#111"><img src="${safeUrl}" alt="Ship" style="max-width:100%;height:auto;display:block" onerror="this.onerror=null;this.src='${placeholderDataUri}'" /></body></html>`);
    }

    res.set('Content-Type', 'image/svg+xml');
    res.send(PLACEHOLDER_SVG);
  } catch (err) {
    console.error('Gallery image error:', err.message);
    res.set('Content-Type', 'image/svg+xml');
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
      res.set('Cache-Control', 'no-cache, must-revalidate');
      return res.send(Buffer.from(imgRes.data));
    }

    const capRes = await api.get(`/api/captains/${id}`).catch(() => null);
    const imageUrl = capRes?.data?.imageUrl ?? capRes?.data?.ImageUrl;
    if (imageUrl && typeof imageUrl === 'string' && imageUrl.startsWith('http')) {
      try {
        const proxyRes = await axios.get(imageUrl, {
          responseType: 'arraybuffer',
          timeout: 10000,
          maxContentLength: 10 * 1024 * 1024,
          headers: { 'User-Agent': 'Mozilla/5.0 (compatible; NavalArchive/1.0)', 'Accept': 'image/*' },
          validateStatus: (s) => s === 200
        });
        if (proxyRes?.data && proxyRes.data.byteLength > 50) {
          const ct = proxyRes.headers['content-type'] || 'image/jpeg';
          res.set('Content-Type', ct);
          res.set('Cache-Control', 'no-cache, must-revalidate');
          return res.send(Buffer.from(proxyRes.data));
        }
      } catch (proxyErr) {
        console.error('Captain image proxy failed:', proxyErr.message);
      }
    }

    res.set('Content-Type', 'image/svg+xml');
    res.send(PLACEHOLDER_SVG);
  } catch (err) {
    console.error('Captain image error:', err.message);
    res.set('Content-Type', 'image/svg+xml');
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
        try {
          if (runSyncFirst) {
            try {
              const syncRes = await api.post('/api/admin/sync?force=true', {}, { timeout: 120000 });
              add({ type: 'sync', message: syncRes.data?.message || 'Completed' });
            } catch (syncErr) {
              add({ type: 'sync', error: syncErr.response?.data?.message || syncErr.message });
            }
          }
          const streamRes = await axios({
            method: 'post',
            url: `${API_BASE}/api/images/populate/stream`,
            data: { ...keys },
            responseType: 'stream',
            timeout: 300000,
            headers: { 'Content-Type': 'application/json' }
          });
          let buf = '';
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
            job.done = true;
            job.updatedAt = Date.now();
          });
          streamRes.data.on('error', (err) => {
            add({ type: 'error', error: err.message });
            job.done = true;
          });
        } catch (err) {
          add({ type: 'error', error: err.message });
          job.done = true;
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

app.get('/membership', (req, res) => {
  res.render('membership', { title: 'Membership' });
});

app.get('/members', (req, res) => {
  res.render('members', { title: 'Add Member' });
});

app.get('/cart', (req, res) => {
  res.render('cart', { title: 'Cart', cardId: req.query.cardId || '' });
});

app.get('/checkout', (req, res) => {
  res.render('checkout', { title: 'Checkout' });
});

app.get('/verify-member', (req, res) => {
  res.render('verify-member', { title: 'Verify Member ID' });
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
