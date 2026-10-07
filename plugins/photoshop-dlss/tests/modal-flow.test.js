const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const controls = new Map();
for (const id of ["mix", "mixNumber", "process", "pano", "clearCache", "status", "log"])
  controls.set(id, { value: "50", dataset: {}, setAttribute(key, value) { this[key] = value; }, addEventListener(event, callback) { this[event] = callback; } });
const log = controls.get("log");
log.childNodes = [];
log.scrollTop = 0;
log.scrollHeight = 0;
log.clientHeight = 100;
log.appendChild = entry => { log.childNodes.push(entry); };
log.removeChild = () => { log.childNodes.shift(); };
Object.defineProperty(log, "firstChild", { get() { return log.childNodes[0]; } });

let modalDepth = 0;
let captures = 0;
let renders = 0;
let capturedDisposed = false;
let outputDisposed = false;
let insertedPixels;
let insertedBounds;
let smartRasterized = false;
let failRasterRead = false;
let rasterFails = false;
let toolModal = false;
let toolCommits = 0;
let layeredReadFails = false;
let isolatedReadFails = false;
let scratchClosed = 0;
let starterDeleted = 0;
let insertions = 0;
let renderFrame = () => new Uint8Array(64 * 64 * 3).fill(200);
let failOnRender = 0;
let smartReadMessage = "Photoshop Error. Code: -1. Message: Could not update smart object files ^0.";
let failSmartPlacement = false;
let failSourceRaster = false;
let failSourceRead = false;
let historyRollbacks = 0;
let sourceRasterizations = 0;
let sourceComponents = 3;
const autoCloseDocuments = new Set();
const original = {
  id: 1, name: "Flattened artwork", kind: "pixel", opacity: 100, blendMode: "normal",
  boundsNoEffects: { left: 4, top: 6, right: 68, bottom: 70 },
  async duplicate(target) {
    assert.equal(modalDepth, 1);
    if (!target) {
      const copy = { id: 10 };
      document.activeLayers = [copy];
      return copy;
    }
    assert.equal(target.id, 8, "the pixel layer must be copied into the isolated document");
    const copy = { id: 6, kind: "pixel" };
    target.layers.unshift(copy);
    target.activeLayers = [copy];
    return copy;
  }
};
const smartOriginal = {
  id: 4, name: "Linked artwork", kind: "smartObject", opacity: 100, blendMode: "normal",
  boundsNoEffects: { left: 4, top: 6, right: 68, bottom: 70 },
  async duplicate(target) {
    assert.equal(modalDepth, 1);
    if (!target) {
      const copy = { id: 11, kind: "smartObject", async rasterize() {
        sourceRasterizations++;
        if (failSourceRaster) throw new Error("Could not rasterize ^0.");
        this.kind = "pixel";
      }, async duplicate(scratch) {
        assert.equal(this.kind, "pixel", "cross-document recovery must copy pixels, not place a Smart Object");
        const isolated = { id: 6, kind: "pixel" };
        scratch.layers.unshift(isolated); scratch.activeLayers = [isolated];
        return isolated;
      } };
      document.layers.unshift(copy); document.activeLayers = [copy];
      return copy;
    }
    assert.equal(target.id, 8, "Smart Objects should be isolated outside the source document");
    if (failSmartPlacement) throw new Error("Photoshop Error. Code: -32005. Message: Could not place the document ^0.");
    const copy = {
      id: 5,
      async rasterize(target) {
        assert.equal(target, "entireLayer");
        if (rasterFails) throw new Error("Could not rasterize ^0.");
        smartRasterized = true;
      }
    };
    target.layers.unshift(copy);
    target.activeLayers = [copy];
    return copy;
  }
};
const document = {
  id: 7,
  width: 128, height: 128, resolution: 72,
  activeLayers: [original],
  layers: [original, smartOriginal],
  async createPixelLayer({ name }) {
    assert.equal(modalDepth, 1);
    insertions++;
    return { id: 2, name, move() {} };
  }
};
const photoshop = {
  app: {
    activeDocument: document,
    async createDocument(options) {
      assert.equal(modalDepth, 1);
      assert.equal(options.width, document.width);
      assert.equal(options.fill, "transparent");
      const scratch = {
        id: 8,
        layers: [{ id: 9, async delete() { starterDeleted++; } }],
        activeLayers: [],
        async closeWithoutSaving() {
          assert.equal(modalDepth, 1);
          scratchClosed++;
          photoshop.app.activeDocument = document;
        }
      };
      this.activeDocument = scratch;
      return scratch;
    }
  },
  constants: {
    ElementPlacement: { PLACEBEFORE: "before" },
    LayerKind: { NORMAL: "pixel", SMARTOBJECT: "smartObject" },
    RasterizeType: { ENTIRELAYER: "entireLayer" }
  },
  core: {
    async getActiveTool() { return { title: toolModal ? "Free Transform" : "Move Tool", isModal: toolModal }; },
    async endModalToolState(commit) {
      assert.equal(commit, true);
      toolCommits++;
      toolModal = false;
    },
    async executeAsModal(callback) {
      assert.equal(modalDepth, 0, "modal scopes must not nest");
      modalDepth++;
      const hostControl = {
        registerAutoCloseDocument(id) { autoCloseDocuments.add(id); },
        unregisterAutoCloseDocument(id) { autoCloseDocuments.delete(id); },
        async suspendHistory({ documentID }) {
          assert.equal(documentID, document.id);
          return { layers: [...document.layers], activeLayers: [...document.activeLayers] };
        },
        async resumeHistory(saved, commit) {
          assert.equal(commit, false, "temporary source edits must be rolled back, never committed");
          document.layers = saved.layers; document.activeLayers = saved.activeLayers;
          historyRollbacks++;
        }
      };
      try { return await callback({ hostControl }); }
      finally { modalDepth--; }
    }
  },
  action: {
    async batchPlay(commands) {
      assert.equal(modalDepth, 1);
      if (commands.some(command => command._obj === "newPlacedLayer"))
        document.activeLayers = [{ id: 3, name: "Smart Object" }];
      else if (commands[0]._target[0]._id === original.id)
        document.activeLayers = [original];
      else if (commands[0]._target[0]._id === smartOriginal.id)
        document.activeLayers = [smartOriginal];
    }
  },
  imaging: {
    async getPixels({ layerID }) {
      assert.equal(modalDepth, 1, "pixel capture needs a modal scope");
      captures++;
      if (layerID === original.id && layeredReadFails)
        throw new Error(smartReadMessage);
      if (layerID === 11 && failSourceRead) throw new Error("Photoshop Error. Code: -32005. Message: Could not place the document ^0.");
      if (layerID === 6 && isolatedReadFails)
        throw new Error("Isolated layer capture failed.");
      if (layerID === smartOriginal.id)
        throw new Error(smartReadMessage);
      if (layerID === 5 && failRasterRead && !smartRasterized)
        throw new Error("Photoshop Error. Code: -1. Message: Could not update smart object files ^0.");
      return {
        sourceBounds: { left: 4, top: 6 },
        imageData: {
          width: 64, height: 64, components: sourceComponents,
          async getData() {
            assert.equal(modalDepth, 1, "pixel reading needs a modal scope");
            const data = new Uint8Array(64 * 64 * sourceComponents).fill(100);
            if (sourceComponents === 4) for (let index = 3; index < data.length; index += 4) data[index] = 77;
            return data;
          },
          dispose() { capturedDisposed = true; }
        }
      };
    },
    async createImageDataFromBuffer(pixels) {
      assert.equal(modalDepth, 1, "result image creation needs a modal scope");
      return { pixels, dispose() { outputDisposed = true; } };
    },
    async putPixels({ imageData, targetBounds }) {
      assert.equal(modalDepth, 1, "pixel insertion needs a modal scope");
      insertedPixels = imageData.pixels;
      insertedBounds = targetBounds;
    }
  }
};
const uxp = { entrypoints: { setup() {} }, shell: {}, storage: { localFileSystem: {} } };
const context = {
  require(name) { return name === "photoshop" ? photoshop : uxp; },
  document: {
    getElementById(id) { return controls.get(id); },
    createElement() { return { dataset: {}, textContent: "" }; }
  },
  async fetch(url) {
    assert.ok(url.startsWith("http://localhost:47837/"), "UXP needs the allowlisted localhost hostname");
    if (url.endsWith("/health"))
      return { ok: true, headers: { get() { return "3"; } } };
    if (url.endsWith("/render")) {
      assert.equal(controls.get("clearCache").disabled, true, "cache clearing must be disabled throughout rendering");
      assert.equal(modalDepth, 0, "neural rendering must not hold Photoshop modal");
      assert.equal(capturedDisposed, true, "capture must be released before rendering");
      renders++;
      if (renders === failOnRender) return { ok: false, async text() { return "Second panorama render failed"; } };
      return { ok: true, headers: { get() { return "miss"; } }, async arrayBuffer() { return renderFrame().buffer; } };
    }
    throw new Error(`Unexpected request: ${url}`);
  },
  setInterval() { return 1; },
  clearInterval() {},
  console: { error() {} },
  Date,
  Uint8Array
};

