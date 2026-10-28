"use strict";

// Network first: the page is small and changes with every release, so the
// cache only stands in while the server cannot be reached. The API and the
// downloads pass straight through; caching those would serve stale job lists
// and copy whole video files into the browser.
const CACHE = "downloader-shell-v1";
const SHELL = ["./", "app.js", "install.js", "app.css", "manifest.webmanifest", "icons/icon-192.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE)
      .then((cache) => cache.addAll(SHELL))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/")) {
    return;
  }

  event.respondWith(
    fetch(event.request)
      .then((response) => {
        if (response.ok) {
          const copy = response.clone();
          event.waitUntil(caches.open(CACHE).then((cache) => cache.put(event.request, copy)));
        }
        return response;
      })
      // A shared link opens /?text=…, which the cache knows as /.
      .catch(() => caches.match(event.request, { ignoreSearch: true }).then((cached) => cached ?? Response.error())),
  );
});
