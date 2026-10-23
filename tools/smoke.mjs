// Drives a running MdViewer (started with --remote-debugging-port) through its features and reports
// PASS/FAIL per check. Run by smoke-test.ps1, which starts the viewer, guards the clipboard and
// checks the tray behaviour afterwards.
//   node smoke.mjs <port> <fixtures folder>
// SMOKE_ONLY=<text> runs only the checks whose name contains it (opening the start file always runs);
// SMOKE_REPEAT=<n> runs each check n times.
import fs from "node:fs";
import { execFileSync } from "node:child_process";
import path from "node:path";

const [port, fixtures] = process.argv.slice(2);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- CDP ----------

let page;
for (let i = 0; i < 40 && !page; i++) {
  try {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
    page = targets.find((t) => t.type === "page" && t.url.includes("md.view/__app"));
  } catch { /* not up yet */ }
  if (!page) await sleep(500);
}
if (!page) { console.log("FAIL  viewer page not found on port " + port); process.exit(1); }

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let seq = 0;
const pending = new Map();
ws.addEventListener("message", (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
const send = (method, params = {}) => new Promise((r) => { const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });

// Evaluates an expression (or an async IIFE body) in the app page and returns its value.
async function js(expr) {
  const r = await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description ?? "script error");
  return r.result?.result?.value;
}

async function waitFor(expr, ms = 5000) {
  for (const end = Date.now() + ms; Date.now() < end; await sleep(150)) {
    try { if (await js(expr)) return true; } catch { /* the page script may not have run yet */ }
  }
  // What the page was showing instead, for intermittent failures.
  const seen = await js(`JSON.stringify({ current: state.current, pending: !!state.pending, toast: ${toastJs} })`);
  throw new Error("timed out waiting for: " + expr + "  page: " + seen);
}

// ---------- clipboard (the PowerShell wrapper restores the user's clipboard afterwards) ----------

const ps = (cmd) => execFileSync("powershell", ["-NoProfile", "-Sta", "-Command",
  "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + cmd], { encoding: "utf8" }).trim();
const setClipboard = (text) => ps(`Set-Clipboard -Value '${text.replace(/'/g, "''")}'`);
const getClipboard = () => ps("Get-Clipboard -Raw");

// ---------- checks ----------

const results = [];
const only = process.env.SMOKE_ONLY ?? "", repeat = Math.max(1, Number(process.env.SMOKE_REPEAT) || 1);
async function check(name, fn) {
  const setup = results.length === 0; // the first check opens the start file the others rely on
  if (only && !setup && !name.includes(only)) return;
  for (let i = 1; i <= (setup ? 1 : repeat); i++) {
    const label = repeat > 1 && !setup ? `${name} #${i}` : name;
    try {
      const detail = await fn();
      results.push(true);
      console.log(`PASS  ${label}${detail ? "  (" + detail + ")" : ""}`);
    } catch (err) {
      results.push(false);
      console.log(`FAIL  ${label}  ${err.message}`);
    }
  }
}
const expect = (cond, msg) => { if (!cond) throw new Error(msg); };

// The page's toast: its text ("" when hidden), a wait for a text in it, and its button.
const toastJs = "($('toast').hidden ? '' : $('toast').textContent)";
const toastText = () => js(toastJs);
const waitForToast = (part, ms = 5000) => waitFor(`${toastJs}.includes(${JSON.stringify(part)})`, ms);
const pressToast = () => js("$('toast').querySelector('.act').click()");

// Changes the clipboard settings through their panel, as a user would.
const setClipOptions = (opts) => js(`(() => {
  openClipSettings();
  const f = $('clipset-form').elements, o = ${JSON.stringify(opts)};
  for (const k in o) f[k].value = o[k];
  f.max.dispatchEvent(new Event('change', { bubbles: true }));
  closeDialog();
})()`);

await check("opens the start file", async () => {
  await waitFor("(state.current ?? '').endsWith('guide.md')", 15000);
  return await js("document.title");
});

const dir = (await js("state.current ?? ''")).replace(/[^/]*$/, ""); // fixtures folder relative to the root
const rel = (name) => dir + name;
const open = async (name) => { await js(`navigate(${JSON.stringify(rel(name))})`); await waitFor(`state.current === ${JSON.stringify(rel(name))} && !state.pending`); await sleep(400); };

await check("code is coloured", async () => {
  const n = await js("frame.contentDocument.querySelectorAll('code.hljs .hljs-keyword').length");
  expect(n > 0, "no highlighted keywords");
  return n + " keywords";
});

await check("mermaid diagram is drawn", async () => {
  await waitFor("frame.contentDocument.querySelector('.mermaid.figure svg')", 8000);
});

await check("math is typeset", async () => {
  await waitFor("frame.contentDocument.querySelectorAll('.katex').length >= 2", 8000);
  return await js("frame.contentDocument.querySelectorAll('.katex').length") + " formulas";
});

await check("front matter box", async () => {
  const text = await js("frame.contentDocument.querySelector('.frontmatter summary')?.textContent ?? ''");
  expect(text.includes("smoke-guide"), "summary was: " + text);
  const tags = await js("[...frame.contentDocument.querySelectorAll('.frontmatter dd')].map(d => d.textContent).join('|')");
  expect(tags.includes("test, fixture"), "list value: " + tags);
});

await check("outline lists the headings", async () => {
  const n = await js("state.headings.length");
  expect(n === 4, "headings: " + n);
});

await check("copy button copies the code", async () => {
  await js("frame.contentDocument.querySelector('.copy-code').click()");
  await sleep(500);
  const text = getClipboard();
  expect(text === "public sealed class Gate { public int Count = 42; }", "clipboard: " + text);
  const toast = await toastText();
  expect(!toast.includes("剪貼簿"), "our own copy came back as a clipboard find: " + toast);
});

await check("heading link copies and reopens at the heading", async () => {
  await js("[...frame.contentDocument.querySelectorAll('.h-anchor')].find(b => b.parentElement.id === '公式').click()");
  await sleep(500);
  const link = getClipboard();
  expect(link.endsWith("guide.md#公式"), "clipboard: " + link);
  await open("other.md");
  setClipboard("請看 " + link + " 這裡");
  await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))}`, 5000);
  await sleep(600);
  const top = await js("Math.round(frame.contentDocument.getElementById('公式').getBoundingClientRect().top)");
  expect(top >= 0 && top < 120, "heading is at " + top + "px");
});

await check("clipboard: bare file name opens", async () => {
  setClipboard("other.md");
  await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`, 5000);
});

