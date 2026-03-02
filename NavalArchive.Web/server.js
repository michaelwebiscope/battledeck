const express = require('express');
const axios = require('axios');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;
const API_BASE = process.env.API_URL || 'http://localhost:5000';
const GATEWAY_URL = process.env.GATEWAY_URL || 'http://localhost:5010';
const VIDEO_SERVICE_URL = process.env.VIDEO_SERVICE_URL || 'http://localhost:5020';
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
  next();
});

// Axios instance for API calls
const api = axios.create({
  baseURL: API_BASE,
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' }
});

// Trace chain: Web -> Gateway (5010) -> Auth -> User -> ... -> Notification
app.get('/api/trace', async (req, res) => {
  try {
    const r = await axios.get(`${GATEWAY_URL}/trace`, { timeout: 15000, validateStatus: () => true });
    res.json(r.data ?? { error: 'No response' });
  } catch (err) {
    console.error('Trace error:', err.message);
    res.status(502).json({ error: 'Trace chain unavailable. Ensure Gateway (port 5010) is running.' });
  }
});

// Video streaming: proxy to Java service (port 5020) with stream response
app.get('/api/videos/:shipId', async (req, res) => {
  try {
    const headers = {};
    if (req.headers.range) headers.Range = req.headers.range;
    const r = await axios({
      method: 'GET',
      url: `${VIDEO_SERVICE_URL}/api/videos/${req.params.shipId}`,
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

app.get('/', (req, res) => {
  res.render('home', {
    title: 'Home',
    featuredShip: {
      name: 'USS Enterprise (CV-6)',
      description: 'The most decorated ship of the Second World War. "The Big E" earned 20 battle stars and participated in nearly every major Pacific engagement.',
      year: 1938,
      imageUrl: 'https://upload.wikimedia.org/wikipedia/commons/thumb/2/2a/USS_Enterprise_%28CV-6%29_in_Puerto_Rico%2C_early_1941.jpg/800px-USS_Enterprise_%28CV-6%29_in_Puerto_Rico%2C_early_1941.jpg'
    }
  });
});

app.get('/fleet', async (req, res) => {
  try {
    const params = {};
    if (req.query.country) params.country = req.query.country;
    if (req.query.type) params.type = req.query.type;
    if (req.query.yearMin) params.yearMin = req.query.yearMin;
    if (req.query.yearMax) params.yearMax = req.query.yearMax;
    const response = await api.get('/api/ships', { params });
    const classesRes = await api.get('/api/classes').catch(() => ({ data: [] }));
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: response.data,
      searchQuery: '',
      classes: classesRes.data,
      filters: { country: req.query.country, type: req.query.type, yearMin: req.query.yearMin, yearMax: req.query.yearMax }
    });
  } catch (err) {
    console.error('Fleet API error:', err.message);
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: [],
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
    const classesRes = await api.get('/api/classes').catch(() => ({ data: [] }));
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: response.data,
      searchQuery: q,
      classes: classesRes.data,
      filters: {}
    });
  } catch (err) {
    console.error('Search API error:', err.message);
    res.render('fleet', {
      title: 'Fleet Roster',
      ships: [],
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
      const shipsRes = await api.get('/api/ships');
      return res.render('compare', {
        title: 'Compare Ships',
        ship1: id1 ? (await api.get(`/api/ships/${id1}`).catch(() => null))?.data : null,
        ship2: null,
        ships: shipsRes.data,
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
    const response = await api.get('/api/ships');
    const ships = (response.data || []).slice(0, 50).map(s => ({
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
      res.set('Cache-Control', 'public, max-age=86400');
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
          res.set('Cache-Control', 'public, max-age=86400');
          return res.send(Buffer.from(proxyRes.data));
        }
      } catch (proxyErr) {
        console.error('Image proxy failed:', proxyErr.message);
      }
      // Proxy failed - serve HTML with img pointing to source URL (IIS may rewrite Location header)
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

app.get('/admin/images', async (req, res) => {
  try {
    const response = await api.get('/api/images/audit');
    res.render('admin-images', { title: 'Image Audit', audit: response.data });
  } catch (err) {
    console.error('Image audit error:', err.message);
    res.render('admin-images', { title: 'Image Audit', audit: null, error: err.message });
  }
});

app.post('/admin/images/populate', async (req, res) => {
  try {
    const response = await api.post('/api/images/populate');
    res.json(response.data);
  } catch (err) {
    res.status(500).json({ error: err.message });
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

app.listen(PORT, () => {
  console.log(`Naval Archive Web running at http://localhost:${PORT}`);
  console.log(`API proxy target: ${API_BASE}`);
});
