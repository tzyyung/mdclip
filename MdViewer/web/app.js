"use strict";
// MdViewer web UI. The native host (MainForm.cs) serves this page, the file tree (/__api/tree)
// and rendered documents (/<relative path>), and talks to us over chrome.webview messages.

const cfg = window.MD;
const $ = (id) => document.getElementById(id);
const frame = $("frame");
const body = document.body;
const post = (msg) => window.chrome?.webview?.postMessage(msg);
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

const ICONS = {
  back: '<path d="M15 18l-6-6 6-6"/>',
  forward: '<path d="M9 18l6-6-6-6"/>',
  reload: '<path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  moon: '<path d="M20 13.5A8 8 0 1 1 10.5 4a6.5 6.5 0 0 0 9.5 9.5z"/>',
  palette: '<path d="M12 3a9 9 0 1 0 0 18c1 0 1.7-.8 1.7-1.7 0-.5-.2-.9-.5-1.2-.3-.3-.5-.7-.5-1.2 0-1 .8-1.7 1.7-1.7H16a5 5 0 0 0 5-5c0-4-4-7.2-9-7.2z"/><circle cx="7.5" cy="11.5" r="1"/><circle cx="10" cy="7.5" r="1"/><circle cx="15" cy="7.5" r="1"/>',
  more: '<circle cx="5" cy="12" r="1.2"/><circle cx="12" cy="12" r="1.2"/><circle cx="19" cy="12" r="1.2"/>',
  side: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M9 4v16"/>',
  toc: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M15 4v16M18 8h-0.01M18 11h-0.01"/>',
  zen: '<path d="M4 9V5a1 1 0 0 1 1-1h4M15 4h4a1 1 0 0 1 1 1v4M20 15v4a1 1 0 0 1-1 1h-4M9 20H5a1 1 0 0 1-1-1v-4"/>',
  chev: '<path d="M9 6l6 6-6 6"/>',
  file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h4"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  folderOpen: '<path d="M3 17V7a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v1"/><path d="M3 17l2.4-6a2 2 0 0 1 1.9-1.3H21l-2.5 7.4a2 2 0 0 1-1.9 1.4H5a2 2 0 0 1-2-2z"/>',
  hash: '<path d="M5 9h14M5 15h14M10 4L8 20M16 4l-2 16"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  copy: '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  explorer: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 11v5M9.5 13.5L12 16l2.5-2.5"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="1.8"/><path d="M21 16l-5-5-9 9"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  keyboard: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  up: '<path d="M6 15l6-6 6 6"/>',
  down: '<path d="M6 9l6 6 6-6"/>',
  text: '<path d="M4 6h16M4 12h10M4 18h7"/><circle cx="17.5" cy="16.5" r="3"/><path d="M20 19l2 2"/>',
};
const icon = (name, cls = "") =>
  `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`;
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

// localStorage is a convenience only; every read/write may throw or come back empty.
const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem("md:" + key); return v == null ? fallback : JSON.parse(v); } catch { return fallback; }
  },
  set(key, value) { try { localStorage.setItem("md:" + key, JSON.stringify(value)); } catch { /* ignore */ } },
};

// Matches a request to the host with its answer (resolve, search): only the latest one counts.
const requestId = () => String(Date.now());

// A tab with no page yet.
function emptyTab() { return { hist: [], at: -1 }; }

const state = {
  tree: [],
  files: [],            // flat list: { rel, name, dir }
  current: null,        // relative path of the open document
  hist: [],             // own history so back/forward also restore scroll: { rel, scroll }
  at: -1,
  pending: null,        // what to do once the iframe finishes loading: { hash, scroll }
  headings: [],
  open: new Set(),      // expanded folders, per root
  history: cfg.history ?? [], // files read lately, across all roots: { path, when, exists }
  tabs: [emptyTab()], // each tab keeps its own back/forward list; state.hist/at are the active tab's
  tab: 0,
  theme: cfg.theme,
  lastLight: cfg.lastLight,
  lastDark: cfg.lastDark,
};

// The image extensions come from the host (Paths.ImageExtensions), like every rule it owns.
const imagePattern = new RegExp(`\\.(${cfg.imageExtensions.join("|")})$`, "i");
const isImage = (rel) => imagePattern.test(rel);
const kindOf = (rel) => (isImage(rel) ? "image" : "doc");
// Images open as a zoomable viewer page; the bare URL stays the raw image for <img src>.
const urlOf = (rel) => "/" + rel.split("/").map(encodeURIComponent).join("/") + (isImage(rel) ? "?view" : "");
const relOf = (pathname) => decodeURIComponent(pathname.replace(/^\//, ""));
const baseName = (rel) => rel.slice(rel.lastIndexOf("/") + 1);
const fileIcon = (rel) => (isImage(rel) ? "image" : "file");
// What the host marked a page as: a picture in the zoom viewer, or a document big enough that off-screen
// parts are only estimated (doc.css).
const isImagePage = (doc) => !!doc.body?.classList.contains("image-page");
const isBigPage = (doc) => !!doc.body?.classList.contains("big");

// Full Windows paths (the reading history, the clipboard, copied links) against the open folder.
const rootDir = () => cfg.root.replace(/\\$/, "");
const fullPath = (rel) => rootDir() + "\\" + rel.replaceAll("/", "\\");
// The path relative to the open folder for a file inside it, else null.
function relUnderRoot(path) {
  const root = rootDir();
  return path.toLowerCase().startsWith(root.toLowerCase() + "\\") ? path.slice(root.length + 1).replaceAll("\\", "/") : null;
}
const nameOf = (path) => baseName(path.replaceAll("\\", "/"));
// Where a file lives, for lists: relative to the open folder when inside it, else the full folder path.
function dirLabel(path) {
  const rel = relUnderRoot(path);
  if (rel === null) return path.slice(0, path.lastIndexOf("\\"));
  return rel.includes("/") ? rel.slice(0, rel.lastIndexOf("/")) : cfg.name;
}

// ---------- project / root ----------

function loadRootState() {
  state.open = new Set(store.get("open:" + cfg.root, []));
  restoreTabs();
  $("proj-name").textContent = cfg.name;
  $("proj-path").textContent = cfg.root;
  $("proj-path").title = cfg.root;
}

// ---------- theme ----------

const themeById = Object.fromEntries(cfg.themes.map((t) => [t.id, t]));
const resolveTheme = (id) =>
  themeById[id === cfg.autoTheme ? (cfg.systemDark ? cfg.autoDark : cfg.autoLight) : id] ?? cfg.themes[0];

function setThemeAttr(id) {
  document.documentElement.dataset.theme = id;
  const doc = frame.contentDocument;
  if (doc?.documentElement) doc.documentElement.dataset.theme = id;
}

// The last light and dark picks (for the light/dark toggle) are kept here and stored by the host.
function applyTheme(id, save = true) {
  const t = resolveTheme(id);
  state.theme = id;
  if (id !== cfg.autoTheme) { if (t.dark) state.lastDark = id; else state.lastLight = id; }
  setThemeAttr(t.id);
  $("dark").innerHTML = icon(t.dark ? "sun" : "moon");
  $("dark").title = withKey("dark", t.dark ? "切換為亮色" : "切換為暗色");
  if (save) post({ type: "theme", id, lastLight: state.lastLight, lastDark: state.lastDark });
  if (frame.contentDocument?.querySelector(".mermaid.figure")) renderDiagrams(frame.contentDocument);
}

const toggleDark = () => applyTheme(resolveTheme(state.theme).dark ? state.lastLight : state.lastDark);

// ---------- layout: sidebar, outline, focus mode ----------

// The panels' default widths are app.css's --side-w and --toc-w.
const rootStyle = getComputedStyle(document.documentElement);
const defaultWidths = { sideW: parseInt(rootStyle.getPropertyValue("--side-w")), tocW: parseInt(rootStyle.getPropertyValue("--toc-w")) };
const layout = Object.assign(
  { side: true, toc: true, zen: false, ...defaultWidths, kind: "all", zoom: 1 },
  store.get("layout", {}), { clip: cfg.clip }); // the host owns the clipboard mode (the tray menu sets it too)
// Also the host's: how many other characters may come with a path for it to open by itself, and what
// to do when one copy names several files ("list" them, or open the "first").
const clipOpts = { chars: cfg.clipChars, multi: cfg.clipMulti, max: cfg.clipMax };

function applyLayout() {
  body.classList.toggle("no-side", !layout.side);
  body.classList.toggle("no-toc", !layout.toc);
  body.classList.toggle("zen", layout.zen);
  body.style.setProperty("--side-w", layout.sideW + "px");
  body.style.setProperty("--toc-w", layout.tocW + "px");
  $("toggle-side").classList.toggle("on", layout.side && !layout.zen);
  $("toggle-toc").classList.toggle("on", layout.toc && !layout.zen);
  $("toggle-zen").classList.toggle("on", layout.zen);
  saveLayout();
}

const saveLayout = () => store.set("layout", layout);

function toggleSide() { if (layout.zen) layout.zen = false; else layout.side = !layout.side; applyLayout(); }
function toggleToc() { if (layout.zen) layout.zen = false; else layout.toc = !layout.toc; applyLayout(); }
function toggleZen() {
  layout.zen = !layout.zen;
  applyLayout();
  toast(layout.zen ? "專注閱讀：按 F11 或 Esc 離開，滑鼠移到邊緣可叫出面板" : "已離開專注閱讀");
}

// ---------- text zoom (document only; the chrome keeps its size) ----------

const ZOOMS = [0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

function applyZoom(doc = frame.contentDocument) {
  $("zoom-reset").textContent = Math.round(layout.zoom * 100) + "%";
  if (!doc?.body || isImagePage(doc)) return;
  // Keep the reading position: restore the same fraction of the page after reflow.
  const root = doc.documentElement, win = doc.defaultView;
  const ratio = root.scrollHeight > win.innerHeight ? win.scrollY / (root.scrollHeight - win.innerHeight) : 0;
  doc.body.style.zoom = layout.zoom;
  if (ratio) win.scrollTo(0, ratio * (root.scrollHeight - win.innerHeight));
}

function zoomStep(delta) {
  const i = ZOOMS.findIndex((z) => z >= layout.zoom - 0.001);
  const next = delta === 0 ? 1 : ZOOMS[Math.min(ZOOMS.length - 1, Math.max(0, (i < 0 ? ZOOMS.indexOf(1) : i) + delta))];
  if (next === layout.zoom && delta !== 0) return;
  layout.zoom = next;
  saveLayout();
  applyZoom();
  if (delta !== 0 || next === 1) toast("文字大小 " + Math.round(next * 100) + "%");
}

function onCtrlWheel(e) {
  if (!e.ctrlKey) return;
  e.preventDefault();
  zoomStep(e.deltaY < 0 ? 1 : -1);
}

// Peeking: touching a window edge floats the hidden panel over the document until the mouse leaves it.
function setupPeek(hotId, panel, cls) {
  let timer;
  const show = () => { clearTimeout(timer); timer = setTimeout(() => body.classList.add(cls), 90); };
  const hide = () => { clearTimeout(timer); timer = setTimeout(() => body.classList.remove(cls), 280); };
  $(hotId).addEventListener("mouseenter", show);
  $(hotId).addEventListener("mouseleave", () => { if (!body.classList.contains(cls)) clearTimeout(timer); });
  panel.addEventListener("mouseenter", () => { if (body.classList.contains(cls)) clearTimeout(timer); });
  panel.addEventListener("mouseleave", () => { if (body.classList.contains(cls) && !panel.contains(document.activeElement)) hide(); });
  panel.addEventListener("focusout", () => { if (!panel.matches(":hover")) hide(); });
}

// The file tree's width is measured from the left edge, the outline's from the right; double-click resets.
function setupResizer() {
  const bars = [
    { bar: $("resizer"), key: "sideW", prop: "--side-w", min: 200, width: (x) => x },
    { bar: $("resizer-toc"), key: "tocW", prop: "--toc-w", min: 180, width: (x) => innerWidth - x },
  ];
  for (const { bar, key, prop, min, width } of bars) {
    bar.addEventListener("mousedown", (e) => {
      e.preventDefault();
      body.classList.add("dragging");
      bar.classList.add("active");
      const move = (ev) => {
        layout[key] = Math.round(Math.min(Math.max(width(ev.clientX), min), Math.min(620, innerWidth * 0.4)));
        body.style.setProperty(prop, layout[key] + "px");
      };
      const up = () => {
        body.classList.remove("dragging");
        bar.classList.remove("active");
        removeEventListener("mousemove", move);
        removeEventListener("mouseup", up);
        applyLayout();
      };
      addEventListener("mousemove", move);
      addEventListener("mouseup", up);
    });
    bar.addEventListener("dblclick", () => { layout[key] = defaultWidths[key]; applyLayout(); });
  }
}

// ---------- file tree ----------

async function loadTree() {
  try {
    const res = await fetch("/__api/tree", { cache: "no-store" });
    state.tree = await res.json();
  } catch {
    state.tree = [];
  }
  state.files = [];
  (function walk(items, dir) {
    for (const it of items) {
      if (it.children) walk(it.children, dir ? dir + "/" + it.name : it.name);
      else state.files.push({ rel: it.path, name: it.name, dir });
    }
  })(state.tree, "");
  revealInTree(state.current);
}

function highlight(text, q) {
  const i = q ? text.toLowerCase().indexOf(q) : -1;
  if (i < 0) return esc(text);
  return esc(text.slice(0, i)) + "<mark>" + esc(text.slice(i, i + q.length)) + "</mark>" + esc(text.slice(i + q.length));
}

function renderTree() {
  const q = $("filter").value.trim().toLowerCase();
  const kind = layout.kind;
  const shown = (it) => it.children
    ? it.children.some(shown)
    : (kind === "all" || kindOf(it.path) === kind) && (!q || it.path.toLowerCase().includes(q));
  const out = [];
  const walk = (items, depth, dir) => {
    for (const it of items) {
      if (!shown(it)) continue;
      if (it.children) {
        const key = dir ? dir + "/" + it.name : it.name;
        const open = q !== "" || state.open.has(key);
        out.push(`<div class="row dir${open ? " open" : ""}" style="--d:${depth}" data-dir="${esc(key)}" role="treeitem" aria-expanded="${open}" tabindex="-1">` +
          `${icon("chev", "chev")}${icon(open ? "folderOpen" : "folder", "ico")}<span class="name">${esc(it.name)}</span></div>`);
        if (open) walk(it.children, depth + 1, key);
      } else {
        out.push(`<div class="row file${isImage(it.path) ? " img" : ""}${it.path === state.current ? " cur" : ""}" style="--d:${depth}" data-rel="${esc(it.path)}" title="${esc(it.path)}" role="treeitem" tabindex="-1">` +
          `${icon(fileIcon(it.path), "ico")}<span class="name">${highlight(it.name, q)}</span></div>`);
      }
    }
  };
  walk(state.tree, 0, "");
  const what = { all: "文件或圖片", doc: ".md 文件", image: "圖片" }[kind];
  $("tree").innerHTML = out.join("") ||
    `<div class="none">${q ? "沒有符合的檔案" : `這個資料夾裡沒有${what}`}</div>`;
  for (const b of document.querySelectorAll("#kinds button")) b.classList.toggle("on", b.dataset.kind === kind);
}

$("kinds").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-kind]");
  if (!b) return;
  layout.kind = b.dataset.kind;
  applyLayout();
  renderTree();
});

