"use strict";

// Offers to install the page as an app whenever it is opened. "Nein" holds
// off until the next visit, "Nie wieder fragen" for good; once the page runs
// as an installed app, it never asks.
(() => {
  const NEVER = "downloader-install-never";
  const LATER = "downloader-install-later";

  const banner = document.getElementById("install");
  const hint = document.getElementById("install-hint");
  const yes = document.getElementById("install-yes");
  const no = document.getElementById("install-no");
  const never = document.getElementById("install-never");

  // Safari has no install prompt; there, "Installieren" explains the steps.
  const ios = /iphone|ipad|ipod/i.test(navigator.userAgent)
    || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const installed = matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  let deferred = null;

  window.addEventListener("beforeinstallprompt", (event) => {
    // Always, so the browser's own banner cannot ignore "Nie wieder fragen".
    event.preventDefault();
    deferred = event;
    show();
  });
  window.addEventListener("appinstalled", () => {
    banner.hidden = true;
  });

  if (ios) {
    show();
  }

  yes.addEventListener("click", async () => {
    if (deferred) {
      const prompt = deferred;
      deferred = null;
      prompt.prompt();
      const { outcome } = await prompt.userChoice;
      if (outcome === "accepted") {
        banner.hidden = true;
      } else {
        // Turning down the browser's dialog counts as "Nein".
        later();
      }
    } else if (ios && !yes.dataset.explained) {
      yes.dataset.explained = "true";
      hint.textContent = "Tippe in Safari auf „Teilen“ und dann auf „Zum Home-Bildschirm“.";
      yes.textContent = "Verstanden";
      // Would do the same as "Verstanden" now.
      no.hidden = true;
    } else {
      later();
    }
  });
  no.addEventListener("click", later);
  never.addEventListener("click", () => {
    write(localStorage, NEVER);
    banner.hidden = true;
  });

  function show() {
    if (!installed && !read(localStorage, NEVER) && !read(sessionStorage, LATER)) {
      banner.hidden = false;
    }
  }

  // Session storage: the next visit is a new session and asks again.
  function later() {
    write(sessionStorage, LATER);
    banner.hidden = true;
  }

  // Storage can be missing or throw, in private windows for one; then the
  // banner simply asks again.
  function read(storage, key) {
    try {
      return storage.getItem(key) !== null;
    } catch {
      return false;
    }
  }

  function write(storage, key) {
    try {
      storage.setItem(key, "1");
    } catch {}
  }
})();
