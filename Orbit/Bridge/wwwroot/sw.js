// Rede primeiro (atualizações chegam), cache como plano B offline. /api e /health nunca em cache.
const CACHE = 'orbit-v2';
const SHELL = ['./', './index.html', './app.js', './style.css', './manifest.webmanifest',
  './icon-192.png', './icon-512.png', './apple-touch-icon.png'];

self.addEventListener('install', e =>
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting())));

self.addEventListener('activate', e =>
  e.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
    .then(() => self.clients.claim())));

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.pathname.startsWith('/api') || url.pathname === '/health') return;
  e.respondWith(
    fetch(e.request)
      .then(r => { if (!r.ok) return r; const copy = r.clone(); caches.open(CACHE).then(c => c.put(e.request, copy)); return r; })
      .catch(() => caches.match(e.request).then(r => r || caches.match('./index.html'))));
});

// Avisos do PC (Web Push): o PC manda {title, body} cifrado; aqui vira notificação do iOS.
self.addEventListener('push', e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch { d = { body: e.data && e.data.text() }; }
  e.waitUntil(self.registration.showNotification(d.title || 'Orbit', { body: d.body || '', icon: 'icon-192.png', badge: 'icon-192.png' }));
});

// Tocar no aviso abre (ou traz pra frente) o app.
self.addEventListener('notificationclick', e => {
  e.notification.close();
  e.waitUntil(self.clients.matchAll({ type: 'window' }).then(list =>
    list.length ? list[0].focus() : self.clients.openWindow('./')));
});