function saveOpen() { store.set("open:" + cfg.root, [...state.open]); }

// Expands every folder on the way to `rel` (a file or folder path) and scrolls it into view.
function revealInTree(rel, isDir = false) {
  if (rel) {
    const parts = rel.split("/");
    for (let i = 1; i < parts.length + (isDir ? 1 : 0); i++) state.open.add(parts.slice(0, i).join("/"));
    saveOpen();
  }
  renderTree();
  const sel = rel && (isDir ? `[data-dir="${CSS.escape(rel)}"]` : ".row.cur");
  if (sel) $("tree").querySelector(sel)?.scrollIntoView({ block: "nearest" });
}

$("tree").addEventListener("click", (e) => {
  const row = e.target.closest(".row");
  if (!row) return;
  if (row.dataset.dir !== undefined) {
    const key = row.dataset.dir;
    if (state.open.has(key)) state.open.delete(key); else state.open.add(key);
    saveOpen();
    renderTree();
  } else {
    openDoc(row.dataset.rel, { newTab: e.ctrlKey });
  }
});
$("tree").addEventListener("auxclick", (e) => {
  const row = e.target.closest(".row.file");
  if (row && e.button === 1) { e.preventDefault(); openDoc(row.dataset.rel, { newTab: true }); }
});

$("filter").addEventListener("input", renderTree);
$("filter").addEventListener("keydown", (e) => {
  if (e.key === "Escape") { $("filter").value = ""; renderTree(); focusDoc(); e.stopPropagation(); }
  if (e.key === "Enter") openDoc($("tree").querySelector(".row.file")?.dataset.rel);
});

// ---------- navigation & history ----------

function saveScroll() {
  const entry = state.hist[state.at];
  if (entry && state.current) entry.scroll = readPosition();
}

// ---------- reading position ----------

// A position is the page's scroll offset in pixels: what the tab history and the host's reading history
// keep. In a big document the browser only estimates the parts off screen (doc.css), so the same offset
// lands elsewhere after reopening; there the block (paragraph, table, ...) at the top of the view and the
// distance past its top are noted under the offset too, and used to come back to the same line. That
// block is on screen, so its place is real, not estimated.
const anchors = store.get("anchors", {}); // "root|rel|offset" -> [index of the block in <main>, pixels past its top]
const anchorKey = (y) => `${cfg.root}|${state.current}|${y}`;

function readPosition() {
  const win = frame.contentWindow, doc = win.document, y = Math.round(win.scrollY);
  const blocks = isBigPage(doc) && doc.querySelector("main")?.children;
  if (blocks?.length) {
    const i = lastAbove(blocks.length, (k) => blocks[k], 0);
    anchors[anchorKey(y)] = [i, Math.round(-blocks[i].getBoundingClientRect().top)];
    for (const key of Object.keys(anchors).slice(0, -30)) delete anchors[key]; // the 30 latest
    store.set("anchors", anchors);
  }
  return y;
}

function applyPosition(win, y, frames = 4) {
  const a = isBigPage(win.document) ? anchors[anchorKey(y)] : null;
  const block = a && win.document.querySelector("main")?.children[a[0]];
  if (!block) { win.scrollTo(0, y); return; }
  block.scrollIntoView({ block: "start" });
  win.scrollBy(0, block.getBoundingClientRect().top + a[1]);
  // The blocks around it are laid out for real over the next frames and shift it: settle it again.
  if (frames > 1) win.requestAnimationFrame(() => applyPosition(win, y, frames - 1));
}

function load(rel, pending) {
  state.pending = pending;
  $("empty").hidden = true;
  frame.contentWindow.location.replace(urlOf(rel) + (pending.hash || ""));
}

// A new page in the active tab's back/forward list, dropping the pages ahead of the current one.
function pushHist(rel, scroll) {
  state.hist.splice(state.at + 1);
  state.hist.push({ rel, scroll });
  state.at = state.hist.length - 1;
}

// scroll: reading position to restore; find: { q, nth } to open the find bar on that match.
function navigate(rel, hash = "", scroll = 0, find = null) {
  if (!rel) return;
  saveScroll();
  pushHist(rel, scroll);
  load(rel, { hash, scroll, find });
}

// Every "open this document": a new tab on request; within the open document, only the heading or find
// changes; otherwise a page turn.
function openDoc(rel, { hash = "", scroll = 0, find = null, newTab = false } = {}) {
  if (!rel) return;
  if (newTab) { openInNewTab(rel, hash, find); return; }
  if (rel !== state.current) { navigate(rel, hash, scroll, find); return; }
  if (hash) scrollToHash(hash, true);
  if (find) openFind(find.q, find.nth);
}

// A file outside the open folder (or a path to look up): the host finds it, switches folders if needed and
// answers with "resolved", which opens it at `scroll`.
function requestOpen(text, scroll = 0) {
  pal.pendingId = requestId();
  pal.pendingScroll = scroll;
  post({ type: "resolve", text, id: pal.pendingId });
}

function go(delta) {
  const to = state.at + delta;
  if (to < 0 || to >= state.hist.length) return;
  saveScroll();
  state.at = to;
  load(state.hist[to].rel, { scroll: state.hist[to].scroll });
}

function reloadDoc() {
  if (!state.current) return;
  state.pending = { scroll: readPosition() };
  frame.contentWindow.location.reload();
}

function reloadAll() { loadTree(); reloadDoc(); }

