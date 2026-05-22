/* ═══════════════════════════════════════════════════════════════════════
   One Pace Downloader — Web UI
   Sources: One Pace / Muhn Pace (Pixeldrain) · Nyaa (qBittorrent) · Usenet (SABnzbd)
   ═══════════════════════════════════════════════════════════════════════ */

let arcs = [];
let episodes = [];
let torrents = [];
let activeArc = null;
let activeSource = "onepace";
let settings = {};
let downloads = {};
let clientTransfers = [];

const SOURCE_DESCS = {
  onepace: "Fan re-cut. Sub for every arc, Dub for newer ones. Direct download via the bypass CDN.",
  muhn: "Fan-made English Dub fillers for arcs One Pace hasn't dubbed (Enies Lobby → Wano).",
  nyaa: "Torrents from nyaa.si — selected magnets are sent straight to your qBittorrent.",
  usenet: "NZB releases via NZBGeek — selected episodes are sent to your SABnzbd.",
};

const ACTION_LABELS = {
  onepace: "Download selected",
  muhn: "Download selected",
  nyaa: "Send to qBittorrent",
  usenet: "Send to SABnzbd",
};

/* ── API helper ──────────────────────────────────────────────────────── */

async function api(path, opts = {}) {
  const res = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...opts,
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({ detail: res.statusText }));
    throw new Error(err.detail || res.statusText);
  }
  return res.json();
}

/* ── Toast ────────────────────────────────────────────────────────────── */

function toast(message, type = "success") {
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.textContent = message;
  document.getElementById("toasts").appendChild(el);
  setTimeout(() => {
    el.style.opacity = "0";
    el.style.transform = "translateX(30px)";
    el.style.transition = "all 0.25s";
    setTimeout(() => el.remove(), 250);
  }, 4000);
}

/* ── Source tabs ──────────────────────────────────────────────────────── */

function switchSource(source) {
  activeSource = source;
  document.querySelectorAll(".source-tab").forEach((t) => {
    t.classList.toggle("active", t.dataset.source === source);
  });
  document.getElementById("sourceDesc").textContent = SOURCE_DESCS[source] || "";
  activeArc = null;
  episodes = [];
  torrents = [];
  document.getElementById("episodeHeading").textContent =
    source === "nyaa" ? "Torrents" : "Episodes";
  document.getElementById("episodeToolbar").style.display = "none";
  document.getElementById("episodeList").innerHTML = `
    <div class="empty-state">
      <div class="empty-icon">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="var(--orange)" stroke-width="1.5"><polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2" ry="2"/></svg>
      </div>
      <p>Select an arc to browse ${source === "nyaa" ? "torrents" : "episodes"}</p>
    </div>`;
  renderArcs(document.getElementById("arcSearch").value);
}

/* ── Load arcs ───────────────────────────────────────────────────────── */

async function loadArcs() {
  try {
    arcs = await api("/api/arcs");
    const count = (k) => arcs.filter((a) => a.sources.includes(k)).length;
    document.getElementById("countOnepace").textContent = count("onepace");
    document.getElementById("countMuhn").textContent = count("muhn");
    document.getElementById("countNyaa").textContent = count("nyaa");
    document.getElementById("countUsenet").textContent = count("usenet");
    renderArcs();
    loadStats();
  } catch (e) {
    toast("Failed to load arcs: " + e.message, "error");
  }
}

function renderArcs(filter = "") {
  const list = document.getElementById("arcList");
  let filtered = arcs.filter((a) => a.sources.includes(activeSource));
  if (filter) {
    filtered = filtered.filter((a) =>
      a.title.toLowerCase().includes(filter.toLowerCase())
    );
  }
  if (!filtered.length) {
    list.innerHTML = `
      <div class="empty-state">
        <div class="empty-icon">&#x1F50D;</div>
        <p>${arcs.length ? "No matching arcs" : "Loading..."}</p>
      </div>`;
    return;
  }
  list.innerHTML = filtered
    .map((a) => {
      const isActive = activeArc === a.title;
      const badge = `${a.episode_count} ep${a.episode_count !== 1 ? "s" : ""}`;
      return `
      <div class="arc-item${isActive ? " active" : ""}"
           onclick="selectArc('${escapeAttr(a.title)}')">
        <span class="arc-item-title">${esc(a.title)}</span>
        <span class="arc-item-badge">${badge}</span>
      </div>`;
    })
    .join("");
}

