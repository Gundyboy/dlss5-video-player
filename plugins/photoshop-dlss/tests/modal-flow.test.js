const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const controls = new Map();
for (const id of ["mix", "mixNumber", "process", "status", "log"])
  controls.set(id, { value: "50", dataset: {}, addEventListener(event, callback) { this[event] = callback; } });
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
let smartCopyDeleted = false;
let smartRasterized = false;
let failRasterRead = false;
const original = { id: 1, opacity: 100, blendMode: "normal" };
const smartOriginal = {
  id: 4, name: "Linked artwork", kind: "smartObject", opacity: 100, blendMode: "normal",
  async duplicate() {
    assert.equal(modalDepth, 1);
    const copy = {
      id: 5,
      async rasterize(target) {
        assert.equal(target, "entireLayer");
        smartRasterized = true;
      },
      async delete() {
        assert.equal(modalDepth, 1);
        smartCopyDeleted = true;
      }
    };
    document.activeLayers = [copy];
    return copy;
  }
};
const document = {
  id: 7,
  activeLayers: [original],
  async createPixelLayer({ name }) {
    assert.equal(modalDepth, 1);
    return { id: 2, name, move() {} };
  }
};
const photoshop = {
  app: { activeDocument: document },
  constants: {
    ElementPlacement: { PLACEBEFORE: "before" },
    LayerKind: { SMARTOBJECT: "smartObject" },
    RasterizeType: { ENTIRELAYER: "entireLayer" }
  },
  core: {
    async executeAsModal(callback) {
      assert.equal(modalDepth, 0, "modal scopes must not nest");
      modalDepth++;
      try { return await callback(); }
      finally { modalDepth--; }
    }
  },
  action: {
    async batchPlay(commands) {
      assert.equal(modalDepth, 1);
      if (commands.some(command => command._obj === "newPlacedLayer"))
        document.activeLayers = [{ id: 3, name: "Smart Object" }];
      else if (commands[0]._target[0]._id === smartOriginal.id)
        document.activeLayers = [smartOriginal];
    }
  },
  imaging: {
    async getPixels({ layerID }) {
      assert.equal(modalDepth, 1, "pixel capture needs a modal scope");
      captures++;
      if (layerID === smartOriginal.id)
        throw new Error("Photoshop Error. Code: -1. Message: Could not update smart object files ^0.");
      if (layerID === 5 && failRasterRead)
        throw new Error("Photoshop could not rasterize the missing linked file.");
      return {
        sourceBounds: { left: 4, top: 6 },
        imageData: {
          width: 64, height: 64, components: 3,
          async getData() {
            assert.equal(modalDepth, 1, "pixel reading needs a modal scope");
            return new Uint8Array(64 * 64 * 3).fill(100);
          },
          dispose() { capturedDisposed = true; }
        }
      };
    },
    async createImageDataFromBuffer(pixels) {
      assert.equal(modalDepth, 1, "result image creation needs a modal scope");
      return { pixels, dispose() { outputDisposed = true; } };
    },
    async putPixels({ imageData }) {
      assert.equal(modalDepth, 1, "pixel insertion needs a modal scope");
      insertedPixels = imageData.pixels;
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
      return { ok: true, headers: { get() { return "2"; } } };
    if (url.endsWith("/render")) {
      assert.equal(modalDepth, 0, "neural rendering must not hold Photoshop modal");
      assert.equal(capturedDisposed, true, "capture must be released before rendering");
      renders++;
      return { ok: true, async arrayBuffer() { return new Uint8Array(64 * 64 * 3).fill(200).buffer; } };
    }
    throw new Error(`Unexpected request: ${url}`);
  },
  setInterval() { return 1; },
  clearInterval() {},
  console,
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
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  assert.equal(controls.get("status").dataset.state, "done");
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(captures, 3, "a failed Smart Object read should retry on the temporary raster layer");
  assert.equal(renders, 2);
  assert.equal(smartRasterized, true);
  assert.equal(smartCopyDeleted, true, "the temporary layer must be removed");
  assert.equal(document.activeLayers[0].name, "DLSS - 50% Mix");
  assert.equal(controls.get("status").dataset.state, "done");
  failRasterRead = true;
  smartCopyDeleted = false;
  document.activeLayers = [smartOriginal];
  await controls.get("process").click();
  assert.equal(smartCopyDeleted, true, "a failed fallback must remove its temporary layer");
  assert.equal(document.activeLayers[0].id, smartOriginal.id, "a failed fallback must reselect the source");
  assert.equal(renders, 2, "a failed capture must never reach the renderer");
  assert.match(controls.get("status").textContent, /Relink any missing source file/);
  assert.equal(context.clampMix(250), 200, "Mix must clamp to the renderer's 200% maximum");
  const enhanced = context.composePixels(
    new Uint8Array([100, 100, 100, 77]), new Uint8Array([150, 150, 150]), 4, 200, 1);
  assert.deepEqual(Array.from(enhanced), [205, 205, 205, 77],
    "200% Mix must use the player's guarded linear-light luminance extension");
  const html = fs.readFileSync(path.join(__dirname, "..", "index.html"), "utf8");
  assert.match(html, /id="mixNumber"[^>]+max="200"/);
  assert.match(html, /id="mix"[^>]+max="200"/);
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
  assert.deepEqual(manifest.requiredPermissions.network.domains, ["http://localhost:47837"]);
  context.fetch = async () => { throw new Error("Permission denied to the url. Manifest entry not found."); };
  await assert.rejects(context.ensureBridge(), /Photoshop blocked the local renderer connection/);
  context.fetch = async () => { throw new Error("Connection refused"); };
  assert.equal(await context.bridgeVersion(), 0, "An offline bridge should trigger startup");
  console.log("Modal capture, non-modal render, Smart Object insertion, and bridge permission handling passed.");
}).catch(error => { console.error(error); process.exitCode = 1; });
