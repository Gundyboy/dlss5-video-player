const { app, core, action, imaging, constants } = require("photoshop");
const { entrypoints, shell, storage } = require("uxp");

const endpoint = "http://127.0.0.1:47837";
const fs = storage.localFileSystem;
const slider = document.getElementById("mix");
const number = document.getElementById("mixNumber");
const button = document.getElementById("process");
const status = document.getElementById("status");
let busy = false;

entrypoints.setup({ panels: { dlssNeuralMix: { show() {} } } });

function setStatus(message) { status.textContent = message; }
function clampMix(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.max(0, Math.min(100, Math.round(parsed))) : 100;
}
slider.addEventListener("input", () => { number.value = slider.value; });
number.addEventListener("change", () => {
  const mix = clampMix(number.value);
  number.value = slider.value = String(mix);
});

async function healthy() {
  try {
    const response = await fetch(endpoint + "/health");
    return response.ok && response.headers.get("X-DLSS-Bridge") === "1";
  } catch (_) { return false; }
}

async function ensureBridge() {
  if (await healthy()) return;
  const pluginFolder = await fs.getPluginFolder();
  const nativeFolder = await pluginFolder.getEntry("native");
  const executable = await nativeFolder.getEntry("DLSSPhotoshopBridge.exe");
  const result = await shell.openPath(
    fs.getNativePath(executable),
    "Starts the local DLSS renderer bridge. Image pixels stay on this computer."
  );
  if (result) throw new Error("Could not start the local bridge: " + result);
  for (let attempt = 0; attempt < 40; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 250));
    if (await healthy()) return;
  }
  throw new Error("The local bridge did not start. Check %LOCALAPPDATA%\\DLSSPhotoshopBridge\\bridge.log.");
}

function selectedLayer() {
  const document = app.activeDocument;
  if (!document) throw new Error("Open a Photoshop document first.");
  if (document.activeLayers.length !== 1)
    throw new Error("Select exactly one layer.");
  return { document, layer: document.activeLayers[0] };
}

async function convertActiveToSmartObject(document, layerId, name) {
  await action.batchPlay([
    { _obj: "select", _target: [{ _ref: "layer", _id: layerId }], makeVisible: false },
    { _obj: "newPlacedLayer" }
  ], {});
  const smart = document.activeLayers[0];
  if (!smart) throw new Error("Photoshop did not return the new Smart Object.");
  smart.name = name;
}

async function insertResult(documentId, sourceId, imageData, bounds, name) {
  await core.executeAsModal(async () => {
    const current = app.activeDocument;
    if (!current || current.id !== documentId ||
        current.activeLayers.length !== 1 || current.activeLayers[0].id !== sourceId)
      throw new Error("The selected document or layer changed during rendering.");
    const source = current.activeLayers[0];
    const layer = await current.createPixelLayer({ name });
    layer.opacity = source.opacity;
    layer.blendMode = source.blendMode;
    await imaging.putPixels({
      documentID: documentId,
      layerID: layer.id,
      imageData,
      targetBounds: { left: bounds.left, top: bounds.top },
      replace: true
    });
    layer.move(source, constants.ElementPlacement.PLACEBEFORE);
    await convertActiveToSmartObject(current, layer.id, name);
  }, { commandName: name });
}

async function duplicateAtZero(documentId, sourceId, name) {
  await core.executeAsModal(async () => {
    const current = app.activeDocument;
    if (!current || current.id !== documentId ||
        current.activeLayers.length !== 1 || current.activeLayers[0].id !== sourceId)
      throw new Error("The selected document or layer changed.");
    const copy = await current.activeLayers[0].duplicate();
    await convertActiveToSmartObject(current, copy.id, name);
  }, { commandName: name });
}

async function processSelectedLayer() {
  if (busy) return;
  busy = true;
  button.disabled = true;
  let captured = null;
  let resultImage = null;
  try {
    const mix = clampMix(number.value);
    number.value = slider.value = String(mix);
    const { document, layer } = selectedLayer();
    const name = `DLSS - ${mix}% Mix`;
    if (mix === 0) {
      setStatus("Creating Smart Object…");
      await duplicateAtZero(document.id, layer.id, name);
      setStatus(`Done: ${name}`);
      return;
    }

    setStatus("Reading selected layer…");
    captured = await imaging.getPixels({
      documentID: document.id,
      layerID: layer.id,
      componentSize: 8,
      colorSpace: "RGB",
      colorProfile: "sRGB IEC61966-2.1",
      applyAlpha: false
    });
    const width = captured.imageData.width;
    const height = captured.imageData.height;
    const pixelCount = width * height;
    if (!pixelCount || pixelCount > 64000000)
      throw new Error("The selected layer is empty or exceeds 64 megapixels.");
    const source = await captured.imageData.getData({ chunky: true });
    const components = captured.imageData.components;
    if (components !== 3 && components !== 4)
      throw new Error("This layer could not be read as RGB pixels.");
    const rgb = new Uint8Array(pixelCount * 3);
    for (let i = 0, j = 0; i < source.length; i += components, j += 3) {
      rgb[j] = source[i]; rgb[j + 1] = source[i + 1]; rgb[j + 2] = source[i + 2];
    }

    setStatus(`Rendering ${width} × ${height}…`);
    await ensureBridge();
    const response = await fetch(endpoint + "/render", {
      method: "POST",
      headers: {
        "Content-Type": "application/octet-stream",
        "X-Width": String(width),
        "X-Height": String(height)
      },
      body: rgb.buffer
    });
    if (!response.ok) throw new Error(await response.text());
    const neural = new Uint8Array(await response.arrayBuffer());
    if (neural.length !== pixelCount * 3)
      throw new Error("The renderer returned the wrong number of pixels.");
    const output = new Uint8Array(pixelCount * 4);
    const originalWeight = 100 - mix;
    for (let p = 0, s = 0, n = 0, o = 0; p < pixelCount; p++, s += components, n += 3, o += 4) {
      output[o] = mix === 100 ? neural[n] : Math.round((source[s] * originalWeight + neural[n] * mix) / 100);
      output[o + 1] = mix === 100 ? neural[n + 1] : Math.round((source[s + 1] * originalWeight + neural[n + 1] * mix) / 100);
      output[o + 2] = mix === 100 ? neural[n + 2] : Math.round((source[s + 2] * originalWeight + neural[n + 2] * mix) / 100);
      output[o + 3] = components === 4 ? source[s + 3] : 255;
    }
    resultImage = await imaging.createImageDataFromBuffer(output, {
      width, height, components: 4, colorSpace: "RGB",
      colorProfile: "sRGB IEC61966-2.1"
    });
    setStatus("Adding Smart Object…");
    await insertResult(document.id, layer.id, resultImage, captured.sourceBounds, name);
    setStatus(`Done: ${name}`);
  } catch (error) {
    setStatus("Failed: " + (error && error.message ? error.message : String(error)));
    console.error(error);
  } finally {
    if (resultImage) resultImage.dispose();
    if (captured) captured.imageData.dispose();
    busy = false;
    button.disabled = false;
  }
}

button.addEventListener("click", processSelectedLayer);