const passage = (file) => "這次修改主要是調整清單排序的規則，原本的做法在重複項目上會多算一次，細節請參考 " + file + " 裡第三節的說明，另外也更新了測試資料。";

await check("clipboard: a passage mentioning a file only offers it", async () => {
  await open("guide.md");
  setClipboard(passage(path.join(fixtures, "other.md")));
  await waitForToast("剪貼簿裡提到");
  expect(await js("state.current") === rel("guide.md"), "the page changed");
  await pressToast();
  await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`);
});

await check("clipboard: several files are listed to pick from", async () => {
  setClipboard(`${path.join(fixtures, "guide.md")}\n${path.join(fixtures, "evil.md")}`);
  await waitForToast("2 個檔案");
  expect(await js("state.current") === rel("other.md"), "the page changed");
  await pressToast();
  const names = await js("pal.mode === 'pick' ? pal.items.map(i => i.path.split('\\\\').pop()).join(',') : 'mode ' + pal.mode");
  expect(names === "guide.md,evil.md", "list: " + names);
  await js("choose(pal.items[0])");
  await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))}`);
});

await check("clipboard settings change what opens by itself", async () => {
  const before = await js("clipOpts.chars");
  await setClipOptions({ chars: 500 });
  try {
    setClipboard(passage(path.join(fixtures, "other.md")));
    await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`, 5000);
  } finally {
    await setClipOptions({ chars: before });
  }
  expect(await js("clipOpts.chars") === before, "not restored");
});

await check("clipboard: list limit and open-the-first", async () => {
  const before = await js("({ multi: clipOpts.multi, max: clipOpts.max })");
  const three = ["guide.md", "evil.md", "other.md"].map((n) => path.join(fixtures, n)).join("\n");
  try {
    await setClipOptions({ multi: "list", max: 2 });
    setClipboard(three);
    await waitForToast("超過 2 個檔案");
    await pressToast();
    expect(await js("pal.items.length === 2 && pal.note.includes('只列出前 2 個')"), "list: " + await js("pal.note"));
    await js("closePalette()");

    await setClipOptions({ multi: "first", max: 5 });
    setClipboard(three);
    await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))} && ${toastJs}.includes('還有其他檔案')`, 5000);
    await pressToast();
    await waitFor("pal.mode === 'pick' && !$('palette').hidden && pal.items.length === 3", 5000);
    await js("closePalette()");
  } finally {
    await setClipOptions(before);
  }
});

