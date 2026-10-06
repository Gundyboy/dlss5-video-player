const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const controls = new Map();
for (const id of ["mix", "mixNumber", "process", "pano", "clearCache", "status", "log"])
  controls.set(id, { dataset: {}, addEventListener() {}, childNodes: [], scrollTop: 0, clientHeight: 100, scrollHeight: 0,
    appendChild(node) { this.childNodes.push(node); }, removeChild() { this.childNodes.shift(); } });
const context = {
  require(name) { return name === "photoshop" ? {} : { entrypoints: { setup() {} }, storage: { localFileSystem: {} } }; },
  document: { getElementById(id) { return controls.get(id); }, createElement() { return { dataset: {} }; } },
  setInterval() { return 1; }, clearInterval() {}, Uint8Array, Float32Array, Date, console
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "..", "panel.js"), "utf8"), context);

function sourceFrame(width, height) {
  const rgb = new Uint8Array(width * height * 3);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const p = (y * width + x) * 3;
    rgb[p] = (x + 17 * y) % 200;
    rgb[p + 1] = (3 * x + y) % 200;
    rgb[p + 2] = (7 * x + 11 * y) % 200;
  }
  return rgb;
}

(async () => {
  for (const width of [64, 65, 127, 8192]) {
    const height = 3;
    const original = sourceFrame(width, height);
    const offset = Math.floor(width / 2);
    const shifted = original.slice();
    context.offsetRgbInPlace(shifted, width, height, offset);
    // Independent reference: the center seam sees both original edge columns.
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) for (let c = 0; c < 3; c++)
      assert.equal(shifted[(y * width + x) * 3 + c], original[(y * width + (x + offset) % width) * 3 + c]);
    context.offsetRgbInPlace(shifted, width, height, width - offset);
    assert.deepEqual(shifted, original, `offset must be reversible without resampling at width ${width}`);

    const requests = [];
    context.fetch = async (url, options) => {
      assert.ok(url.endsWith("/render"));
      assert.equal(Number(options.headers["X-Width"]), width, "Pano must not enlarge the renderer input");
      const input = new Uint8Array(options.body).slice();
      requests.push(input);
      return { ok: true, headers: { get() { return "hit"; } }, async arrayBuffer() { return input.buffer; } };
    };
    const result = await context.renderPanorama(original.slice(), width, height);
    assert.deepEqual(result, original, "identity rendering must preserve orientation and every pixel");
    assert.equal(requests.length, 2);
    assert.deepEqual(requests[0], original);
    const shiftedInput = original.slice();
    context.offsetRgbInPlace(shiftedInput, width, height, offset);
    assert.deepEqual(requests[1], shiftedInput);

    // Simulate a renderer that damages its own border. After restoring the
    // offset view those damaged pixels are in the middle and must be excluded.
    const direct = original.slice();
    const second = shiftedInput.slice();
    for (let y = 0; y < height; y++) for (const x of [0, width - 1]) for (let c = 0; c < 3; c++) {
      direct[(y * width + x) * 3 + c] = 255;
      second[(y * width + x) * 3 + c] = 0;
    }
    const corrected = context.blendPanoramaSeam(direct, second, width, height, offset);
    assert.deepEqual(corrected, original, "both artificial borders must be excluded from the final panorama");
  }

  const width = 512;
  const feathered = context.blendPanoramaSeam(new Uint8Array(width * 3).fill(80),
    new Uint8Array(width * 3).fill(120), width, 1, width / 2);
  assert.equal(feathered[0], 120);
  assert.equal(feathered[(width - 1) * 3], 120);
  assert.equal(feathered[width / 2 * 3], 80);
  for (let x = 0; x < width / 2; x++) {
    assert.equal(feathered[x * 3], feathered[(width - 1 - x) * 3], "feather must be symmetric across the wrap seam");
    if (x) assert.ok(feathered[(x - 1) * 3] - feathered[x * 3] >= 0 &&
      feathered[(x - 1) * 3] - feathered[x * 3] <= 2, "feather must transition smoothly without a hard join");
  }
  for (const mix of [50, 100, 200]) {
    const composed = context.composePixels(new Uint8Array([100, 100, 100, 77]),
      new Uint8Array([120, 120, 120]), 4, mix, 1);
    assert.equal(composed[3], 77, `source alpha must survive ${mix}% Mix`);
  }
  console.log("Panorama offset, orientation, border exclusion, smooth feather, 8192px width, and alpha tests passed.");
})().catch(error => { console.error(error); process.exitCode = 1; });