function filterArcs() {
  renderArcs(document.getElementById("arcSearch").value);
}

/* ── Select arc ──────────────────────────────────────────────────────── */

async function selectArc(title) {
  activeArc = title;
  renderArcs(document.getElementById("arcSearch").value);

  const arc = arcs.find((a) => a.title === title);
  const sourceName = { onepace: "One Pace", muhn: "Muhn Pace",
                       nyaa: "Nyaa", usenet: "Usenet" }[activeSource];
  document.getElementById("episodeToolbar").style.display = "flex";
  document.getElementById("downloadBtnLabel").textContent =
    ACTION_LABELS[activeSource];

  if (activeSource === "nyaa") {
    document.getElementById("episodeHeading").innerHTML =
      `Torrents &mdash; <strong>${esc(title)}</strong> &middot; ${sourceName}`;
    try {
      torrents = await api(`/api/arcs/${encodeURIComponent(title)}/torrents`);
      renderTorrents();
    } catch (e) {
      toast("Failed to load torrents: " + e.message, "error");
    }
  } else {
    const epCount = arc ? arc.episode_count : 0;
    document.getElementById("episodeHeading").innerHTML =
      `Episodes &mdash; <strong>${esc(title)}</strong> (${epCount}) &middot; ${sourceName}`;
    document.getElementById("statusText").textContent =
      `${esc(title)} — ${epCount} episodes`;
    try {
      episodes = await api(`/api/arcs/${encodeURIComponent(title)}/episodes`);
      renderEpisodes();
    } catch (e) {
      toast("Failed to load episodes: " + e.message, "error");
    }
  }
}

/* ── Render episodes ─────────────────────────────────────────────────── */

function renderEpisodes() {
  const list = document.getElementById("episodeList");
  if (!episodes.length) {
    list.innerHTML = `
      <div class="empty-state">
        <div class="empty-icon">&#x1F3AC;</div>
        <p>No episodes available</p>
      </div>`;
    return;
  }

  const selectable = episodes.filter((ep) =>
    (ep.kinds || []).includes(activeSource)
  ).length;
  document.getElementById("selectAllBtn").textContent =
    `Select all (${selectable})`;

  list.innerHTML = episodes
    .map((ep) => {
      const available = (ep.kinds || []).includes(activeSource);
      let tags = "";
      if (ep.has_sub) tags += '<span class="tag tag-sub">Sub</span>';
      if (ep.has_dub) tags += '<span class="tag tag-dub">Dub</span>';
      const checkOrLabel = available
        ? `<input type="checkbox" data-num="${ep.num}" />`
        : `<span class="ep-na" title="Not on ${activeSource}">—</span>`;
      return `
      <div class="ep-row${available ? "" : " ep-row-disabled"}"
           ${available ? `onclick="toggleRow(event, ${ep.num})"` : ""}>
        <span class="ep-check">${checkOrLabel}</span>
        <span class="ep-num">${String(ep.num).padStart(2, "0")}</span>
        <span class="ep-title" title="${escapeAttr(ep.canonical_title || ep.title)}">${esc(ep.canonical_title || ep.title)}</span>
        <span class="ep-tags">${tags}</span>
        <span class="ep-size">${ep.size || ""}</span>
      </div>`;
    })
    .join("");
}

function toggleRow(event, num) {
  if (event.target.tagName === "INPUT") return;
  const box = document.querySelector(
    `#episodeList input[type="checkbox"][data-num="${num}"]`
  );
  if (box) box.checked = !box.checked;
}

/* ── Render torrents (Nyaa) ──────────────────────────────────────────── */

function renderTorrents() {
  const list = document.getElementById("episodeList");
  if (!torrents.length) {
    list.innerHTML = `
      <div class="empty-state">
        <div class="empty-icon">&#x1F9F2;</div>
        <p>No torrents found for this arc</p>
      </div>`;
    document.getElementById("selectAllBtn").textContent = "Select all (0)";
    return;
  }
  document.getElementById("selectAllBtn").textContent =
    `Select all (${torrents.length})`;

  list.innerHTML = torrents
    .map((t, i) => {
      const meta = [
        t.quality,
        t.size,
        t.seeders != null ? `&#8593;${t.seeders}` : "",
        t.uploader ? `by ${esc(t.uploader)}` : "",
      ]
        .filter(Boolean)
        .join("  &middot;  ");
      return `
      <div class="ep-row torrent-row" onclick="toggleTorrentRow(event, ${i})">
        <span class="ep-check"><input type="checkbox" data-idx="${i}" /></span>
        <span class="torrent-info">
          <span class="ep-title" title="${escapeAttr(t.title)}">${esc(t.title)}</span>
          <span class="torrent-meta">${meta}</span>
        </span>
      </div>`;
    })
    .join("");
}

