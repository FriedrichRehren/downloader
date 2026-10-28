"use strict";

const form = document.getElementById("form");
const input = document.getElementById("url");
const probeButton = document.getElementById("probe");
const message = document.getElementById("message");
const media = document.getElementById("media");
const thumbnail = document.getElementById("thumbnail");
const resolutionSelect = document.getElementById("video-resolution");
const videoFormat = document.getElementById("video-format");
const audioFormat = document.getElementById("audio-format");
const list = document.getElementById("jobs");
const empty = document.getElementById("empty");
const template = document.getElementById("job");

// Rows are created once per job and updated in place: rebuilding them every
// second would swallow clicks that land mid-render.
const rows = new Map();
let jobs = [];
let fetchedAt = Date.now();
let pollTimer;
// The URL that was read last, and what it offers.
let probed = null;

const number = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 1 });
const formatNames = { mp4: "MP4", mkv: "MKV", mp3: "MP3", m4a: "M4A", flac: "FLAC", wav: "WAV" };

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const url = input.value.trim();
  probeButton.disabled = true;
  probeButton.textContent = "Liest ein …";
  showMessage("");
  media.hidden = true;
  try {
    const response = await fetch("api/probe", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url }),
    });
    if (!response.ok) {
      throw new Error(await problem(response));
    }
    const probe = await response.json();
    // Someone typed over the URL while it was being read.
    if (input.value.trim() === url) {
      probed = { url, probe };
      showMedia(probe);
    }
  } catch (error) {
    showMessage(error.message);
  } finally {
    probeButton.disabled = false;
    probeButton.textContent = "Einlesen";
  }
});

// The formats on show belong to the URL that was read, not to a new one.
input.addEventListener("input", () => {
  if (probed && input.value.trim() !== probed.url) {
    media.hidden = true;
  }
});

videoFormat.addEventListener("change", () => fillResolutions(probed.probe));

for (const button of media.querySelectorAll("button[data-mode]")) {
  button.addEventListener("click", async () => {
    const mode = button.dataset.mode;
    const request = mode === "video"
      ? { url: probed.url, mode, format: videoFormat.value, resolution: Number(resolutionSelect.value) || null }
      : { url: probed.url, mode, format: audioFormat.value };
    button.disabled = true;
    try {
      const response = await fetch("api/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });
      if (!response.ok) {
        throw new Error(await problem(response));
      }
      showMessage("");
      await refresh();
    } catch (error) {
      showMessage(error.message);
    } finally {
      button.disabled = false;
    }
  });
}

function showMedia(probe) {
  setText(document.getElementById("media-title"), probe.title ?? probed.url);
  const duration = probe.duration ? countdown(Math.round(probe.duration)) : null;
  setText(document.getElementById("media-meta"), [probe.uploader, duration].filter(Boolean).join(" · "));
  thumbnail.hidden = !probe.thumbnail;
  thumbnail.src = probe.thumbnail ?? "";

  document.getElementById("video").hidden = probe.resolutions.length === 0 && probe.best.tier == null;
  document.getElementById("audio").hidden = probe.best.audioCodec == null && probe.audioCodecs.length === 0;
  videoFormat.value = "original";
  resolutionSelect.value = "";
  fillResolutions(probe);
  fillAudio(probe);
  media.hidden = false;
}

// The first entry is always "best": no limit, which is what yt-dlp would
// pick on its own. Below it come the lower tiers, each sent as the highest
// resolution within it.
function fillResolutions(probe) {
  const previous = resolutionSelect.value;
  // MP4 means H.264, so only its streams count, and those are never HDR. A
  // source without any H.264 is remuxed as it is.
  const h264 = probe.resolutions
    .filter((offer) => offer.h264)
    .map((offer) => ({ ...offer, fps: offer.h264Fps, hdr: false }));
  const mp4 = videoFormat.value === "mp4" && h264.length > 0;
  const best = mp4 ? h264[0] : probe.best;
  const lower = (mp4 ? h264 : probe.resolutions).filter((offer) => offer.tier < (best.tier ?? Infinity));

  resolutionSelect.replaceChildren(
    option("", [
      "Beste Qualität",
      describe(best),
      mp4 ? "H.264" : best.videoCodec,
      videoFormat.value === "mp4" || !best.size ? null : `ca. ${size(best.size)}`,
    ]),
    ...lower.map((offer) => option(offer.resolution, [describe(offer)])),
  );
  resolutionSelect.value = [...resolutionSelect.options].some((entry) => entry.value === previous) ? previous : "";
}

function fillAudio(probe) {
  const kept = (codec) => (probe.audioCodecs.includes(codec) ? "ohne Umwandlung" : "umgewandelt");
  const best = probe.best;
  audioFormat.replaceChildren(
    option("original", [
      "Beste Qualität",
      best.audioCodec,
      best.audioBitrate ? `${Math.round(best.audioBitrate)} kbit/s` : null,
    ]),
    option("mp3", ["MP3", kept("MP3")]),
    option("m4a", ["M4A (AAC)", kept("AAC")]),
    option("flac", ["FLAC", kept("FLAC")]),
    option("wav", ["WAV", "umgewandelt, unkomprimiert"]),
  );
}

function describe(offer) {
  if (offer.tier == null) {
    return null;
  }
  const fps = offer.fps > 30 ? Math.round(offer.fps) : "";
  const name = offer.tier >= 4320 ? " · 8K" : offer.tier >= 2160 ? " · 4K" : "";
  return `${offer.tier}p${fps}${name}${offer.hdr ? " · HDR" : ""}`;
}

function option(value, parts) {
  return new Option(parts.filter(Boolean).join(" · "), value);
}

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
  setText(row.mode, kind(job));
  setText(row.title, job.title ?? job.fileName ?? job.url);
  row.title.title = job.url;

  row.bar.hidden = job.state !== "running";
  row.bar.classList.toggle("indeterminate", percent === null);
  row.fill.style.width = percent === null ? "" : `${percent}%`;

  setText(row.status, status(job, percent, Math.max(0, (job.expiresIn ?? 0) - elapsed)));
  row.download.hidden = job.state !== "completed";
  setText(row.remove, active ? "Abbrechen" : "Entfernen");
}

function kind(job) {
  const name = formatNames[job.format] ?? (job.mode === "audio" ? "Audio" : "Video");
  return job.resolution ? `${name} ${job.resolution}p` : name;
}

function status(job, percent, expiresIn) {
  switch (job.state) {
    case "queued":
      return "Wartet auf einen freien Platz …";
    case "running":
      // Downloaded, now merging or converting; ffmpeg reports no progress.
      if (percent >= 100) {
        return "Wird verarbeitet …";
      }
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