await check("keyboard shortcuts work from the KEYS table", async () => {
  const press = (key, mods = {}) => js(`document.dispatchEvent(new KeyboardEvent("keydown", ${JSON.stringify({ key, bubbles: true, cancelable: true, ...mods })}))`);
  const side = await js("layout.side");
  await press("b", { ctrlKey: true });
  expect(await js("layout.side") === !side, "Ctrl+B did not toggle the file panel");
  await press("b", { ctrlKey: true });
  await press("?", { shiftKey: true });
  expect(await js("!$('help').hidden"), "? did not open the help panel");
  await press("Escape");
  expect(await js("$('help').hidden"), "Esc did not close the help panel");
  await press("t", { ctrlKey: true });
  expect(await js("state.tabs.length === 2"), "Ctrl+T did not open a tab");
  await js("closePalette()");
  await press("w", { ctrlKey: true });
  await sleep(300);
  expect(await js("state.tabs.length === 1"), "Ctrl+W did not close the tab");
  await press("F", { ctrlKey: true, shiftKey: true });
  expect(await js("pal.mode === 'text' && !$('palette').hidden"), "Ctrl+Shift+F did not open text search");
  await js("closePalette()");
  expect(await js("$('back').title === '上一頁 (Alt+←)' && $('search').querySelector('kbd').textContent === 'Ctrl+P'"), "tooltips or key labels not written from KEYS");
});

await check("quick open finds a file", async () => {
  await js("openPalette('othe')");
  const subs = await js("pal.items.map(i => i.rel ?? '')");
  await js("closePalette()");
  expect(subs.includes(rel("other.md")), "items: " + subs.join(", "));
});

await check("full-text search", async () => {
  await js("openPalette('', 'text'); $('palette-input').value = '門檻'; $('palette-input').dispatchEvent(new Event('input'))");
  await waitFor("pal.items.length > 0 && pal.note", 5000);
  const note = await js("pal.note");
  const files = await js("[...new Set(pal.items.map(i => i.rel))]");
  await js("closePalette()");
  expect(files.includes(rel("guide.md")) && files.includes(rel("other.md")), "files: " + files.join(", "));
  return note;
});

await check("find in page", async () => {
  await open("guide.md");
  await js("openFind('門檻')");
  await sleep(300);
  const count = await js("$('find-count').textContent");
  await js("closeFind()");
  expect(/^1 \/ [3-9]$/.test(count), "count: " + count);
  return count;
});

await check("back and forward", async () => {
  await open("other.md");
  await js("go(-1)");
  await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))}`);
  await js("go(1)");
  await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`);
});

await check("tabs", async () => {
  await js(`openInNewTab(${JSON.stringify(rel("guide.md"))})`);
  await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))} && state.tabs.length === 2`);
  expect(await js("!$('tabs').hidden && $('tabs').querySelectorAll('.tab').length === 2"), "strip not shown");
  await js("switchTab(0)");
  await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`);
  await js("go(-1)"); // each tab keeps its own history
  await waitFor(`state.current === ${JSON.stringify(rel("guide.md"))}`);
  await js("closeTab(1)");
  await sleep(300);
  expect(await js("state.tabs.length === 1 && $('tabs').hidden"), "tab not closed");
});

