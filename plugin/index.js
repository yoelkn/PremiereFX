// PremiereFX — Premiere side.
// Connects to the PremiereFX.exe helper (ws://127.0.0.1:8791), sends it the list
// of installed effects, and applies the effect the helper asks for to the
// selected timeline clips.
const ppro = require("premierepro");

const WS_URL = "ws://127.0.0.1:8791";
const statusEl = document.getElementById("status");
const lastEl = document.getElementById("last");
let ws = null;

function setStatus(on) {
  statusEl.textContent = on ? "connected" : "waiting for helper";
  statusEl.className = on ? "on" : "off";
}

async function effectList() {
  const items = [];
  const vd = await ppro.VideoFilterFactory.getDisplayNames();
  const vm = await ppro.VideoFilterFactory.getMatchNames();
  for (let i = 0; i < vd.length; i++) items.push({ n: vd[i], m: vm[i], k: "v" });
  const ad = await ppro.AudioFilterFactory.getDisplayNames();
  for (const n of ad) items.push({ n, m: n, k: "a" });
  return items;
}

async function applyEffect(kind, match, name) {
  const p = await ppro.Project.getActiveProject();
  if (!p) return { ok: false, msg: "No open project" };
  const seq = await p.getActiveSequence();
  if (!seq) return { ok: false, msg: "No active sequence" };
  const sel = await seq.getSelection();
  const all = (await sel.getTrackItems()).filter(Boolean);
  const isVideo = kind === "v";
  const targets = all.filter((it) => (isVideo ? it instanceof ppro.VideoClipTrackItem : it instanceof ppro.AudioClipTrackItem));
  if (!targets.length) {
    return { ok: false, msg: all.length ? `No ${isVideo ? "video" : "audio"} clip selected for ${name}` : "Select a clip first" };
  }

  // Build everything that needs await before opening the transaction.
  const plan = [];
  for (const it of targets) {
    const chain = await it.getComponentChain();
    let insertAt = -1;
    if (isVideo) {
      // Graphic clips: appending lands inside the graphic's layer group and does not
      // render, so insert right after Motion instead.
      const count = await chain.getComponentCount();
      for (let i = 0; i < count; i++) {
        const c = await chain.getComponentAtIndex(i);
        if ((await c.getMatchName()) === "AE.ADBE Graphic Group") { insertAt = 2; break; }
      }
    }
    const fx = isVideo
      ? await ppro.VideoFilterFactory.createComponent(match)
      : await ppro.AudioFilterFactory.createComponentByDisplayName(match, it);
    plan.push({ chain, fx, insertAt });
  }

  let ok = false;
  p.lockedAccess(() => {
    ok = p.executeTransaction((ca) => {
      for (const { chain, fx, insertAt } of plan) {
        ca.addAction(insertAt >= 0 ? chain.createInsertComponentAction(fx, insertAt) : chain.createAppendComponentAction(fx));
      }
    }, `Apply ${name}`);
  });
  if (!ok) return { ok: false, msg: `Premiere refused to apply ${name}` };
  return { ok: true, msg: `${name} → ${targets.length} clip${targets.length > 1 ? "s" : ""}` };
}

function send(obj) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
}

function connect() {
  try { ws = new WebSocket(WS_URL); }
  catch (e) { return setTimeout(connect, 3000); }
  ws.onopen = async () => {
    setStatus(true);
    try { send({ type: "effects", items: await effectList() }); }
    catch (e) { lastEl.textContent = `Effect list failed: ${e.message}`; }
  };
  ws.onclose = () => { setStatus(false); setTimeout(connect, 3000); };
  ws.onerror = () => {};
  ws.onmessage = async (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === "refresh") return send({ type: "effects", items: await effectList() });
    if (msg.type !== "apply") return;
    let res;
    try { res = await applyEffect(msg.k, msg.m, msg.n); }
    catch (e) { res = { ok: false, msg: `${msg.n}: ${e.message || e}` }; }
    lastEl.textContent = res.msg;
    send({ type: "result", id: msg.id, ...res });
  };
}

connect();