function toggleTorrentRow(event, idx) {
  if (event.target.tagName === "INPUT") return;
  const box = document.querySelector(
    `#episodeList input[type="checkbox"][data-idx="${idx}"]`
  );
  if (box) box.checked = !box.checked;
}

/* ── Select all ──────────────────────────────────────────────────────── */

function toggleSelectAll() {
  const boxes = document.querySelectorAll(
    '#episodeList input[type="checkbox"]'
  );
  const allChecked = boxes.length && [...boxes].every((b) => b.checked);
  boxes.forEach((b) => (b.checked = !allChecked));
}

/* ── Primary action — dispatched by source ───────────────────────────── */

function primaryAction() {
  if (activeSource === "nyaa") return torrentsSend();
  if (activeSource === "usenet") return usenetSend();
  return downloadSelected();
}

function checkedEpisodeNums() {
  return [...document.querySelectorAll(
    '#episodeList input[type="checkbox"]:checked')]
    .map((b) => parseInt(b.dataset.num));
}

/* Pixeldrain download (One Pace / Muhn Pace) */
async function downloadSelected() {
  const nums = checkedEpisodeNums();
  if (!nums.length) return toast("No episodes selected", "error");
  try {
    await api("/api/downloads", {
      method: "POST",
      body: JSON.stringify({
        arc_title: activeArc,
        episode_nums: nums,
        source: activeSource,
        version: settings.version || "English Subtitles",
        quality: settings.quality || "1080p",
      }),
    });
    toast(`Download started: ${activeArc} (${nums.length} episodes)`, "info");
  } catch (e) {
    toast("Download failed: " + e.message, "error");
  }
}

/* Usenet → SABnzbd */
async function usenetSend() {
  const nums = checkedEpisodeNums();
  if (!nums.length) return toast("No episodes selected", "error");
  try {
    const r = await api("/api/usenet/send", {
      method: "POST",
      body: JSON.stringify({
        arc_title: activeArc,
        episode_nums: nums,
        quality: settings.quality || "1080p",
      }),
    });
    reportSend(r, "SABnzbd");
  } catch (e) {
    toast("SABnzbd: " + e.message, "error");
  }
}

/* Nyaa → qBittorrent */
async function torrentsSend() {
  const magnets = [...document.querySelectorAll(
    '#episodeList input[type="checkbox"]:checked')]
    .map((b) => torrents[parseInt(b.dataset.idx)])
    .filter(Boolean)
    .map((t) => t.magnet);
  if (!magnets.length) return toast("No torrents selected", "error");
  try {
    const r = await api("/api/torrents/send", {
      method: "POST",
      body: JSON.stringify({ magnets }),
    });
    reportSend(r, "qBittorrent");
  } catch (e) {
    toast("qBittorrent: " + e.message, "error");
  }
}

function reportSend(result, target) {
  if (result.sent && !result.failed) {
    toast(`Sent ${result.sent} to ${target}`);
  } else if (result.sent && result.failed) {
    toast(`${target}: ${result.sent} sent, ${result.failed} failed`, "info");
  } else {
    toast(`${target}: nothing queued — ${(result.messages || [])[0] || "check settings"}`, "error");
  }
}

/* ── Right panel: downloads ──────────────────────────────────────────── */

const SOURCE_TAGS = {
  pixeldrain: "Direct",
  usenet: "SABnzbd",
  nyaa: "qBittorrent",
};