function showEmpty() {
  state.current = null;
  state.headings = [];
  frame.contentWindow.location.replace("about:blank");
  $("empty").hidden = false;
  renderToc();
  updateChrome();
}

function scrollToHash(hash, smooth) {
  const doc = frame.contentDocument;
  const el = doc?.getElementById(decodeURIComponent(hash.replace(/^#/, "")));
  if (!el) return;
  el.scrollIntoView({ behavior: smooth && !reducedMotion ? "smooth" : "auto", block: "start" });
  el.classList.remove("flash");
  void el.offsetWidth;
  el.classList.add("flash");
}

frame.addEventListener("load", () => {
  const win = frame.contentWindow, doc = frame.contentDocument;
  if (!doc || win.location.href === "about:blank") return;
  const rel = relOf(win.location.pathname);
  if (state.hist[state.at]?.rel !== rel) pushHist(rel, 0); // safety net for a navigation we did not start
  state.current = rel;
  doc.documentElement.dataset.theme = document.documentElement.dataset.theme;
  if (!isImagePage(doc)) doc.body.style.zoom = layout.zoom;
  hookDoc(doc);
  collectHeadings(doc);
  renderToc(isImage(rel) ? "圖片沒有大綱" : undefined);

  const p = state.pending;
  state.pending = null;
  highlightCode(doc);
  addDocButtons(doc);
  const restore = () => {
    if (p?.hash) scrollToHash(p.hash, false);
    else if (p?.scroll) applyPosition(win, p.scroll);
  };
  restore();
  // Diagrams change the page height, so the position is restored again once they are drawn,
  // unless the reader has moved in the meantime.
  let moved = false;
  for (const type of ["wheel", "keydown", "pointerdown"]) doc.addEventListener(type, () => { moved = true; }, { once: true, passive: true });
  Promise.all([renderDiagrams(doc), renderMath(doc)]).then(([drawn, typeset]) => {
    if (doc !== frame.contentDocument) return;
    if ((drawn || typeset) && !moved) restore();
    if (p?.find) openFind(p.find.q, p.find.nth);
    else if (!$("findbar").hidden) runFind(-1);
  });

  post({ type: "opened", rel }); // the host remembers the folder and adds the file to the reading history
  updateChrome();
  revealInTree(rel);
});

// Keys, mouse back/forward buttons, file drops, toast dismissal and (unless an image page, which zooms
// the picture itself) Ctrl+wheel text zoom: the same for the app page and every document.
function listenTo(target, { zoomWheel = true } = {}) {
  target.addEventListener("keydown", onKey);
  target.addEventListener("mouseup", onMouseNav);
  target.addEventListener("dragenter", onDragEnter);
  target.addEventListener("dragover", (e) => { if (hasFiles(e)) e.preventDefault(); });
  for (const type of ["pointerdown", "wheel", "keydown"]) target.addEventListener(type, noteInteraction, { capture: true, passive: true });
  if (zoomWheel) target.addEventListener("wheel", onCtrlWheel, { passive: false });
}

// Where a link in a document points: { url, rel } with rel set for our own pages, or null for script links.
function linkTarget(a, doc) {
  const url = new URL(a.getAttribute("href"), doc.baseURI);
  if (url.protocol === "javascript:") return null; // never run, never hand to the shell
  return { url, rel: url.origin === location.origin ? relOf(url.pathname) : null };
}

function hookDoc(doc) {
  listenTo(doc, { zoomWheel: !isImagePage(doc) });
  doc.addEventListener("mousedown", closeMenus);
  doc.addEventListener("click", (e) => {
    const pic = e.target.closest("main img");
    if (pic && !e.target.closest("a")) { e.preventDefault(); openLightbox(doc, pic); return; }
    const a = e.target.closest("a[href]");
    if (!a || e.defaultPrevented) return;
    e.preventDefault();
    const link = linkTarget(a, doc);
    if (!link) return;
    if (link.rel === null) post({ type: "external", url: link.url.href });
    else openDoc(link.rel, { hash: link.url.hash, newTab: e.ctrlKey });
  });
  doc.addEventListener("auxclick", (e) => {
    const a = e.target.closest("a[href]");
    const link = a && e.button === 1 && linkTarget(a, doc);
    if (!link?.rel) return;
    e.preventDefault();
    openDoc(link.rel, { hash: link.url.hash, newTab: true });
  });
  let ticking = false, report = 0;
  doc.defaultView.addEventListener("scroll", () => {
    // The host keeps the last position of each file in the reading history.
    clearTimeout(report);
    report = setTimeout(() => {
      if (doc === frame.contentDocument && state.current && !isImage(state.current))
        post({ type: "scroll", rel: state.current, y: readPosition() });
    }, 700);
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(() => { ticking = false; updateActiveHeading(); });
  }, { passive: true });
}

function updateChrome() {
  renderTabs();
  saveTabs();
  $("back").disabled = state.at <= 0;
  $("forward").disabled = state.at >= state.hist.length - 1;
  renderCrumbs();
  const name = state.current ? baseName(state.current) : "";
  document.title = name || cfg.name;
  post({ type: "title", text: name ? `${name} — ${cfg.name}` : cfg.name });
}

function renderCrumbs() {
  const parts = state.current ? state.current.split("/") : [];
  let html = `<button data-dir="" title="${esc(cfg.root)}">${esc(cfg.name)}</button>`;
  parts.forEach((part, i) => {
    html += `<span class="sep">›</span>`;
    html += i === parts.length - 1
      ? `<span class="cur" title="${esc(state.current)}">${esc(part)}</span>`
      : `<button data-dir="${esc(parts.slice(0, i + 1).join("/"))}">${esc(part)}</button>`;
  });
  $("crumbs").innerHTML = html;
}

$("crumbs").addEventListener("click", (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  if (!layout.side || layout.zen) { layout.side = true; layout.zen = false; applyLayout(); }
  if (b.dataset.dir) revealInTree(b.dataset.dir, true);
  else $("tree").scrollTo({ top: 0 });
});

// ---------- outline ----------

function collectHeadings(doc) {
  let hs = [...doc.querySelectorAll("main h1[id], main h2[id], main h3[id]")];
  // A lone H1 is the document title; leave it out when there are section headings.
  if (hs.filter((h) => h.tagName === "H1").length === 1 && hs.length > 1) hs = hs.filter((h) => h.tagName !== "H1");
  const base = Math.min(...hs.map((h) => +h.tagName[1]));
  state.headings = hs.map((h) => ({ id: h.id, text: h.textContent.trim(), depth: +h.tagName[1] - base, el: h }));
}

let activeTocLink = null; // the outline entry marked as the current section

function renderToc(emptyText) {
  $("toc-list").innerHTML = state.headings.length
    ? state.headings.map((h) => `<li><a href="#${esc(h.id)}" data-id="${esc(h.id)}" style="--d:${h.depth}">${esc(h.text)}</a></li>`).join("")
    : `<li class="none">${emptyText ?? (state.current ? "這篇沒有小標題" : "尚未開啟文件")}</li>`;
  activeTocLink = null;
  updateActiveHeading();
}

// Of `count` elements in page order (element i is elAt(i)), the last whose top is at most `limit` px from
// the top of the view, else the first. Binary search: it runs on every scroll frame, and a long document
// has thousands of headings and blocks.
function lastAbove(count, elAt, limit) {
  let found = 0;
  for (let lo = 0, hi = count - 1; lo <= hi;) {
    const mid = (lo + hi) >> 1;
    if (elAt(mid).getBoundingClientRect().top <= limit) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

// Marks the current section in the outline, touching only the entries that change.
function updateActiveHeading() {
  const hs = state.headings;
  if (!hs.length) return;
  const win = frame.contentWindow;
  let active = lastAbove(hs.length, (i) => hs[i].el, 100);
  if (win.innerHeight + win.scrollY >= win.document.documentElement.scrollHeight - 4) active = hs.length - 1;
  const link = $("toc-list").children[active]?.firstElementChild;
  if (!link || link === activeTocLink) return;
  activeTocLink?.classList.remove("active");
  link.classList.add("active");
  link.scrollIntoView({ block: "nearest" });
  activeTocLink = link;
}

$("toc-list").addEventListener("click", (e) => {
  const a = e.target.closest("a");
  if (!a) return;
  e.preventDefault();
  scrollToHash(a.dataset.id, true);
});

// ---------- quick open ----------

// mode "files": file names, pasted paths, # headings; mode "text": the contents of every document.
const pal = { items: [], sel: 0, pendingId: null, pendingScroll: 0, mode: "files", note: "", searchId: null, timer: 0 };

// foot: [keys, what they do] (keys as in keysOf; without the text, the shortcut's own label).
const PALETTE_MODES = {
  files: {
    placeholder: "輸入檔名搜尋、貼上路徑，或用 # 跳到本篇標題",
    foot: [[["↑", "↓"], "選擇"], [["Enter"], "開啟"], [["#"], "本篇標題"], [["searchText"]]],
  },
  pick: {
    placeholder: "剪貼簿裡的檔案：輸入文字篩選",
    foot: [[["↑", "↓"], "選擇"], [["Enter"], "開啟"], [["openInTab"]]],
  },
  text: {
    placeholder: "搜尋所有文件的內文",
    foot: [[["↑", "↓"], "選擇"], [["Enter"], "開啟並跳到那一處"], [["palette"], "改搜檔名"]],
  },
};

function openPalette(prefix = "", mode = "files") {
  closeMenus();
  const input = $("palette-input");
  if (!$("palette").hidden && pal.mode === mode && !prefix) { input.select(); return; }
  pal.mode = mode;
  $("palette").hidden = false;
  $("palette").dataset.mode = mode;
  input.placeholder = PALETTE_MODES[mode].placeholder;
  $("palette-foot").innerHTML = PALETTE_MODES[mode].foot.map(([keys, text]) => `<span>${kbds(keysOf(keys))} ${esc(text ?? KEYS[keys[0]].label)}</span>`).join("");
  input.value = prefix;
  input.focus();
  updatePalette();
}

function closePalette() {
  $("palette").hidden = true;
  pal.pendingId = null;
  pal.searchId = null;
  clearTimeout(pal.timer);
  focusDoc();
}

const looksLikePath = (s) => /^["']?([a-z]:[\\/]|\\\\|file:)/i.test(s) || s.includes("\\");

function fuzzy(q, s) {
  const S = s.toLowerCase();
  let i = 0, score = 0, prev = -2;
  const idx = [];
  for (let j = 0; j < S.length && i < q.length; j++) {
    if (S[j] !== q[i]) continue;
    score += j === prev + 1 ? 6 : 1;
    if (j === 0 || "/\\-_. ".includes(S[j - 1])) score += 4;
    idx.push(j);
    prev = j;
    i++;
  }
  return i === q.length ? { score, idx } : null;
}

// Every space-separated term must match; hits in the file name score higher than in the folder.
function matchFile(f, q) {
  let score = 0, nameIdx = [];
  for (const term of q.toLowerCase().split(/\s+/).filter(Boolean)) {
    const n = fuzzy(term, f.name);
    if (n) { score += n.score + 12; nameIdx = nameIdx.concat(n.idx); continue; }
    const p = fuzzy(term, f.rel);
    if (!p) return null;
    score += p.score;
  }
  return { score: score - f.rel.length * 0.05, nameIdx };
}

const markIdx = (s, idx) => { const set = new Set(idx); return [...s].map((c, i) => (set.has(i) ? `<mark>${esc(c)}</mark>` : esc(c))).join(""); };

function updatePalette() {
  if (pal.mode === "text") { updateTextSearch(); return; }
  if (pal.mode === "pick") { updatePick(); return; }
  pal.note = "";
  const q = $("palette-input").value.trim();
  const items = [];
  if (q.startsWith("#")) {
    const t = q.slice(1).trim().toLowerCase();
    for (const h of state.headings)
      if (h.text.toLowerCase().includes(t)) items.push({ kind: "heading", id: h.id, icon: "hash", title: highlight(h.text, t), sub: "" });
  } else {
    if (q && looksLikePath(q)) items.push({ kind: "path", text: q, icon: "link", title: "開啟這個路徑", sub: esc(q.replace(/^["']|["']$/g, "")) });
    if (q) {
      state.files.map((f) => ({ f, m: matchFile(f, q) })).filter((x) => x.m)
        .sort((a, b) => b.m.score - a.m.score).slice(0, 60)
        .forEach(({ f, m }) => items.push({ kind: "file", rel: f.rel, icon: fileIcon(f.rel), title: markIdx(f.name, m.nameIdx), sub: esc(f.dir || cfg.name) }));
      if (!items.length) items.push({ kind: "path", text: q, icon: "link", title: "當作路徑開啟", sub: esc(q) });
    } else {
      for (const h of state.history)
        if (h.exists) { const l = histLabels(h); items.push({ kind: "hist", path: h.path, scroll: h.scroll, icon: "clock", title: esc(l.name), sub: esc(l.dir + " · " + l.ago) }); }
    }
  }
  pal.items = items;
  pal.sel = 0;
  renderPalette();
}

function renderPalette(message) {
  const list = $("palette-list");
  if (message) { list.innerHTML = message; return; }
  const q = $("palette-input").value.trim();
  const empty = pal.mode === "text"
    ? (q ? "沒有文件包含這段文字" : "輸入文字，搜尋這個資料夾裡所有文件的內容")
    : q.startsWith("#") ? "這篇沒有符合的標題" : q ? "沒有符合的檔案" : "輸入檔名開始搜尋，或貼上路徑";
  list.innerHTML = (pal.note ? `<li class="note">${pal.note}</li>` : "") + (pal.items.length
    ? pal.items.map((it, i) => `<li class="${it.kind}${i === pal.sel ? " sel" : ""}" data-i="${i}" role="option">${icon(it.icon, "ico")}<span class="t">${it.title}</span><span class="s">${it.sub}</span></li>`).join("")
    : `<li class="hint">${empty}</li>`);
  list.querySelector(".sel")?.scrollIntoView({ block: "nearest" });
}

// Several files from one copy: list just those, in the order they were copied (more: the copy names
// even more than the list holds).
function openPick(files, more = false) {
  pal.pick = files;
  pal.pickMore = more;
  openPalette("", "pick");
  updatePalette();
}

function updatePick() {
  const q = $("palette-input").value.trim().toLowerCase();
  pal.note = pal.pickMore ? `剪貼簿裡的檔案超過 ${pal.pick.length} 個，只列出前 ${pal.pick.length} 個` : `剪貼簿裡有 ${pal.pick.length} 個檔案`;
  pal.items = pal.pick.filter((f) => !q || (f.rel ?? f.path).toLowerCase().includes(q))
    .map((f) => ({ kind: "pick", rel: f.rel, path: f.path, icon: fileIcon(f.path), title: esc(nameOf(f.path)), sub: esc(dirLabel(f.path)) }));
  pal.sel = 0;
  renderPalette();
}

// A file from the clipboard: under the root it opens directly, elsewhere the host switches folders first.
function openClipFile(f, newTab = false, hash = "") {
  if (f.rel) openDoc(f.rel, { hash, newTab });
  else requestOpen(f.path + hash);
}

// newTab: Ctrl+Enter or Ctrl+click opens the file in a new tab.
function choose(it, newTab = false) {
  if (!it) return;
  if (it.kind === "path") { renderPalette(`<li class="hint">開啟中…</li>`); requestOpen(it.text); return; } // the palette shows the answer
  closePalette();
  if (it.kind === "pick") openClipFile(it, newTab);
  else if (it.kind === "file") openDoc(it.rel, { newTab });
  else if (it.kind === "heading") scrollToHash(it.id, true);
  else if (it.kind === "hist") openHistory(it.path, it.scroll);
  else if (it.kind === "hit") openDoc(it.rel, { find: { q: it.q, nth: it.nth }, newTab });
}

function onResolved(m) {
  if (m.id !== pal.pendingId) return;
  pal.pendingId = null;
  if (m.error) {
    if ($("palette").hidden) toast(m.error); else renderPalette(`<li class="err">${esc(m.error)}</li>`);
    return;
  }
  if (!$("palette").hidden) closePalette();
  openDoc(m.rel, { hash: m.hash ?? "", scroll: pal.pendingScroll });
}

// ---------- tabs ----------

// Writes the active tab's back/forward list into its slot.
function syncTab() {
  const t = state.tabs[state.tab];
  if (t) { t.hist = state.hist; t.at = state.at; }
}

// Before another tab becomes active: the reading position too.
function stashTab() {
  saveScroll();
  syncTab();
}

// A new, empty tab right after the active one, made active.
function insertTab() {
  stashTab();
  state.tabs.splice(state.tab + 1, 0, emptyTab());
  activateTab(state.tab + 1);
}

function activateTab(i) {
  state.tab = i;
  state.hist = state.tabs[i].hist;
  state.at = state.tabs[i].at;
}

function openActiveTab() {
  const entry = state.hist[state.at];
  if (entry) load(entry.rel, { scroll: entry.scroll }); else showEmpty();
  updateChrome();
}

function switchTab(i) {
  const n = state.tabs.length;
  if (n < 2) return;
  i = ((i % n) + n) % n;
  if (i === state.tab) return;
  stashTab();
  activateTab(i);
  openActiveTab();
}

function openInNewTab(rel, hash = "", find = null) {
  if (!rel) return;
  insertTab();
  navigate(rel, hash, 0, find);
  renderTabs();
}

// Ctrl+T: an empty tab with quick open ready.
function newTab() {
  insertTab();
  showEmpty();
  openPalette();
}

function closeTab(i) {
  if (state.tabs.length === 1) {
    state.tabs[0] = emptyTab();
    activateTab(0);
    showEmpty();
    return;
  }
  const wasActive = i === state.tab;
  if (!wasActive) stashTab();
  state.tabs.splice(i, 1);
  if (wasActive) {
    activateTab(Math.min(i, state.tabs.length - 1));
    openActiveTab();
  } else {
    if (i < state.tab) state.tab--;
    renderTabs();
    saveTabs();
  }
}

// The active tab may be blank for a moment (switching folders, starting up) while its page is still set.
const tabRel = (t, i) => (i === state.tab ? state.current ?? state.hist[state.at]?.rel : t.hist[t.at]?.rel);

// The strip appears only with two or more tabs.
function renderTabs() {
  const strip = $("tabs"), many = state.tabs.length > 1;
  body.classList.toggle("has-tabs", many);
  strip.hidden = !many;
  if (!many) return;
  strip.innerHTML = state.tabs.map((t, i) => {
    const rel = tabRel(t, i);
    return `<div class="tab${i === state.tab ? " on" : ""}" data-i="${i}" title="${esc(rel ?? KEYS.newTab.label)}" role="tab">` +
      `${icon(fileIcon(rel ?? ""), "ico")}<span>${esc(rel ? baseName(rel) : KEYS.newTab.label)}</span>` +
      `<button class="x" data-close="${i}" title="${esc(withKey("closeTab"))}">${icon("close")}</button></div>`;
  }).join("") + `<button class="tab-new" title="${esc(withKey("newTab"))}">${icon("plus")}</button>`;
  strip.querySelector(".tab.on")?.scrollIntoView({ block: "nearest", inline: "nearest" });
}

// Each folder remembers its open tabs (the page each one shows).
function saveTabs() {
  syncTab();
  const list = state.tabs.map((t, i) => tabRel(t, i) ?? "");
  store.set("tabs:" + cfg.root, { list, active: state.tab });
}

function restoreTabs() {
  const saved = store.get("tabs:" + cfg.root, null);
  const list = saved?.list?.length ? saved.list : [""];
  state.tabs = list.map((rel) => (rel ? { hist: [{ rel, scroll: 0 }], at: 0 } : emptyTab()));
  activateTab(Math.min(Math.max(saved?.active ?? 0, 0), state.tabs.length - 1));
}

$("tabs").addEventListener("click", (e) => {
  if (e.target.closest(".tab-new")) { newTab(); return; }
  const close = e.target.closest("[data-close]");
  if (close) { closeTab(+close.dataset.close); return; }
  const tab = e.target.closest(".tab");
  if (tab) switchTab(+tab.dataset.i);
});
$("tabs").addEventListener("auxclick", (e) => {
  const tab = e.target.closest(".tab");
  if (tab && e.button === 1) { e.preventDefault(); closeTab(+tab.dataset.i); }
});

// ---------- math ----------

// Big libraries load only when a document needs them, once: KaTeX for math, Mermaid (2.5 MB) for diagrams.
const scripts = new Map();
function loadScript(src, global, name) {
  if (!scripts.has(src)) scripts.set(src, new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = src;
    script.onload = () => resolve(window[global]);
    script.onerror = () => { scripts.delete(src); reject(new Error("無法載入 " + name)); };
    document.head.append(script);
  }));
  return scripts.get(src);
}

async function renderMath(doc) {
  const blocks = doc?.body ? [...doc.querySelectorAll("main .math:not(.typeset)")] : [];
  if (!blocks.length) return false;
  if (!doc.getElementById("katex-css")) {
    const link = doc.createElement("link");
    link.id = "katex-css";
    link.rel = "stylesheet";
    link.href = "/__app/katex/katex.min.css";
    doc.head.append(link);
  }
  let katex;
  try { katex = await loadScript("katex/katex.min.js", "katex", "KaTeX"); } catch { return false; }
  for (const el of blocks) {
    const tex = el.textContent.trim().replace(/^\\[([]/, "").replace(/\\[)\]]$/, "").trim();
    katex.render(tex, el, { displayMode: el.tagName === "DIV", throwOnError: false });
    el.classList.add("typeset");
  }
  finder.index = null; // the text changed
  return true;
}

// ---------- help ----------

// [keys, what they do]: keys are names in KEYS (shown as their keys) or plain text for mouse actions.
const HELP = [
  ["開啟與搜尋", [
    [["palette"], "快速開啟：輸入檔名、貼上路徑，或用 # 跳到本篇標題"],
    [["searchText"], "搜尋這個資料夾所有文件的內文"],
    [["find"], "在本篇中尋找（Enter / Shift+Enter 或 F3 切換）"],
    [["history"], `閱讀紀錄（最近 ${cfg.maxHistory} 個檔案，回到上次讀到的位置）`],
    [["openFile", "openFolder"], "開啟檔案 · 開啟資料夾"],
    [["拖曳"], "把檔案或資料夾拖到視窗上就打開"],
  ]],
  ["分頁與瀏覽", [
    [["newTab", "closeTab"], "新分頁 · 關閉分頁（也可以用滑鼠中鍵點分頁）"],
    [["nextTab"], "下一個分頁（加 Shift 是上一個）"],
    [["Ctrl+點", "中鍵"], "在新分頁開啟連結或檔案；快速開啟裡用 Ctrl+Enter"],
    [["back", "forward"], "上一頁 · 下一頁（滑鼠側鍵也可以）"],
    [["reload"], "重新整理"],
  ]],
  ["版面", [
    [["side", "toc"], "檔案側欄 · 本篇大綱"],
    [["zen"], "專注閱讀（隱藏所有面板；移到視窗邊緣會暫時滑出來）"],
    [["zoomIn", "zoomOut", "zoomReset"], "放大 · 縮小 · 恢復文字大小（也可以 Ctrl+滾輪）"],
    [["dark"], "亮色 / 暗色切換；調色盤按鈕可以換主題"],
    [["拖曳邊線"], "調整側欄和大綱寬度，雙擊恢復"],
  ]],
  ["文件裡", [
    [["點圖片"], "放大檢視：滾輪縮放、拖曳移動、← → 切換"],
    [["程式碼右上角"], "複製程式碼"],
    [["標題左邊的 🔗"], "複製「檔案路徑#標題」，貼回來會直接跳到那一節"],
    [["更多選單"], "匯出 PDF、複製檔案路徑、在檔案總管顯示"],
  ]],
  ["剪貼簿與背景", [
    [["複製路徑"], "剪貼簿出現 .md 或圖片路徑（含檔名、相對路徑）時自動開啟；路徑夾在一大段文字裡只提示，一次複製多個檔案會列出來讓你選。在「更多 → 剪貼簿設定」調整"],
    [["✕ 關閉"], "縮到右下角系統匣，繼續看剪貼簿；要結束請在系統匣圖示按右鍵 →「結束」"],
    [["系統匣選單"], "最近讀過的檔案、開機時在背景啟動"],
  ]],
];

// ---------- panels: shortcuts, clipboard settings ----------

// One panel at a time over the page; its ✕, Esc or a click beside it closes it.
function openDialog(id) {
  closeMenus();
  $(id).hidden = false;
  $(id).querySelector(".help-head button").focus();
}

function closeDialog() {
  const open = document.querySelector(".help:not([hidden])");
  if (!open) return false;
  open.hidden = true;
  focusDoc();
  return true;
}

for (const panel of document.querySelectorAll(".help")) {
  panel.querySelector(".help-head button").onclick = closeDialog;
  panel.addEventListener("mousedown", (e) => { if (e.target === panel) closeDialog(); });
}

function openHelp() {
  $("help-body").innerHTML = HELP.map(([group, rows]) =>
    `<section><h3>${group}</h3><dl>${rows.map(([keys, text]) => `<dt>${kbds(keysOf(keys))}</dt><dd>${esc(text)}</dd>`).join("")}</dl></section>`).join("");
  openDialog("help");
}

function setClipMode(mode) {
  layout.clip = mode;
  saveLayout();
  post({ type: "clipMode", mode });
}

// The choices and ranges come from the host (Settings), which also checks what is typed and answers with
// what it kept ("clipOptions").
const radios = (name, choices) => choices.map((c) => `<label><input type="radio" name="${name}" value="${c.id}">${esc(c.label)}</label>`).join("");
$("clip-modes").outerHTML = radios("mode", cfg.clipModes);
$("clip-multi").outerHTML = radios("multi", cfg.clipMultiModes);
$("clipset-form").elements.max.min = cfg.clipMaxRange[0];
$("clipset-form").elements.max.max = cfg.clipMaxRange[1];

function showClipOptions() {
  const f = $("clipset-form").elements;
  f.mode.value = layout.clip;
  f.chars.value = clipOpts.chars;
  f.multi.value = clipOpts.multi;
  f.max.value = clipOpts.max;
}

function openClipSettings() {
  showClipOptions();
  openDialog("clipset");
}

$("clipset-form").addEventListener("submit", (e) => e.preventDefault());
$("clipset-form").addEventListener("change", (e) => {
  const f = $("clipset-form").elements;
  if (e.target.name === "mode") { setClipMode(f.mode.value); return; }
  Object.assign(clipOpts, { chars: Number(f.chars.value), multi: f.multi.value, max: Number(f.max.value) });
  post({ type: "clipOptions", ...clipOpts });
});

// ---------- full-text search ----------

function updateTextSearch() {
  clearTimeout(pal.timer);
  const q = $("palette-input").value;
  pal.items = [];
  pal.note = "";
  pal.searchId = null;
  if (!q.trim()) { renderPalette(); return; }
  renderPalette(`<li class="hint">搜尋中…</li>`);
  pal.timer = setTimeout(() => {
    pal.searchId = requestId();
    post({ type: "search", q, id: pal.searchId });
  }, 220);
}

function onSearchResults(m) {
  if (m.id !== pal.searchId || $("palette").hidden) return;
  const q = $("palette-input").value;
  pal.items = m.hits.map((h) => ({
    kind: "hit", rel: h.rel, nth: h.nth, q, icon: "file",
    title: `${esc(h.snippet.before)}<mark>${esc(h.snippet.match)}</mark>${esc(h.snippet.after)}`,
    sub: esc(`${h.rel}:${h.line}`),
  }));
  pal.note = m.total ? `${m.total} 個結果，在 ${m.files} 份文件裡${m.total > m.hits.length ? `（只列出前 ${m.hits.length} 個）` : ""}` : "";
  pal.sel = 0;
  renderPalette();
}

// ---------- find in page ----------

// Matches are painted with the CSS Custom Highlight API, so the document's DOM is never changed.
// index: the document's text and where each text node starts in it, built once per document (and again
// after diagrams or math change the text), not on every keystroke.
const finder = { ranges: [], i: -1, timer: 0, index: null };

function textIndex(doc, main) {
  if (finder.index?.doc === doc) return finder.index;
  const nodes = [];
  let text = "";
  const walker = doc.createTreeWalker(main, NodeFilter.SHOW_TEXT, {
    acceptNode: (n) => (n.parentElement.closest("style, script, svg, .katex-mathml") ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
  });
  for (let n; (n = walker.nextNode()); ) { nodes.push({ n, start: text.length }); text += n.data; }
  return (finder.index = { doc, nodes, hay: text.toLowerCase() });
}

function openFind(q, nth) {
  $("findbar").hidden = false;
  const input = $("find-input");
  if (q != null) input.value = q;
  else {
    const sel = frame.contentWindow?.getSelection()?.toString().trim();
    if (sel && sel.length < 200 && !sel.includes("\n")) input.value = sel;
    input.focus();
    input.select();
  }
  runFind(nth ?? 0);
}

function closeFind() {
  $("findbar").hidden = true;
  clearFind();
  focusDoc();
}

function clearFind() {
  const hl = frame.contentWindow?.CSS?.highlights;
  hl?.delete("find");
  hl?.delete("find-cur");
  finder.ranges = [];
  finder.i = -1;
}

// Finds every match of the query (case-insensitive, across inline formatting) and moves to match nth;
// nth -1 only paints them, for when the page reloads under an open find bar.
function runFind(nth = 0) {
  clearFind();
  const q = $("find-input").value.toLowerCase();
  const doc = frame.contentDocument, win = frame.contentWindow;
  const main = doc?.querySelector("main");
  if (q && main && !isImagePage(doc) && win.Highlight) {
    const { nodes, hay } = textIndex(doc, main);
    // The node holding character `off`; an end offset may sit at the very end of the previous node.
    const at = (off, isEnd) => {
      let lo = 0, hi = nodes.length - 1;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (isEnd ? nodes[mid].start < off : nodes[mid].start <= off) lo = mid; else hi = mid - 1;
      }
      return [nodes[lo].n, off - nodes[lo].start];
    };
    for (let i = hay.indexOf(q); i >= 0 && finder.ranges.length < 5000; i = hay.indexOf(q, i + q.length)) {
      const r = doc.createRange();
      r.setStart(...at(i, false));
      r.setEnd(...at(i + q.length, true));
      finder.ranges.push(r);
    }
    if (finder.ranges.length) win.CSS.highlights.set("find", new win.Highlight(...finder.ranges));
  }
  if (nth >= 0 && finder.ranges.length) gotoFind(Math.min(nth, finder.ranges.length - 1));
  else updateFindCount();
}

function gotoFind(i) {
  const n = finder.ranges.length;
  if (!n) return;
  finder.i = ((i % n) + n) % n;
  const r = finder.ranges[finder.i], win = frame.contentWindow;
  win.CSS.highlights.set("find-cur", new win.Highlight(r));
  const box = r.getBoundingClientRect();
  if (box.top < 70 || box.bottom > win.innerHeight - 50)
    r.startContainer.parentElement.scrollIntoView({ block: "center", behavior: reducedMotion ? "auto" : "smooth" });
  updateFindCount();
}

function updateFindCount() {
  const n = finder.ranges.length, q = $("find-input").value;
  $("find-count").textContent = !q ? "" : !n ? "沒有結果" : finder.i < 0 ? `${n} 個` : `${finder.i + 1} / ${n}`;
  $("findbar").classList.toggle("none", !!q && !n);
}

$("find-input").addEventListener("input", () => {
  clearTimeout(finder.timer);
  finder.timer = setTimeout(() => runFind(0), 120);
});
$("find-input").addEventListener("keydown", (e) => {
  if (e.key === "Enter") gotoFind(finder.i + (e.shiftKey ? -1 : 1));
  else if (e.key === "Escape") closeFind();
  else return;
  e.preventDefault();
  e.stopPropagation();
});
$("find-prev").onclick = () => gotoFind(finder.i - 1);
$("find-next").onclick = () => gotoFind(finder.i + 1);
$("find-close").onclick = closeFind;

// ---------- code colours and diagrams ----------

function highlightCode(doc) {
  if (!window.hljs || isImagePage(doc)) return;
  for (const code of doc.querySelectorAll("pre > code[class*='language-']")) {
    const lang = /language-([\w+#-]+)/.exec(code.className)?.[1].toLowerCase();
    if (!lang || lang === "mermaid" || !hljs.getLanguage(lang)) continue;
    code.innerHTML = hljs.highlight(code.textContent, { language: lang, ignoreIllegals: true }).value;
    code.classList.add("hljs");
  }
}

let diagramSeq = 0;

// Diagram colours come from the document's own theme variables.
// Copy buttons on code blocks and a link button beside each heading.
function addDocButtons(doc) {
  if (isImagePage(doc)) return;
  const button = (cls, title, ic) => {
    const b = doc.createElement("button");
    b.type = "button";
    b.className = cls;
    b.title = title;
    b.innerHTML = icon(ic);
    return b;
  };
  for (const pre of doc.querySelectorAll("main pre")) {
    if (pre.closest(".mermaid")) continue;
    const b = button("copy-code", "複製程式碼", "copy");
    b.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      copyText((pre.querySelector("code") ?? pre).textContent.replace(/\n$/, ""));
      b.innerHTML = icon("check");
      b.classList.add("done");
      setTimeout(() => { b.innerHTML = icon("copy"); b.classList.remove("done"); }, 1500);
    });
    pre.append(b);
  }
  for (const h of doc.querySelectorAll("main :is(h1, h2, h3, h4)[id]")) {
    const b = button("h-anchor", "複製這一節的連結（貼回來會直接跳到這裡）", "link");
    b.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      const link = fullPath(state.current) + "#" + h.id;
      copyText(link);
      toast("已複製這一節的連結：" + link);
    });
    h.prepend(b);
  }
}

// Through the host, which also keeps our own copies from being picked up as clipboard paths.
const copyText = (text) => post({ type: "copy", text });

// ---------- drag and drop ----------

const hasFiles = (e) => [...(e.dataTransfer?.types ?? [])].includes("Files");

// The overlay covers the document too, so the drop always lands in this page.
function onDragEnter(e) {
  if (!hasFiles(e)) return;
  e.preventDefault();
  $("drop").hidden = false;
}

$("drop").addEventListener("dragover", (e) => { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; });
$("drop").addEventListener("dragleave", (e) => { if (!$("drop").contains(e.relatedTarget)) $("drop").hidden = true; });
$("drop").addEventListener("drop", (e) => {
  e.preventDefault();
  $("drop").hidden = true;
  if (e.dataTransfer.files.length) chrome.webview.postMessageWithAdditionalObjects({ type: "drop" }, e.dataTransfer.files);
});

// ---------- PDF ----------

// Sends the document as it looks now (code colours, drawn diagrams), in a light theme when a dark
// one is active, without the hover buttons and the text zoom.
async function exportPdf() {
  const doc = frame.contentDocument;
  if (!state.current || isImage(state.current) || !doc?.body) return;
  const now = resolveTheme(state.theme), paper = now.dark ? resolveTheme(state.lastLight) : now;
  if (paper.id !== now.id) { doc.documentElement.dataset.theme = paper.id; await renderDiagrams(doc); }
  const page = doc.documentElement.cloneNode(true);
  if (paper.id !== now.id) { doc.documentElement.dataset.theme = now.id; renderDiagrams(doc); }
  page.querySelectorAll(".flash").forEach((el) => el.classList.remove("flash"));
  page.querySelector("body").style.zoom = "";
  page.querySelectorAll(".frontmatter").forEach((el) => { el.open = true; });
  page.querySelectorAll("img").forEach((img, i) => img.setAttribute("src", doc.images[i]?.src ?? img.src));
  toast("正在匯出 PDF…");
  post({ type: "exportPdf", rel: state.current, html: "<!doctype html>" + page.outerHTML });
}

function onExported(m) {
  if (m.ok) toast("已匯出 PDF：" + m.path, { label: "開啟", run: () => post({ type: "openPdf", path: m.path }) }, 10000);
  else toast("匯出 PDF 失敗，請看 error.log");
}

function mermaidConfig(doc) {
  const css = doc.defaultView.getComputedStyle(doc.documentElement);
  const v = (name) => css.getPropertyValue("--" + name).trim();
  return {
    startOnLoad: false,
    securityLevel: "strict",
    theme: "base",
    themeVariables: {
      darkMode: (themeById[doc.documentElement.dataset.theme] ?? resolveTheme(state.theme)).dark,
      background: v("bg"), fontFamily: doc.defaultView.getComputedStyle(doc.body).fontFamily, fontSize: "15px",
      primaryColor: v("accent-soft"), primaryBorderColor: v("accent"), primaryTextColor: v("fg"),
      secondaryColor: v("code-bg"), tertiaryColor: v("pre-bg"), lineColor: v("muted"), textColor: v("fg"),
      edgeLabelBackground: v("bg"), noteBkgColor: v("quote-bg"), noteBorderColor: v("quote-bar"), noteTextColor: v("fg"),
    },
  };
}

// Turns ```mermaid blocks into drawn diagrams (again, after a theme change). Resolves true when it drew any.
async function renderDiagrams(doc) {
  if (!doc?.body) return false;
  for (const el of doc.querySelectorAll("pre > code.language-mermaid, pre.mermaid, div.mermaid:not(.figure)")) {
    const fig = doc.createElement("div");
    fig.className = "mermaid figure";
    fig.dataset.src = el.textContent;
    (el.closest("pre") ?? el).replaceWith(fig);
  }
  const figs = [...doc.querySelectorAll(".mermaid.figure")];
  if (!figs.length) return false;
  let mermaid;
  try { mermaid = await loadScript("mermaid.min.js", "mermaid", "Mermaid"); } catch (err) { figs.forEach((f) => showDiagramError(f, err)); return false; }
  mermaid.initialize(mermaidConfig(doc));
  for (const fig of figs) {
    const id = "mmd" + ++diagramSeq;
    try {
      const { svg } = await mermaid.render(id, fig.dataset.src);
      fig.classList.remove("error");
      fig.innerHTML = svg;
    } catch (err) {
      showDiagramError(fig, err);
    } finally {
      document.getElementById("d" + id)?.remove(); // mermaid leaves its scratch element behind on errors
    }
  }
  finder.index = null; // the text changed
  return true;
}

function showDiagramError(fig, err) {
  fig.classList.add("error");
  fig.innerHTML = `<div class="msg">這張 Mermaid 圖畫不出來：${esc(err?.message ?? err)}</div><pre>${esc(fig.dataset.src)}</pre>`;
}

$("palette-input").addEventListener("input", updatePalette);
$("palette-input").addEventListener("keydown", (e) => {
  const n = pal.items.length;
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    if (n) { pal.sel = (pal.sel + (e.key === "ArrowDown" ? 1 : -1) + n) % n; renderPalette(); }
  } else if (e.key === "Enter") {
    e.preventDefault();
    choose(pal.items[pal.sel], e.ctrlKey);
  } else if (e.key === "Escape") {
    e.preventDefault();
    e.stopPropagation();
    closePalette();
  }
});
$("palette-list").addEventListener("click", (e) => {
  const li = e.target.closest("li[data-i]");
  if (li) choose(pal.items[+li.dataset.i], e.ctrlKey);
});
$("palette").addEventListener("mousedown", (e) => { if (e.target === $("palette")) closePalette(); });

// ---------- menus ----------

function showMenu(menu, anchor) {
  const open = !menu.hidden;
  closeMenus();
  if (open) return;
  menu.hidden = false;
  const r = anchor.getBoundingClientRect();
  menu.style.top = r.bottom + 6 + "px";
  menu.style.left = Math.max(8, Math.min(r.right - menu.offsetWidth, innerWidth - menu.offsetWidth - 8)) + "px";
  menu.querySelector("button")?.focus();
}

function closeMenus() {
  let closed = false;
  for (const m of document.querySelectorAll(".menu")) {
    if (!m.hidden) { m.hidden = true; closed = true; }
  }
  setThemeAttr(resolveTheme(state.theme).id); // drop any hover preview
  return closed;
}

function openThemeMenu() {
  const check = (id) => (state.theme === id ? `<span class="tail">${icon("check")}</span>` : "");
  const row = (t) => `<button data-theme="${t.id}"><span class="sw"><i style="background:${t.bg}"></i><i style="background:${t.accent}"></i><i style="background:${t.h3}"></i></span><span>${esc(t.name)}</span>${check(t.id)}</button>`;
  const menu = $("theme-menu");
  menu.innerHTML =
    `<button data-theme="${cfg.autoTheme}"><span class="sw"><i style="background:${themeById[cfg.autoLight].bg}"></i><i style="background:${themeById[cfg.autoDark].bg}"></i></span><span>跟隨系統</span>${check(cfg.autoTheme)}</button>` +
    `<div class="label">亮色</div>${cfg.themes.filter((t) => !t.dark).map(row).join("")}` +
    `<div class="label">暗色</div>${cfg.themes.filter((t) => t.dark).map(row).join("")}`;
  showMenu(menu, $("theme"));
}

$("theme-menu").addEventListener("mouseover", (e) => {
  const b = e.target.closest("button[data-theme]");
  if (b) setThemeAttr(resolveTheme(b.dataset.theme).id);
});
$("theme-menu").addEventListener("mouseleave", () => setThemeAttr(resolveTheme(state.theme).id));
$("theme-menu").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-theme]");
  if (!b) return;
  applyTheme(b.dataset.theme);
  closeMenus();
});

// ---------- reading history ----------

// A reading-history entry as lists show it: file name, where it lives, how long ago.
const histLabels = (h) => ({ name: nameOf(h.path), dir: dirLabel(h.path), ago: ago(h.when) });

function ago(when) {
  const min = Math.floor((Date.now() - when) / 60000);
  if (min < 1) return "剛剛";
  if (min < 60) return min + " 分鐘前";
  if (min < 24 * 60) return Math.floor(min / 60) + " 小時前";
  const d = new Date(when), days = Math.floor(min / 1440);
  if (days < 7) return days === 1 ? "昨天" : days + " 天前";
  return `${d.getMonth() + 1}/${d.getDate()}`;
}

// Opens at the last reading position; files outside the open folder go through the host.
function openHistory(path, scroll = 0) {
  const rel = relUnderRoot(path);
  if (rel) navigate(rel, "", scroll); else requestOpen(path, scroll);
}

function openHistoryMenu() {
  const menu = $("history-menu");
  const current = state.current && fullPath(state.current).toLowerCase();
  const rows = state.history.map((h, i) => {
    const cur = h.path.toLowerCase() === current, l = histLabels(h);
    return `<button data-hist="${i}" class="hist${cur ? " cur" : ""}"${h.exists ? "" : " disabled"} title="${esc(h.path)}">` +
      `${icon(fileIcon(h.path))}<span class="hn"><span class="t">${esc(l.name)}</span><span class="s">${esc(h.exists ? l.dir : "檔案已不存在")}</span></span>` +
      `<span class="tail">${l.ago}</span></button>`;
  });
  menu.innerHTML = `<div class="label">最近讀過的檔案（最多 ${cfg.maxHistory} 個）</div>` +
    (rows.length ? rows.join("") + `<hr><button data-hist="clear">${icon("close")}<span>清除紀錄</span></button>` : `<div class="empty">還沒有讀過的檔案</div>`);
  showMenu(menu, $("history"));
}

$("history-menu").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-hist]");
  if (!b) return;
  closeMenus();
  if (b.dataset.hist === "clear") post({ type: "clearHistory" });
  else { const h = state.history[+b.dataset.hist]; openHistory(h.path, h.scroll); }
});

