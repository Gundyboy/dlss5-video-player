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
const original = { id: 1, opacity: 100, blendMode: "normal" };
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
  constants: { ElementPlacement: { PLACEBEFORE: "before" } },
  core: {
    async executeAsModal(callback) {
      assert.equal(modalDepth, 0, "modal scopes must not nest");
      modalDepth++;
      try { return await callback(); }
      finally { modalDepth--; }
    }
  },
  action: {
    async batchPlay() {
      assert.equal(modalDepth, 1);
      document.activeLayers = [{ id: 3, name: "Smart Object" }];
    }
  },
  imaging: {
    async getPixels() {
      assert.equal(modalDepth, 1, "pixel capture needs a modal scope");
      captures++;
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
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
  assert.deepEqual(manifest.requiredPermissions.network.domains, ["http://localhost:47837"]);
  context.fetch = async () => { throw new Error("Permission denied to the url. Manifest entry not found."); };
  await assert.rejects(context.ensureBridge(), /Photoshop blocked the local renderer connection/);
  context.fetch = async () => { throw new Error("Connection refused"); };
  assert.equal(await context.bridgeVersion(), 0, "An offline bridge should trigger startup");
  console.log("Modal capture, non-modal render, Smart Object insertion, and bridge permission handling passed.");
}).catch(error => { console.error(error); process.exitCode = 1; });