/* One unified card for any transfer — Pixeldrain, SABnzbd or qBittorrent. */
function transferCard(t) {
  const pct = Math.round((t.progress || 0) * 100);
  const tag = SOURCE_TAGS[t.source] || t.source;
  return `
    <div class="dl-job">
      <div class="dl-job-head">
        <span class="dl-job-title">${esc(t.title)}</span>
        <span class="src-tag src-${t.source}">${tag}</span>
      </div>
      ${t.sub ? `<div class="dl-job-sub">${esc(t.sub)}</div>` : ""}
      <div class="dl-job-progress">
        <span class="dl-pct-big">${pct}%</span>
        <span class="dl-speed-big">${esc(t.speed || "")}</span>
      </div>
      <div class="progress-bar"><div class="progress-fill" style="width:${pct}%"></div></div>
      <div class="dl-job-meta">
        <span>${esc(t.meta || "")}</span>
        <span class="dl-status-pill ${t.status}">${esc(t.status)}</span>
      </div>
    </div>`;
}

function renderDownloads() {
  // Pixeldrain jobs come from our own download manager (SSE).
  const pix = Object.values(downloads);
  const pixActive = pix
    .filter((d) => d.status === "downloading" || d.status === "queued")
    .map((d) => ({
      source: "pixeldrain",
      title: d.arc_title,
      sub: d.current_file || "Preparing...",
      progress: d.progress,
      speed: d.speed,
      status: d.status,
      meta: `${d.current_idx || 0} of ${d.total_files || 0} files`,
    }));
  const pixDone = pix.filter((d) =>
    ["done", "error", "cancelled"].includes(d.status)
  );

  // SABnzbd / qBittorrent transfers come from polling (clientTransfers).
  const clientActive = clientTransfers.map((t) => ({
    source: t.source,
    title: t.name,
    sub: "",
    progress: t.progress,
    speed: t.speed,
    status: t.status,
    meta: t.eta ? "ETA " + t.eta : "",
  }));

  const active = [...pixActive, ...clientActive];

  // Downloads panel when anything's happening; otherwise the guide.
  if (!active.length && !pixDone.length) {
    document.getElementById("guidePanel").style.display = "";
    document.getElementById("downloadPanel").style.display = "none";
    return;
  }
  document.getElementById("guidePanel").style.display = "none";
  document.getElementById("downloadPanel").style.display = "";

  const statusEl = document.getElementById("downloadStatus");
  statusEl.innerHTML = active.length
    ? active.map(transferCard).join("")
    : `<p style="font-size:0.82rem;color:var(--dim)">No active downloads.</p>`;

  const histCard = document.getElementById("downloadHistoryCard");
  const histEl = document.getElementById("downloadHistory");
  if (pixDone.length) {
    histCard.style.display = "";
    histEl.innerHTML = pixDone
      .map(
        (d) => `
        <div class="dl-hist-row">
          <span>${esc(d.arc_title)}</span>
          <span class="dl-status-pill ${d.status}">${d.status}</span>
        </div>`
      )
      .join("");
  } else {
    histCard.style.display = "none";
  }

  const a = active[0];
  if (a) {
    document.getElementById("statusText").textContent =
      `${Math.round((a.progress || 0) * 100)}%  ${a.speed || ""} — ${a.title}`;
  }
}

/* ── Client polling — SABnzbd / qBittorrent progress ─────────────────── */

let clientPollTimer = null;

async function pollClients() {
  try {
    const r = await api("/api/clients/status");
    clientTransfers = r.transfers || [];
  } catch (e) {
    clientTransfers = [];
  }
  renderDownloads();
}

/* Poll only when a hand-off client is actually configured. */
function startClientPolling() {
  if (clientPollTimer) {
    clearInterval(clientPollTimer);
    clientPollTimer = null;
  }
  if (settings.sabnzbd_url || settings.qbittorrent_url) {
    pollClients();
    clientPollTimer = setInterval(pollClients, 5000);
  } else {
    clientTransfers = [];
    renderDownloads();
  }
}

/* ── SSE stream ──────────────────────────────────────────────────────── */

function connectSSE() {
  const es = new EventSource("/api/downloads/events/stream");
  es.onmessage = (ev) => {
    try {
      const data = JSON.parse(ev.data);
      downloads[data.id] = data;
      renderDownloads();
      if (data.status === "done" && !data._toasted) {
        toast(`Completed: ${data.arc_title}`);
        data._toasted = true;
        loadStats();
      }
      if (data.status === "error" && !data._toasted) {
        toast(`Error: ${data.arc_title} — ${data.error || "unknown"}`, "error");
        data._toasted = true;
      }
    } catch (e) {}
  };
  es.onerror = () => {
    es.close();
    setTimeout(connectSSE, 5000);
  };
}

/* ── Stats ───────────────────────────────────────────────────────────── */