vm.runInNewContext(fs.readFileSync(path.join(__dirname, "..", "panel.js"), "utf8"), context);
controls.get("process").click().then(async () => {
  assert.equal(captures, 1);
  assert.equal(renders, 1);
  assert.equal(insertedPixels[0], 150, "50% Mix should blend source and neural pixels");
  assert.equal(insertedPixels[3], 255);
  assert.equal(outputDisposed, true);
  assert.equal(scratchClosed, 0, "a successful direct read must skip isolated capture");
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  assert.equal(controls.get("status").dataset.state, "done");
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(captures, 3, "a failed Smart Object read should retry on the temporary raster layer");
  assert.equal(renders, 2);
  assert.equal(smartRasterized, false, "a readable isolated Smart Object must not be rasterized");
  assert.equal(scratchClosed, 1, "the temporary Smart Object document must close");
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  assert.equal(controls.get("status").dataset.state, "done");
  failRasterRead = true;
  rasterFails = true;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(scratchClosed, 2, "a failed fallback must close its temporary document");
  assert.equal(document.activeLayers[0].id, smartOriginal.id, "a failed fallback must reselect the source");
  assert.equal(renders, 2, "a failed capture must never reach the renderer");
  assert.match(controls.get("status").textContent, /Could not rasterize/);
  rasterFails = false;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(smartRasterized, true, "the isolated Smart Object should be rasterized only after its read fails");
  assert.equal(scratchClosed, 3);
  assert.equal(renders, 3);
  layeredReadFails = true;
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(scratchClosed, 4, "the isolated document must close after capture");
  assert.equal(starterDeleted, 4, "the isolated document must contain only the selected layer");
  assert.equal(photoshop.app.activeDocument, document);
  assert.equal(renders, 4, "the isolated pixel layer should reach the renderer");
  assert.equal(insertedBounds.left, 4, "the result must keep the source layer's offset");
  assert.equal(insertedBounds.top, 6);
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  isolatedReadFails = true;
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(scratchClosed, 5, "a failed isolated read must close its temporary document");
  assert.equal(photoshop.app.activeDocument, document);
  assert.equal(document.activeLayers[0].id, original.id);
  assert.equal(renders, 4, "an isolated read failure must not start rendering");
  assert.match(controls.get("status").textContent, /even in an isolated document/);
  layeredReadFails = false;
  toolModal = true;
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(toolCommits, 1, "an active Photoshop tool edit must be committed before capture");
  assert.equal(renders, 5);
  controls.get("pano").click();
  assert.equal(controls.get("pano")["aria-pressed"], "true");
  let panoPass = 0;
  renderFrame = () => new Uint8Array(64 * 64 * 3).fill(++panoPass === 1 ? 200 : 60);
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(renders, 7, "Pano must render both original and offset views");
  assert.equal(insertedPixels[0], 80, "the seam view must be blended before applying Mix");
  assert.equal(insertedPixels[32 * 4], 150, "Pano must retain the original render in the middle");
  assert.equal(insertedPixels[3], 255, "Pano must retain source alpha");
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  assert.equal(controls.get("pano").disabled, false);
  const beforeZero = renders;
  controls.get("mixNumber").value = "0";
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(renders, beforeZero, "0% Pano must skip both renders");
  assert.equal(document.activeLayers[0].name, "DLSS - 0% Mix");
  controls.get("mixNumber").value = "50";
  failOnRender = renders + 2;
  const beforeFailure = insertions;
  document.activeLayers = [original];
  await controls.get("process").click();
  assert.equal(insertions, beforeFailure, "a failed second pass must not insert a partial result");
  assert.equal(document.activeLayers[0].id, original.id);
  assert.equal(controls.get("status").dataset.state, "error");
  assert.match(controls.get("status").textContent, /Second panorama render failed/);
  assert.equal(controls.get("pano").disabled, false);
  assert.equal(context.clampMix(250), 200, "Mix must clamp to the renderer's 200% maximum");
  const enhanced = context.composePixels(
    new Uint8Array([100, 100, 100, 77]), new Uint8Array([150, 150, 150]), 4, 200, 1);
  assert.deepEqual(Array.from(enhanced), [205, 205, 205, 77],
    "200% Mix must use the player's guarded linear-light luminance extension");
  const html = fs.readFileSync(path.join(__dirname, "..", "index.html"), "utf8");
  failOnRender = 0;
  isolatedReadFails = false;
  failRasterRead = false;
  controls.get("pano").click();
  smartReadMessage = "Photoshop Error. Code: -32005. Message: Could not place the document ^0.";
  sourceComponents = 4;
  document.activeLayers = [smartOriginal];
  const beforeRecovery = renders;
  const beforeScratch = scratchClosed;
  await controls.get("process").click();
  assert.equal(renders, beforeRecovery + 1, "a placement error must recover and reach rendering without clearing the cache");
  assert.equal(scratchClosed, beforeScratch, "placement recovery should avoid placing a Smart Object in another document");
  assert.equal(historyRollbacks, 1);
  assert.equal(sourceRasterizations, 1);
  assert.equal(document.layers.length, 2, "temporary source duplicates must be rolled back");
  assert.equal(smartOriginal.kind, "smartObject");
  assert.equal(insertedPixels[3], 77, "recovered pixels must preserve source alpha");
  assert.equal(insertedBounds.left, 4);
  assert.equal(insertedBounds.top, 6);
  smartReadMessage = "Photoshop Error. Code: -1. Message: Could not update smart object files ^0.";
  failSmartPlacement = true;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(renders, beforeRecovery + 2, "a placement error while isolating must recover through a source pixel copy");
  assert.equal(historyRollbacks, 2);
  assert.equal(scratchClosed, beforeScratch + 1);
  failSmartPlacement = false;
  smartReadMessage = "Photoshop Error. Code: -32005. Message: Could not place the document ^0.";
  failSourceRead = true;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(renders, beforeRecovery + 3, "an unreadable raster copy must be isolated as pixels");
  assert.equal(historyRollbacks, 3);
  assert.equal(scratchClosed, beforeScratch + 2);
  assert.equal(document.layers.length, 2);
  failSourceRead = false;
  failSourceRaster = true;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(renders, beforeRecovery + 3, "an unrecoverable rasterization failure must not start rendering");
  assert.equal(historyRollbacks, 4, "failed temporary edits must also roll back");
  assert.equal(document.layers.length, 2);
  assert.equal(document.activeLayers[0].id, smartOriginal.id);
  assert.equal(controls.get("status").dataset.state, "error");
  assert.equal(controls.get("process").disabled, false);
  assert.equal(autoCloseDocuments.size, 0, "no temporary capture documents may remain registered");
  assert.equal(context.isSmartObjectReadError({ number: -32005 }), true);
  assert.equal(context.isSmartObjectReadError(new Error("Permission denied")), false);
  assert.match(html, /id="mixNumber"[^>]+max="200"/);
  assert.match(html, /id="mix"[^>]+max="200"/);
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
  assert.deepEqual(manifest.requiredPermissions.network.domains, ["http://localhost:47837"]);
  assert.equal(controls.get("clearCache").disabled, false);
  photoshop.app.activeDocument = null;
  const originalFetch = context.fetch;
  let clears = 0;
  let finishClear;
  context.fetch = async (url, options) => {
    if (url.endsWith("/health")) return originalFetch(url);
    assert.ok(url.endsWith("/player-cache/clear"));
    assert.equal(options.method, "POST");
    assert.equal(modalDepth, 0, "cache clearing must not acquire a Photoshop modal scope");
    for (const id of ["process", "pano", "clearCache"])
      assert.equal(controls.get(id).disabled, true, "all action buttons must be disabled during cache clearing");
    clears++;
    await new Promise(resolve => { finishClear = resolve; });
    return { ok: true, status: 200, async json() { return { root: "C:/Player/cache/v1", bytes: 3 * 1024 * 1024 }; } };
  };
  const clearing = controls.get("clearCache").click();
  while (!finishClear) await Promise.resolve();
  await controls.get("clearCache").click();
  await controls.get("process").click();
  assert.equal(clears, 1, "busy clicks must not send another request");
  finishClear();
  await clearing;
  assert.match(controls.get("status").textContent, /DLSS Video Player cache cleared \(3\.0 MB freed\)/);
  assert.ok(log.childNodes.some(entry => /C:\/Player\/cache\/v1/.test(entry.textContent)), "the activity log must identify the player cache directory");
  assert.ok(log.childNodes.some(entry => /Second panorama render failed/.test(entry.textContent)), "clearing frames must keep previous activity entries");
  for (const [code, message, expected] of [
    [200, "", /0\.0 MB freed/],
    [404, "Unknown bridge endpoint", /reinstall DLSS Neural Mix v0\.2\.14/],
    [409, "An image is still processing.", /still processing/],
    [500, "Access to the cache file is denied.", /Access to the cache file/]
  ]) {
    context.fetch = async url => url.endsWith("/health") ? originalFetch(url) :
      { ok: code === 200, status: code, async json() { return { root: "C:/Player/cache/v1", bytes: 0 }; }, async text() { return message; } };
    await controls.get("clearCache").click();
    assert.match(controls.get("status").textContent, expected);
    assert.equal(controls.get("status").dataset.state, code === 200 ? "done" : "error");
    for (const id of ["process", "pano", "clearCache"]) assert.equal(controls.get(id).disabled, false);
    assert.equal(controls.get("clearCache").textContent, "Clear cache");
  }
  context.fetch = async () => { throw new Error("Permission denied to the url. Manifest entry not found."); };
  await assert.rejects(context.ensureBridge(), /Photoshop blocked the local renderer connection/);
  context.fetch = async () => { throw new Error("Connection refused"); };
  assert.equal(await context.bridgeVersion(), 0, "An offline bridge should trigger startup");
  console.log("Modal capture, Pano/Mix rendering, cache clearing, failure cleanup, Smart Object insertion, and bridge permissions passed.");
}).catch(error => { console.error(error); process.exitCode = 1; });