function openMoreMenu() {
  const has = !!state.current;
  const item = (act, ic, label, kb = "", enabled = true) =>
    `<button data-act="${act}"${enabled ? "" : " disabled"}>${icon(ic)}<span>${label}</span>${kb ? `<span class="tail">${kb}</span>` : ""}</button>`;
  $("more-menu").innerHTML =
    item("file", "file", "開啟檔案…", keyOf("openFile")) +
    item("folder", "folderOpen", "開啟資料夾…", keyOf("openFolder")) +
    "<hr>" +
    item("copy", "copy", "複製檔案路徑", "", has) +
    item("reveal", "explorer", "在檔案總管顯示", "", has) +
    item("pdf", "file", "匯出 PDF…", "", has && !isImage(state.current)) +
    "<hr>" +
    item("reload", "reload", KEYS.reload.label, keyOf("reload")) +
    item("help", "keyboard", KEYS.help.label, keyOf("help")) +
    `<div class="label">${esc(cfg.clipModesTitle)}</div>` +
    cfg.clipModes
      .map((m) => `<button data-clip="${m.id}">${icon("copy")}<span>${esc(m.label)}</span>${layout.clip === m.id ? `<span class="tail">${icon("check")}</span>` : ""}</button>`)
      .join("") +
    item("clipset", "keyboard", "剪貼簿設定…");
  showMenu($("more-menu"), $("more"));
}