await check("reading history", async () => {
  const first = await js("state.history[0]?.path ?? ''");
  expect(first.endsWith("guide.md"), "latest: " + first);
});

await check("drag and drop opens a file", async () => {
  const file = path.join(fixtures, "other.md");
  const data = { items: [], files: [file], dragOperationsMask: 1 };
  for (const type of ["dragEnter", "dragOver", "drop"]) await send("Input.dispatchDragEvent", { type, x: 500, y: 400, data });
  await waitFor(`state.current === ${JSON.stringify(rel("other.md"))}`, 5000);
});

await check("text zoom", async () => {
  await js("zoomStep(1)");
  const zoom = await js("frame.contentDocument.body.style.zoom");
  await js("zoomStep(-1)");
  expect(Number(zoom) > 1, "zoom: " + zoom);
});

await check("theme switch redraws diagrams", async () => {
  const before = await js("state.theme");
  await open("guide.md");
  await js("applyTheme('github-dark')");
  await sleep(1200);
  const dark = await js("frame.contentDocument.documentElement.dataset.theme === 'github-dark' && !!frame.contentDocument.querySelector('.mermaid.figure svg')");
  await js(`applyTheme(${JSON.stringify(before)})`);
  expect(dark, "document did not switch or lost its diagram");
});

await check("help panel", async () => {
  await js("openHelp()");
  const n = await js("$('help-body').querySelectorAll('section').length");
  await js("closeDialog()");
  expect(n === 5, "sections: " + n);
});

await check("big document: quick, and reopens at the same line", async () => {
  // About 1.3 MB: past the size where off-screen parts are only estimated (doc.css "big").
  const para = "這次修改主要是調整清單排序的規則，原本的做法在重複項目上會多算一次，細節請參考說明。 **重點** 與 `code`。\n\n";
  let text = "# 大文件\n\n";
  for (let i = 0; i < 900; i++) text += `## 第 ${i} 節\n\n${para.repeat(6)}\`\`\`csharp\npublic sealed class Gate${i} { }\n\`\`\`\n\n`;
  const file = path.join(fixtures, "big.md");
  fs.writeFileSync(file, text);
  try {
    const start = Date.now();
    await open("big.md");
    const took = Date.now() - start;
    expect(await js("frame.contentDocument.body.classList.contains('big')"), "not marked big");
    const id = await js("state.headings[700].id");
    await js(`scrollToHash(${JSON.stringify(id)}, false)`);
    await sleep(1200); // the position is saved after a pause in scrolling
    const before = await js(`Math.round(frame.contentDocument.getElementById(${JSON.stringify(id)}).getBoundingClientRect().top)`);
    await open("guide.md");
    await js("go(-1)");
    await waitFor(`state.current === ${JSON.stringify(rel("big.md"))} && !state.pending`);
    await sleep(800);
    const after = await js(`Math.round(frame.contentDocument.getElementById(${JSON.stringify(id)}).getBoundingClientRect().top)`);
    expect(Math.abs(after - before) <= 4, `heading was at ${before}px, now ${after}px`);
    return `opened in ${took} ms`;
  } finally {
    fs.rmSync(file, { force: true });
  }
});

await check("scripts inside documents never run", async () => {
  await open("evil.md");
  await js("frame.contentDocument.querySelector('a[href^=\"javascript\"]')?.click()");
  await sleep(500);
  const title = await js("document.title");
  expect(!title.includes("PWNED"), "title: " + title);
  // Files under the root that are shown as they are: an HTML page and an SVG picture with scripts.
  for (const file of ["evil.html", "pics/evil.svg"]) {
    await js(`frame.contentWindow.location.replace("/" + ${JSON.stringify(rel(file))})`);
    await sleep(700);
    const after = await js("document.title");
    expect(!after.includes("PWNED"), `${file} ran its script: ${after}`);
  }
});

await open("guide.md");
ws.close();
const failed = results.filter((ok) => !ok).length;
console.log(`\n${results.length - failed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
