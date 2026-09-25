const { app, core, action, imaging, constants } = require("photoshop");
const { entrypoints, shell, storage } = require("uxp");

const endpoint = "http://127.0.0.1:47837";
const fs = storage.localFileSystem;
const slider = document.getElementById("mix");
const number = document.getElementById("mixNumber");
const button = document.getElementById("process");
const status = document.getElementById("status");
const log = document.getElementById("log");
let busy = false;
let progress = null;

entrypoints.setup({ panels: { dlssNeuralMix: { show() {} } } });

function appendLog(message, state = "normal") {
  const followTail = log.scrollTop + log.clientHeight >= log.scrollHeight - 20;
  const entry = document.createElement("div");
  entry.className = "log-entry";
  entry.dataset.state = state;
  entry.textContent = `${new Date().toLocaleTimeString()}  ${message}`;
  log.appendChild(entry);
  while (log.childNodes.length > 100) log.removeChild(log.firstChild);
  if (followTail) log.scrollTop = log.scrollHeight;
}

function setStatus(message, state = "working") {
  status.textContent = message;
  status.dataset.state = state;
  appendLog(message, state);
}

function elapsedText(seconds) {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

const bridgePhases = {
  receiving: "Receiving image pixels",
  checkingCache: "Checking render cache",
  rendering: "Running DLSS Neural Rendering",
  decoding: "Decoding rendered image",
  sending: "Returning rendered pixels"
};

async function pollBridgeStatus(current) {
  if (!current.active || current.polling) return;
  current.polling = true;
  try {
    const response = await fetch(endpoint + "/status");
    if (response.status === 404 && current.active) {
      clearInterval(current.pollTimer);
      appendLog("Bridge stage updates need the new bridge version. Elapsed time remains visible.");
      return;
    }
    if (!response.ok || !current.active) return;
    const data = await response.json();
    const next = data.busy && bridgePhases[data.phase];
    if (next && next !== current.stage && current.active) {
      current.stage = next;
      appendLog(next);
    }
  } catch (_) {
    // Older bridge versions have no status endpoint; the elapsed timer remains.
  } finally { current.polling = false; }
}

function startProgress() {
  const current = {
    active: true,
    polling: false,
    started: Date.now(),
    nextLog: Date.now() + 30000,
    stage: "Waiting for renderer response"
  };
  progress = current;
  status.dataset.state = "working";
  appendLog("Renderer request sent");
  current.timer = setInterval(() => {
    if (!current.active) return;
    const elapsed = Math.floor((Date.now() - current.started) / 1000);
    status.textContent = `${current.stage} · ${elapsedText(elapsed)} elapsed`;
    if (Date.now() >= current.nextLog) {
      appendLog(`${current.stage} (${elapsedText(elapsed)} elapsed)`);
      current.nextLog = Date.now() + 30000;
    }
  }, 1000);
  current.pollTimer = setInterval(() => { pollBridgeStatus(current); }, 1500);
}

function stopProgress() {
  if (!progress) return;
  progress.active = false;
  clearInterval(progress.timer);
  clearInterval(progress.pollTimer);
  progress = null;
}

appendLog("Ready. Select a layer, choose Mix, then apply.");
function clampMix(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.max(0, Math.min(100, Math.round(parsed))) : 100;
}
slider.addEventListener("input", () => { number.value = slider.value; });
number.addEventListener("input", () => {
  if (number.value !== "" && Number.isFinite(Number(number.value)))
    slider.value = String(clampMix(number.value));
});
number.addEventListener("change", () => {
  const mix = clampMix(number.value);
  number.value = slider.value = String(mix);
});

async function bridgeVersion() {
  try {
    const response = await fetch(endpoint + "/health");
    return response.ok ? Number(response.headers.get("X-DLSS-Bridge")) || 0 : 0;
  } catch (_) { return 0; }
}

async function ensureBridge() {
  appendLog("Checking local renderer bridge");
  const runningVersion = await bridgeVersion();
  if (runningVersion === 2) {
    appendLog("Renderer bridge connected");
    return;
  }
  if (runningVersion > 0)
    throw new Error("An older DLSS bridge is still running. Close DLSSPhotoshopBridge.exe in Task Manager, then try again.");
  appendLog("Starting local renderer bridge");
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
    if (await bridgeVersion() === 2) {
      appendLog("Renderer bridge connected");
      return;
    }
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
  button.textContent = "Processing selected layer…";
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
      setStatus(`Done: ${name}`, "done");
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
    appendLog(`Layer size: ${width} × ${height}`);
    if (width < 64 || height < 64)
      throw new Error("DLSS Neural Rendering needs at least 64 × 64 pixels in the selected layer.");
    if (width > 8192 || height > 8192)
      throw new Error("DLSS Neural Rendering supports up to 8192 × 8192 pixels. Resize the selected layer first.");
    const pixelCount = width * height;
    if (!pixelCount || pixelCount > 64000000)
      throw new Error("The selected layer is empty or exceeds 64 megapixels.");
    const source = await captured.imageData.getData({ chunky: true });
    const components = captured.imageData.components;
    if (components !== 3 && components !== 4)
      throw new Error("This layer could not be read as RGB pixels.");
    if (source.length !== pixelCount * components)
      throw new Error("Photoshop returned an incomplete pixel buffer for this layer.");
    const rgb = new Uint8Array(pixelCount * 3);
    for (let i = 0, j = 0; i < source.length; i += components, j += 3) {
      rgb[j] = source[i]; rgb[j + 1] = source[i + 1]; rgb[j + 2] = source[i + 2];
    }

    setStatus(`Preparing ${width} × ${height} render…`);
    await ensureBridge();
    startProgress();
    const response = await fetch(endpoint + "/render", {
      method: "POST",
      headers: {
        "Content-Type": "application/octet-stream",
        "X-Width": String(width),
        "X-Height": String(height)
      },
      body: rgb.buffer
    });
    if (!response.ok) {
      stopProgress();
      throw new Error(await response.text());
    }
    const neural = new Uint8Array(await response.arrayBuffer());
    stopProgress();
    if (neural.length !== pixelCount * 3)
      throw new Error("The renderer returned the wrong number of pixels.");
    setStatus("Mixing rendered and original pixels…");
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
    setStatus(`Done: ${name}`, "done");
  } catch (error) {
    setStatus("Failed: " + (error && error.message ? error.message : String(error)), "error");
    console.error(error);
  } finally {
    stopProgress();
    if (resultImage) resultImage.dispose();
    if (captured) captured.imageData.dispose();
    busy = false;
    button.disabled = false;
    button.textContent = "Apply to selected layer";
  }
}

button.addEventListener("click", processSelectedLayer);