$("more-menu").addEventListener("click", (e) => {
  const clip = e.target.closest("button[data-clip]");
  if (clip) { setClipMode(clip.dataset.clip); closeMenus(); return; }
  const b = e.target.closest("button[data-act]");
  if (!b) return;
  closeMenus();
  const act = b.dataset.act;
  if (act === "file") KEYS.openFile.run();
  else if (act === "folder") KEYS.openFolder.run();
  else if (act === "reveal") post({ type: "reveal", rel: state.current });
  else if (act === "reload") reloadAll();
  else if (act === "pdf") exportPdf();
  else if (act === "help") openHelp();
  else if (act === "clipset") openClipSettings();
  else if (act === "copy") {
    copyText(fullPath(state.current));
    toast("已複製：" + fullPath(state.current));
  }
});

document.addEventListener("mousedown", (e) => { if (!e.target.closest(".menu, #theme, #more, #history")) closeMenus(); });
addEventListener("blur", () => setTimeout(() => { if (document.activeElement === frame) closeMenus(); }));

// ---------- lightbox for images inside a document ----------

const lb = { imgs: [], i: 0, viewer: null };

function openLightbox(doc, pic) {
  closeMenus();
  lb.imgs = [...doc.querySelectorAll("main img")];
  lb.i = Math.max(0, lb.imgs.indexOf(pic));
  $("lightbox").hidden = false;
  showLightboxImage();
}

