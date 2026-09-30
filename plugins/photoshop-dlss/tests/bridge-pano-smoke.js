// Run after Build-Installer.ps1. Requires the installed neural-only runner.
// Uses the actual packaged bridge and GPU without opening Photoshop documents.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { spawn, execFileSync } = require("node:child_process");

const endpoint = "http://localhost:47837";
const pluginRoot = path.resolve(__dirname, "..");
const executable = path.join(pluginRoot, "dist/bridge-build/DLSSPhotoshopBridge.exe");
const controls = new Map();
for (const id of ["mix", "mixNumber", "process", "pano", "status", "log"])
  controls.set(id, { dataset: {}, addEventListener() {}, childNodes: [], scrollTop: 0, clientHeight: 100, scrollHeight: 0,
    appendChild(node) { this.childNodes.push(node); }, removeChild() { this.childNodes.shift(); } });
const cacheStates = [];
const context = {
  require(name) { return name === "photoshop" ? {} : { entrypoints: { setup() {} }, storage: { localFileSystem: {} } }; },
  document: { getElementById(id) { return controls.get(id); }, createElement() { return { dataset: {} }; } },
  async fetch(url, options) {
    const response = await fetch(url, { ...options, signal: AbortSignal.timeout(600000) });
    if (url.endsWith("/render")) {
      const cache = response.headers.get("X-DLSS-Cache");
      cacheStates.push(cache);
      console.log(`GPU view ${cacheStates.length}: HTTP ${response.status}, cache ${cache}`);
    }
    return response;
  },
  setInterval, clearInterval, Uint8Array, Float32Array, Date, console
};
vm.runInNewContext(fs.readFileSync(path.join(pluginRoot, "panel.js"), "utf8"), context);

(async () => {
  try {
    await fetch(endpoint + "/health", { signal: AbortSignal.timeout(1000) });
    throw new Error("A bridge is already listening. Close the idle bridge before running this isolated test.");
  } catch (error) {
    if (/already listening/.test(error.message)) throw error;
  }
  const bridge = spawn(executable, [], { windowsHide: true, stdio: "ignore" });
  let launchError;
  bridge.on("error", error => { launchError = error; });
  try {
    let connected = false;
    for (let attempt = 0; attempt < 50; attempt++) {
      if (launchError) throw launchError;
      try {
        const response = await fetch(endpoint + "/health", { signal: AbortSignal.timeout(1000) });
        if (response.ok) {
          assert.equal(response.headers.get("X-DLSS-Bridge"), "3");
          connected = true;
          break;
        }
      } catch (_) { }
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    assert.ok(connected, "The rebuilt bridge must connect");
    const width = 512, height = 256;
    const rgb = new Uint8Array(width * height * 3);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const angle = (x + 0.5) * Math.PI * 2 / width;
      const p = (y * width + x) * 3;
      rgb[p] = Math.round(110 + 55 * Math.sin(angle));
      rgb[p + 1] = Math.round(90 + 45 * Math.cos(angle) + y / 8);
      rgb[p + 2] = 80 + y % 80;
    }
    const first = await context.renderPanorama(rgb.slice(), width, height);
    const second = await context.renderPanorama(rgb.slice(), width, height);
    assert.equal(first.length, width * height * 3);
    assert.deepEqual(first, second, "Cached panorama results must be identical");
    assert.deepEqual(cacheStates, ["miss", "miss", "hit", "hit"],
      "Both actual GPU views must be reused when applying another Mix");
    console.log("Real GPU panorama passed: two verified renders, two cache hits, identical combined pixels.");
  } finally {
    // Terminate only the process tree started by this test, after its requests.
    if (bridge.pid && bridge.exitCode === null) {
      try { execFileSync("taskkill", ["/PID", String(bridge.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" }); }
      catch (_) { }
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
