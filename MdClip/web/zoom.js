"use strict";
// Wheel-zoom / drag-pan image viewer, shared by image pages and the picture viewer (pictures.js).
// mdZoom(stage, img, { label }) -> { fit, key(e) -> handled, destroy }
window.mdZoom = function (stage, img, options = {}) {
  const MIN = 0.02, MAX = 40, PAD = 40;
  let scale = 1, x = 0, y = 0, fitted = true, drag = null;

  const bar = document.createElement("div");
  bar.className = "zoom-bar";
  bar.innerHTML =
    (options.label ? `<span class="name"></span><span class="sep"></span>` : "") +
    `<button data-z="out" title="縮小 (-)">−</button>` +
    `<button data-z="fit" class="pct" title="符合視窗 (0)">100%</button>` +
    `<button data-z="in" title="放大 (+)">+</button>` +
    `<span class="sep"></span>` +
    `<button data-z="fit" title="符合視窗 (0)">符合視窗</button>` +
    `<button data-z="actual" title="原始大小 (1)">1:1</button>`;
  if (options.label) {
    bar.querySelector(".name").textContent = options.label;
    bar.querySelector(".name").title = options.label;
  }
  stage.appendChild(bar);
  const pct = bar.querySelector(".pct");

  function apply() {
    img.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
    img.style.imageRendering = scale >= 3 ? "pixelated" : "auto";
    pct.textContent = Math.round(scale * 100) + "%";
  }

  function fit() {
    const w = img.naturalWidth || 1, h = img.naturalHeight || 1, r = stage.getBoundingClientRect();
    scale = Math.min((r.width - PAD * 2) / w, (r.height - PAD * 2) / h, 1);
    if (!(scale > 0)) scale = 1;
    x = (r.width - w * scale) / 2;
    y = (r.height - h * scale) / 2;
    fitted = true;
    apply();
  }

  // Zoom keeping the image point under (cx, cy) fixed on screen.
  function zoomAt(cx, cy, factor) {
    const r = stage.getBoundingClientRect();
    const px = cx - r.left, py = cy - r.top;
    const next = Math.min(MAX, Math.max(MIN, scale * factor));
    x = px - (px - x) * (next / scale);
    y = py - (py - y) * (next / scale);
    scale = next;
    fitted = false;
    apply();
  }

  function center() {
    const r = stage.getBoundingClientRect();
    return [r.left + r.width / 2, r.top + r.height / 2];
  }

  const zoomCenter = (factor) => zoomAt(...center(), factor);
  const actual = (cx, cy) => (cx === undefined ? zoomCenter(1 / scale) : zoomAt(cx, cy, 1 / scale));

  stage.addEventListener("wheel", (e) => {
    e.preventDefault();
    // Precision touchpads send small deltas with ctrlKey for pinch; scale them up.
    zoomAt(e.clientX, e.clientY, Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0018)));
  }, { passive: false });

  stage.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || e.target.closest(".zoom-bar")) return;
    drag = { px: e.clientX, py: e.clientY, x, y };
    stage.setPointerCapture(e.pointerId);
    stage.classList.add("grabbing");
  });
  stage.addEventListener("pointermove", (e) => {
    if (!drag) return;
    x = drag.x + e.clientX - drag.px;
    y = drag.y + e.clientY - drag.py;
    fitted = false;
    apply();
  });
  const endDrag = () => { drag = null; stage.classList.remove("grabbing"); };
  stage.addEventListener("pointerup", endDrag);
  stage.addEventListener("pointercancel", endDrag);
  stage.addEventListener("dblclick", (e) => {
    if (e.target.closest(".zoom-bar")) return;
    if (fitted) actual(e.clientX, e.clientY); else fit();
  });

  bar.addEventListener("click", (e) => {
    const z = e.target.closest("button")?.dataset.z;
    if (z === "in") zoomCenter(1.25);
    else if (z === "out") zoomCenter(0.8);
    else if (z === "fit") fit();
    else if (z === "actual") actual();
  });

  function key(e) {
    if (e.ctrlKey || e.altKey || e.metaKey) return false;
    if (e.key === "+" || e.key === "=") zoomCenter(1.25);
    else if (e.key === "-" || e.key === "_") zoomCenter(0.8);
    else if (e.key === "0") fit();
    else if (e.key === "1") actual();
    else return false;
    return true;
  }

  const resize = new ResizeObserver(() => { if (fitted) fit(); });
  resize.observe(stage);
  img.draggable = false;
  if (img.complete && img.naturalWidth) fit(); else img.addEventListener("load", fit, { once: true });

  return { fit, key, destroy: () => resize.disconnect() };
};
