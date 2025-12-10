const CACHE_NAME = 'kams-enterprise-final-v1.0';

const PRECACHE_ASSETS = [
  '/',
  '/index.html',
  '/manifest.json',
  'https://cdn.tailwindcss.com'
];

// Install: Cache critical core files immediately
self.addEventListener('install', (event) => {
  self.skipWaiting();
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => {
      // Pre-cache core files. If this fails, the app might not load offline initially.
      return cache.addAll(PRECACHE_ASSETS).catch(err => {
        console.warn('Pre-cache warning:', err);
      });
    })
  );
});

// Activate: Clean up old caches to ensure latest version
self.addEventListener('activate', (event) => {
  event.waitUntil(
    Promise.all([
      clients.claim(),
      caches.keys().then((cacheNames) => {
        return Promise.all(
          cacheNames.map((cacheName) => {
            if (cacheName !== CACHE_NAME) {
              console.log('Deleting old cache:', cacheName);
              return caches.delete(cacheName);
            }
          })
        );
      })
    ])
  );
});

// Fetch: The Core Offline Logic
self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);

  // 1. STRATEGY: Cache First for External CDNs (React, Tailwind, etc.)
  if (url.hostname.includes('cdn') || url.hostname.includes('aistudiocdn')) {
    event.respondWith(
      caches.match(event.request).then((cachedResponse) => {
        if (cachedResponse) {
          return cachedResponse;
        }
        return fetch(event.request).then((networkResponse) => {
          if (networkResponse && networkResponse.status === 200) {
            const responseToCache = networkResponse.clone();
            caches.open(CACHE_NAME).then((cache) => {
              cache.put(event.request, responseToCache);
            });
          }
          return networkResponse;
        }).catch(() => {
          console.log('Offline: Failed to fetch CDN resource', url.href);
        });
      })
    );
    return;
  }

  // 2. STRATEGY: Network First for HTML/App Logic
  // Try to get the latest version from server. If offline, use cache.
  event.respondWith(
    fetch(event.request)
      .then((networkResponse) => {
        const responseToCache = networkResponse.clone();
        caches.open(CACHE_NAME).then((cache) => {
          cache.put(event.request, responseToCache);
        });
        return networkResponse;
      })
      .catch(() => {
        return caches.match(event.request).then(response => {
           return response || caches.match('/index.html');
        });
      })
  );
});