function showLightboxImage() {
  const pic = lb.imgs[lb.i];
  const src = pic.currentSrc || pic.src;
  const name = pic.alt || decodeURIComponent(src.split("?")[0].split("/").pop());
  const many = lb.imgs.length > 1;
  lb.viewer?.destroy();
  $("lightbox").innerHTML =
    `<div class="zoom-stage" id="lb-stage"><img alt=""></div>` +
    `<button class="icon lb-close" title="${esc(withKey("close"))}">${icon("close")}</button>` +
    (many ? `<button class="icon lb-prev" title="上一張 (←)">${icon("back")}</button><button class="icon lb-next" title="下一張 (→)">${icon("forward")}</button>` : "");
  const img = $("lightbox").querySelector("img");
  img.src = src;
  lb.viewer = mdZoom($("lb-stage"), img, { label: many ? `${name}　${lb.i + 1} / ${lb.imgs.length}` : name });
}

function stepLightbox(delta) {
  if (lb.imgs.length < 2) return;
  lb.i = (lb.i + delta + lb.imgs.length) % lb.imgs.length;
  showLightboxImage();
}

function closeLightbox() {
  lb.viewer?.destroy();
  lb.viewer = null;
  $("lightbox").hidden = true;
  $("lightbox").innerHTML = "";
  focusDoc();
}

