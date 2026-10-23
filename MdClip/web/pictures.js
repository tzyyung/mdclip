"use strict";
// Looking through a set of pictures one at a time: wheel zoom and drag (zoom.js), ← → or the arrow buttons
// for the previous / next one. The one viewer for the picture window (pictures.html) and the picture pane
// docked in the app (app.html).
// mdPictures(host, { actions: [{ label, title, run }], onIndex(i) })
//   -> { show(items, start), step(delta), key(e) -> handled, items, index }
// items: [{ src, name, path? }]
window.mdPictures = function (host, options = {}) {
  let items = [], index = 0, viewer = null;
  host.classList.add("pictures");
  host.innerHTML =
    `<div class="pic-head">` +
    `<button class="pic-step" data-step="-1" title="上一張 (←)">‹</button>` +
    `<span class="pic-name"></span><span class="pic-count"></span>` +
    `<button class="pic-step" data-step="1" title="下一張 (→)">›</button>` +
    `<span class="pic-gap"></span>` +
    (options.actions ?? []).map((a, i) => `<button class="pic-act" data-act="${i}" title="${a.title}">${a.label}</button>`).join("") +
    `</div><div class="zoom-stage pic-stage"></div>`;

  host.querySelector(".pic-head").addEventListener("click", (e) => {
    const b = e.target.closest("button");
    if (b?.dataset.step) step(+b.dataset.step);
    else if (b?.dataset.act) options.actions[+b.dataset.act].run();
  });

  function render() {
    viewer?.destroy();
    // A new stage each time: zoom.js listens on it, and the old picture's listeners go with the old one.
    const stage = host.querySelector(".pic-stage"), fresh = stage.cloneNode(false);
    fresh.innerHTML = `<img alt="">`;
    stage.replaceWith(fresh);
    const item = items[index], img = fresh.querySelector("img");
    img.src = item.src;
    viewer = mdZoom(fresh, img);
    const name = host.querySelector(".pic-name");
    name.textContent = item.name;
    name.title = item.path ?? item.name;
    host.querySelector(".pic-count").textContent = items.length > 1 ? `${index + 1} / ${items.length}` : "";
    for (const b of host.querySelectorAll(".pic-step")) b.hidden = items.length < 2;
    options.onIndex?.(index);
  }

  function step(delta) {
    if (items.length < 2) return;
    index = (index + delta + items.length) % items.length;
    render();
  }

  // ← → move through the set; + - 0 1 zoom (zoom.js). Alt+arrows stay the app's back / forward.
  function key(e) {
    if (e.ctrlKey || e.altKey || e.metaKey) return false;
    if (e.key === "ArrowLeft") step(-1);
    else if (e.key === "ArrowRight") step(1);
    else return viewer?.key(e) ?? false;
    return true;
  }

  return {
    show(list, start = 0) { items = list; index = Math.min(Math.max(start, 0), list.length - 1); render(); },
    step, key,
    get items() { return items; },
    get index() { return index; },
  };
};
