// A small service worker so the app can be installed and its shell opens without a network round trip.
// It caches only the app shell (the page and the fingerprinted files under /app/). It never touches /api/, so
// conversations, settings and run streams always come from the server, and nothing a person typed is stored here.
const CACHE = "app-shell-v1";
const SHELL = "/app/";

self.addEventListener("install", () => self.skipWaiting());

self.addEventListener("activate", event => {
  event.waitUntil(
    caches
      .keys()
      .then(names => Promise.all(names.filter(name => name !== CACHE).map(name => caches.delete(name))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", event => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || !url.pathname.startsWith("/app/")) return;

  // The page: always try the network first so a new release shows up at once; fall back to the cached shell offline.
  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request)
        .then(response => {
          if (response.ok) {
            const copy = response.clone();
            caches.open(CACHE).then(cache => cache.put(SHELL, copy));
          }
          return response;
        })
        .catch(() => caches.match(SHELL).then(cached => cached ?? Response.error()))
    );
    return;
  }

  // Fingerprinted scripts, styles and icons never change under the same name, so the cache can answer first.
  if (url.pathname.startsWith("/app/assets/") || url.pathname.startsWith("/app/icons/")) {
    event.respondWith(
      caches.match(request).then(
        cached =>
          cached ??
          fetch(request).then(response => {
            if (response.ok) {
              const copy = response.clone();
              caches.open(CACHE).then(cache => cache.put(request, copy));
            }
            return response;
          })
      )
    );
  }
});