$("lightbox").addEventListener("click", (e) => {
  if (e.target.closest(".lb-close")) closeLightbox();
  else if (e.target.closest(".lb-prev")) stepLightbox(-1);
  else if (e.target.closest(".lb-next")) stepLightbox(1);
});

// Returns true when the lightbox is open and consumed the key.
function lightboxKey(e) {
  if ($("lightbox").hidden) return false;
  if (e.key === "Escape") closeLightbox();
  else if (e.key === "ArrowLeft" && !e.altKey) stepLightbox(-1);
  else if (e.key === "ArrowRight" && !e.altKey) stepLightbox(1);
  else if (!lb.viewer?.key(e)) return false;
  return true;
}

// ---------- toast ----------

let toastTimer;
let toastUntilInteraction = false;
// action: optional { label, run } rendered as a button. ms: how long it stays (hovering pauses it), or
// "interact" to keep it until the user next clicks, scrolls or types in the viewer.
function toast(text, action, ms = action ? 15000 : 4000) {
  const t = $("toast");
  t.replaceChildren();
  const msg = document.createElement("span");
  msg.className = "msg";
  msg.textContent = text;
  msg.title = text;
  t.append(msg);
  if (action) {
    const b = document.createElement("button");
    b.className = "act";
    b.textContent = action.label;
    b.onclick = () => { hideToast(); action.run(); };
    t.append(b);
  }
  const close = document.createElement("button");
  close.className = "x";
  close.title = "關閉";
  close.innerHTML = icon("close");
  close.onclick = hideToast;
  t.append(close);

  t.hidden = false;
  t.classList.remove("show");
  void t.offsetWidth;
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastUntilInteraction = ms === "interact";
  if (!toastUntilInteraction) armToast(ms);
}

// Any real use of the viewer means the user has seen it; let a waiting toast go shortly after.
function noteInteraction(e) {
  if (!toastUntilInteraction || $("toast").hidden || e.target.closest?.(".toast")) return;
  toastUntilInteraction = false;
  armToast(2000);
}

function armToast(ms) {
  clearTimeout(toastTimer);
  toastTimer = setTimeout(hideToast, ms);
}

function hideToast() {
  clearTimeout(toastTimer);
  $("toast").hidden = true;
}

$("toast").addEventListener("mouseenter", () => clearTimeout(toastTimer));
$("toast").addEventListener("mouseleave", () => { if (!toastUntilInteraction) armToast(3000); });

// ---------- clipboard ----------