async function loadStats() {
  try {
    const s = await api("/api/stats");
    document.getElementById("statDownloaded").textContent = s.downloaded_episodes;
    document.getElementById("statTotal").textContent = s.total_episodes;
    document.getElementById("statArcs").textContent = s.total_arcs;
    document.getElementById("footerStats").textContent =
      `${s.downloaded_episodes} / ${s.total_episodes} episodes`;
  } catch (e) {}
}

/* ── Refresh ─────────────────────────────────────────────────────────── */

async function refreshIndex() {
  toast("Refreshing index...", "info");
  try {
    const res = await api("/api/refresh", { method: "POST" });
    if (res.success) {
      toast("Index refreshed!");
      loadArcs();
    } else {
      toast("Refresh failed: " + (res.messages || []).join(", "), "error");
    }
  } catch (e) {
    toast("Refresh error: " + e.message, "error");
  }
}

/* ── Settings ────────────────────────────────────────────────────────── */

const SETTINGS_FIELDS = {
  sabUrl: "sabnzbd_url",
  sabKey: "sabnzbd_api_key",
  sabCat: "sabnzbd_category",
  qbUrl: "qbittorrent_url",
  qbUser: "qbittorrent_user",
  qbPass: "qbittorrent_pass",
  qbCat: "qbittorrent_category",
  geekUrl: "nzbgeek_url",
  geekKey: "nzbgeek_api_key",
};

async function loadSettings() {
  try {
    settings = await api("/api/settings");
    const vSel = document.getElementById("settingsVersion");
    const qSel = document.getElementById("settingsQuality");
    vSel.innerHTML = (settings.available_versions || [])
      .map((v) =>
        `<option value="${escapeAttr(v)}"${v === settings.version ? " selected" : ""}>${esc(v)}</option>`)
      .join("");
    qSel.innerHTML = (settings.available_qualities || [])
      .map((q) =>
        `<option value="${escapeAttr(q)}"${q === settings.quality ? " selected" : ""}>${esc(q)}</option>`)
      .join("");
    for (const [elId, key] of Object.entries(SETTINGS_FIELDS)) {
      const el = document.getElementById(elId);
      if (el) el.value = settings[key] || "";
    }
    startClientPolling();
  } catch (e) {}
}

function openSettings() {
  document.getElementById("settingsModal").classList.add("active");
}
function closeSettings() {
  document.getElementById("settingsModal").classList.remove("active");
}

function settingsPayload() {
  const p = {
    version: document.getElementById("settingsVersion").value,
    quality: document.getElementById("settingsQuality").value,
  };
  for (const [elId, key] of Object.entries(SETTINGS_FIELDS)) {
    const el = document.getElementById(elId);
    if (el) p[key] = el.value.trim();
  }
  return p;
}

async function saveSettings() {
  try {
    await api("/api/settings", {
      method: "PUT",
      body: JSON.stringify(settingsPayload()),
    });
    settings = { ...settings, ...settingsPayload() };
    toast("Settings saved");
    closeSettings();
    startClientPolling();
  } catch (e) {
    toast("Failed to save: " + e.message, "error");
  }
}

async function testIntegration(which) {
  const statusEl = {
    sabnzbd: "sabStatus",
    qbittorrent: "qbStatus",
    nzbgeek: "geekStatus",
  }[which];
  const el = document.getElementById(statusEl);
  el.textContent = "Testing...";
  el.className = "test-status testing";
  try {
    const r = await api(`/api/integrations/test/${which}`, {
      method: "POST",
      body: JSON.stringify(settingsPayload()),
    });
    el.textContent = r.message;
    el.className = "test-status " + (r.ok ? "ok" : "bad");
  } catch (e) {
    el.textContent = e.message;
    el.className = "test-status bad";
  }
}

/* ── Utilities ───────────────────────────────────────────────────────── */

function esc(s) {
  const d = document.createElement("div");
  d.textContent = s == null ? "" : String(s);
  return d.innerHTML;
}
function escapeAttr(s) {
  return (s == null ? "" : String(s))
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/* ── Init ────────────────────────────────────────────────────────────── */

document.addEventListener("DOMContentLoaded", () => {
  loadSettings();
  loadArcs();
  connectSSE();

  document.getElementById("settingsModal").addEventListener("click", (e) => {
    if (e.target === e.currentTarget) closeSettings();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") closeSettings();
  });
});
