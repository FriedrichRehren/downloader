"use strict";

const form = document.getElementById("form");
const input = document.getElementById("url");
const message = document.getElementById("message");
const list = document.getElementById("jobs");
const empty = document.getElementById("empty");
const template = document.getElementById("job");

// Rows are created once per job and updated in place: rebuilding them every
// second would swallow clicks that land mid-render.
const rows = new Map();
let jobs = [];
let fetchedAt = Date.now();
let pollTimer;

const number = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 1 });

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const mode = event.submitter?.value ?? "video";
  setBusy(true);
  try {
    const response = await fetch("api/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url: input.value, mode }),
    });
    if (!response.ok) {
      throw new Error(await problem(response));
    }
    input.value = "";
    showMessage("");
    await refresh();
  } catch (error) {
    showMessage(error.message);
  } finally {
    setBusy(false);
    input.focus();
  }
});

async function refresh() {
  clearTimeout(pollTimer);
  try {
    const response = await fetch("api/jobs", { cache: "no-store" });
    if (response.ok) {
      jobs = await response.json();
      fetchedAt = Date.now();
      render();
    }
  } catch {
    // Offline or redeploying; the next poll tries again.
  } finally {
    const active = jobs.some((job) => job.state === "queued" || job.state === "running");
    pollTimer = setTimeout(refresh, active ? 1000 : 5000);
  }
}

function render() {
  const elapsed = Math.floor((Date.now() - fetchedAt) / 1000);

  jobs.forEach((job, index) => {
    let row = rows.get(job.id);
    if (!row) {
      row = createRow(job.id);
      rows.set(job.id, row);
    }
    updateRow(row, job, elapsed);
    if (list.children[index] !== row.element) {
      list.insertBefore(row.element, list.children[index] ?? null);
    }
  });

  for (const [id, row] of rows) {
    if (!jobs.some((job) => job.id === id)) {
      row.element.remove();
      rows.delete(id);
    }
  }

  empty.hidden = jobs.length > 0;
}

function createRow(id) {
  const element = template.content.firstElementChild.cloneNode(true);
  const row = {
    element,
    mode: element.querySelector(".mode"),
    title: element.querySelector(".title"),
    bar: element.querySelector(".bar"),
    fill: element.querySelector(".bar > span"),
    status: element.querySelector(".status"),
    download: element.querySelector(".download"),
    remove: element.querySelector(".remove"),
  };
  row.download.href = `api/jobs/${id}/file`;
  row.remove.addEventListener("click", async () => {
    row.remove.disabled = true;
    try {
      await fetch(`api/jobs/${id}`, { method: "DELETE" });
    } finally {
      await refresh();
    }
  });
  return row;
}

function updateRow(row, job, elapsed) {
  const active = job.state === "queued" || job.state === "running";
  const percent = job.totalBytes ? Math.min(100, (job.downloadedBytes ?? 0) / job.totalBytes * 100) : null;

  row.element.dataset.state = job.state;
  setText(row.mode, job.mode === "audio" ? "Audio" : "Video");
  setText(row.title, job.title ?? job.fileName ?? job.url);
  row.title.title = job.url;

  row.bar.hidden = job.state !== "running";
  row.bar.classList.toggle("indeterminate", percent === null);
  row.fill.style.width = percent === null ? "" : `${percent}%`;

  setText(row.status, status(job, percent, Math.max(0, (job.expiresIn ?? 0) - elapsed)));
  row.download.hidden = job.state !== "completed";
  setText(row.remove, active ? "Abbrechen" : "Entfernen");
}

function status(job, percent, expiresIn) {
  switch (job.state) {
    case "queued":
      return "Wartet auf einen freien Platz …";
    case "running":
      if (percent !== null) {
        return `${number.format(percent)} % · ${size(job.downloadedBytes)} von ${size(job.totalBytes)}`;
      }
      return job.downloadedBytes ? `${size(job.downloadedBytes)} geladen` : "Startet …";
    case "completed":
      return `${size(job.fileSize)} · wird in ${countdown(expiresIn)} gelöscht`;
    default:
      return job.error ?? "Fehlgeschlagen";
  }
}

function size(bytes) {
  if (!bytes) {
    return "0 MB";
  }
  return bytes >= 1024 ** 3
    ? `${number.format(bytes / 1024 ** 3)} GB`
    : `${number.format(bytes / 1024 ** 2)} MB`;
}

function countdown(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor(seconds / 60) % 60;
  const s = String(seconds % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}

function setText(element, text) {
  if (element.textContent !== text) {
    element.textContent = text;
  }
}

function setBusy(busy) {
  for (const button of form.querySelectorAll("button")) {
    button.disabled = busy;
  }
}

function showMessage(text) {
  message.textContent = text;
  message.hidden = !text;
}

async function problem(response) {
  try {
    const body = await response.json();
    return body.detail ?? body.title ?? response.statusText;
  } catch {
    return `Fehler ${response.status}`;
  }
}

refresh();
setInterval(render, 1000);