// The host reports copied markdown/image paths it can find on disk, with what to do about them (m.action,
// decided by ClipboardPolicy): this carries it out against what is on screen. force: the user clicked the
// tray notification.
function onClipboard(m) {
  const CLIP_MS = "interact", listAll = () => post({ type: "clipList" });
  if (m.files) {
    const n = m.files.length, pick = () => openPick(m.files, m.more);
    if (m.action === "OpenFirst") {
      openClipFile(m.files[0]);
      // From text only the first was looked for; the host finds the rest when asked.
      const label = m.files[0].rel ?? m.files[0].path;
      if (n === 1) toast(`已從剪貼簿開啟：${label}，剪貼簿裡還有其他檔案`, { label: "列出", run: listAll }, CLIP_MS);
      else toast(`已從剪貼簿開啟：${label}，還有 ${m.more ? "超過 " : ""}${n - 1} 個`, { label: "選擇", run: pick }, CLIP_MS);
    }
    else if (m.force) pick();
    else if (n === 1) toast("剪貼簿裡提到多個檔案", { label: "列出", run: listAll }, CLIP_MS); // only the first was looked for
    else toast(`剪貼簿裡有${m.more ? "超過 " : " "}${n} 個檔案`, { label: "選擇", run: pick }, CLIP_MS);
    return;
  }
  const offerOnly = m.action !== "Open";
  if (m.matches) {
    // Only a file name / path tail was copied. Prefer the match next to the open document.
    const dir = state.current ? state.current.slice(0, state.current.lastIndexOf("/") + 1) : "";
    const near = m.matches.filter((rel) => rel.slice(0, rel.lastIndexOf("/") + 1) === dir);
    if (m.matches.length === 1) m.rel = m.matches[0];
    else if (near.length === 1) m.rel = near[0];
    else {
      const name = baseName(m.matches[0]);
      toast(`剪貼簿：找到 ${m.matches.length} 個「${name}」`, { label: "選擇", run: () => openPalette(name) }, CLIP_MS);
      return;
    }
  }
  if (m.rel && m.rel === state.current) {
    if (offerOnly) return; // already in front of the user
    if (m.hash) scrollToHash(m.hash, true);
    else toast("剪貼簿的檔案已經開著：" + m.rel, null, CLIP_MS);
    return;
  }
  const label = m.rel ?? m.path;
  const open = () => openClipFile(m, false, m.hash ?? "");
  if (offerOnly) { toast((m.action === "Offer" ? "剪貼簿：" : "剪貼簿裡提到：") + label, { label: "開啟", run: open }, CLIP_MS); return; }
  const from = state.current;
  open();
  // A copy made inside the viewer may just be text being read; offer a one-click way back.
  if (m.fromSelf && from) toast("已從剪貼簿開啟：" + label, { label: "返回", run: () => go(-1) }, CLIP_MS);
  else toast("已從剪貼簿開啟：" + label, null, CLIP_MS);
}

// ---------- keyboard & mouse ----------

function focusDoc() { frame.contentWindow?.focus(); }

// ---------- keyboard shortcuts: the one table ----------

// Every shortcut once: the keys (the first is the one shown), what it does, and when it only applies
// sometimes. onKey matches keys against this table; the help panel, menus, quick-open hints and button
// tooltips are written from it. run returning false means "not handled here" (Esc with nothing to close).
const KEYS = {
  palette: { keys: ["Ctrl+P", "Ctrl+K"], label: "快速開啟", run: () => openPalette() },
  searchText: { keys: ["Ctrl+Shift+F"], label: "搜尋內文", run: () => openPalette("", "text") },
  find: { keys: ["Ctrl+F"], label: "在本篇中尋找", run: () => openFind() },
  findNext: { keys: ["F3", "Shift+F3"], label: "下一個、上一個符合處", when: () => !$("findbar").hidden, run: (e) => gotoFind(finder.i + (e.shiftKey ? -1 : 1)) },
  findDown: { keys: ["Enter"], label: "下一個" }, // in the find box (its own handler); shown on its button
  findUp: { keys: ["Shift+Enter"], label: "上一個" },
  openInTab: { keys: ["Ctrl+Enter"], label: "在新分頁開啟" }, // in quick open (its own handler)
  dark: { keys: ["Ctrl+D"], label: "亮色 / 暗色切換", run: () => toggleDark() },
  history: { keys: ["Ctrl+H"], label: "閱讀紀錄", run: () => openHistoryMenu() },
  newTab: { keys: ["Ctrl+T"], label: "新分頁", run: () => newTab() },
  closeTab: { keys: ["Ctrl+W"], label: "關閉分頁", run: () => closeTab(state.tab) },
  nextTab: { keys: ["Ctrl+Tab", "Ctrl+PageDown"], label: "下一個分頁", run: () => switchTab(state.tab + 1) },
  prevTab: { keys: ["Ctrl+Shift+Tab", "Ctrl+PageUp"], label: "上一個分頁", run: () => switchTab(state.tab - 1) },
  help: { keys: ["?", "F1"], label: "快捷鍵與功能", when: (e) => !e.target.closest?.("input, textarea"), run: () => openHelp() },
  side: { keys: ["Ctrl+B"], label: "檔案側欄", run: () => toggleSide() },
  toc: { keys: ["Ctrl+J"], label: "本篇大綱", run: () => toggleToc() },
  openFile: { keys: ["Ctrl+O"], label: "開啟檔案", run: () => post({ type: "pickFile" }) },
  openFolder: { keys: ["Ctrl+Shift+O"], label: "開啟資料夾", run: () => post({ type: "pickFolder" }) },
  zoomIn: { keys: ["Ctrl+=", "Ctrl++"], label: "放大文字", run: () => zoomStep(1) },
  zoomOut: { keys: ["Ctrl+-", "Ctrl+_"], label: "縮小文字", run: () => zoomStep(-1) },
  zoomReset: { keys: ["Ctrl+0"], label: "恢復 100%", run: () => zoomStep(0) },
  reload: { keys: ["F5", "Ctrl+R"], label: "重新整理", run: () => reloadAll() },
  zen: { keys: ["F11"], label: "專注閱讀", run: () => toggleZen() },
  back: { keys: ["Alt+←", "BrowserBack"], label: "上一頁", run: () => go(-1) },
  forward: { keys: ["Alt+→", "BrowserForward"], label: "下一頁", run: () => go(1) },
  close: {
    keys: ["Esc"], label: "關閉",
    run: () => closeMenus() || closeDialog() || (!$("findbar").hidden && (closeFind(), true)) || (layout.zen && (toggleZen(), true)),
  },
};

// "Ctrl+Shift+F" -> what to compare with a keydown. Shift counts except for punctuation keys, which
// Shift itself may produce ("+", "?", "_").
function parseKeys(text) {
  const parts = text.endsWith("++") ? [...text.slice(0, -2).split("+"), "+"] : text.split("+");
  const key = parts.pop().toLowerCase();
  const mods = new Set(parts.map((p) => p.toLowerCase()));
  return { ctrl: mods.has("ctrl"), shift: mods.has("shift"), alt: mods.has("alt"), key: { "←": "arrowleft", "→": "arrowright", esc: "escape" }[key] ?? key };
}
for (const action of Object.values(KEYS)) action.combos = action.keys.map(parseKeys);

const pressed = (c, e, key) => key === c.key && (e.ctrlKey || e.metaKey) === c.ctrl && e.altKey === c.alt
  && (e.shiftKey === c.shift || (key.length === 1 && !/[a-z0-9]/.test(key)));

const keyOf = (name) => KEYS[name].keys[0];
const withKey = (name, label = KEYS[name].label) => `${label} (${keyOf(name)})`;
const kbds = (keys) => keys.map((k) => `<kbd>${esc(k)}</kbd>`).join(" ");
// Names in KEYS become their shown key; anything else is shown as written.
const keysOf = (list) => list.map((k) => (KEYS[k] ? keyOf(k) : k));

function onKey(e) {
  if (!lightboxKey(e)) {
    const key = e.key.toLowerCase();
    const action = Object.values(KEYS).find((a) => a.run && a.combos.some((c) => pressed(c, e, key)) && (!a.when || a.when(e)));
    if (!action || action.run(e) === false) return;
  }
  e.preventDefault();
  e.stopPropagation();
}

function onMouseNav(e) {
  if (e.button === 3) { e.preventDefault(); go(-1); }
  if (e.button === 4) { e.preventDefault(); go(1); }
}


// ---------- messages from the host ----------

window.chrome?.webview?.addEventListener("message", (e) => {
  const m = e.data;
  switch (m.type) {
    case "tree": loadTree(); break;
    case "changed":
      if (m.rel === state.current) { reloadDoc(); toast("檔案已更新"); }
      break;
    case "root":
      if (m.root === cfg.root) break;
      saveTabs();
      cfg.root = m.root;
      cfg.name = m.name;
      $("filter").value = "";
      loadRootState(); // before anything saves tabs under the new folder's name
      showEmpty();
      loadTree();
      // Show the folder's active tab unless an "open" for a file there follows right away.
      setTimeout(() => { if (!state.current && !state.pending) openActiveTab(); }, 60);
      break;
    case "open": openDoc(m.rel, { scroll: m.scroll ?? 0 }); break;
    case "resolved": onResolved(m); break;
    case "clipboard": onClipboard(m); break;
    case "clipPick": openPick(m.files, m.more); break;
    case "history": state.history = m.items; break;
    case "searchResults": onSearchResults(m); break;
    case "clipMode": layout.clip = m.mode; saveLayout(); break;
    case "clipOptions": Object.assign(clipOpts, { chars: m.chars, multi: m.multi, max: m.max }); showClipOptions(); break;
    case "exported": onExported(m); break;
    case "system":
      cfg.systemDark = m.dark;
      if (state.theme === cfg.autoTheme) applyTheme(cfg.autoTheme, false);
      break;
  }
});

// ---------- wiring ----------

const setIcon = (id, name) => { $(id).innerHTML = icon(name); };
setIcon("back", "back");
setIcon("forward", "forward");
setIcon("reload", "reload");
setIcon("history", "clock");
setIcon("toggle-side", "side");
setIcon("toggle-toc", "toc");
setIcon("toggle-zen", "zen");
setIcon("theme", "palette");
setIcon("more", "more");
setIcon("change-root", "folderOpen");
setIcon("search-ico", "search");
setIcon("find-prev", "up");
setIcon("find-next", "down");
setIcon("find-close", "close");

// Buttons that stand for a shortcut (data-key) say so in their tooltip and, unless wired otherwise above, do
// what the shortcut does; key labels show the key. All from KEYS.
for (const el of document.querySelectorAll("[data-key]")) {
  const action = KEYS[el.dataset.key];
  el.title = withKey(el.dataset.key);
  if (!el.onclick && action.run) el.onclick = (e) => action.run(e);
}
for (const el of document.querySelectorAll("kbd[data-keys]")) el.textContent = keyOf(el.dataset.keys);

$("theme").onclick = openThemeMenu;
$("more").onclick = openMoreMenu;
listenTo(document);

setupPeek("hot-left", $("side"), "peek-left");
setupPeek("hot-right", $("toc"), "peek-right");
setupPeek("hot-top", document.querySelector(".topbar"), "peek-top");
setupResizer();

loadRootState();
applyTheme(state.theme, false);
applyLayout();
applyZoom();
renderToc();
updateChrome();
loadTree();
const start = decodeURIComponent(location.hash.slice(1));
const startTab = start ? state.tabs.findIndex((t) => t.hist[t.at]?.rel === start) : -1;
if (startTab >= 0) { activateTab(startTab); state.hist[state.at].scroll = cfg.startScroll || 0; openActiveTab(); }
else if (start) navigate(start, "", cfg.startScroll || 0);
else openActiveTab();
post({ type: "ready" }); // the host holds clipboard finds and open requests until now
