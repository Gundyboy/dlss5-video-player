const { app, core, action, imaging, constants } = require("photoshop");
const { entrypoints, shell, storage } = require("uxp");

// UXP's manifest allowlist rejects numeric loopback addresses in Photoshop.
const endpoint = "http://localhost:47837";
const panelVersion = "0.2.9";
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
  const detail = state === "error" ? `v${panelVersion}: ${message}` : message;
  status.textContent = detail;
  status.dataset.state = state;
  appendLog(detail, state);
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

appendLog(`DLSS Neural Mix v${panelVersion} ready. Select a layer, choose Mix, then apply.`);
function clampMix(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.max(0, Math.min(200, Math.round(parsed))) : 100;
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
  } catch (error) {
    if (/permission denied|manifest entry/i.test(String(error)))
      throw new Error("Photoshop blocked the local renderer connection. Reinstall the latest DLSS Neural Mix plugin, then restart Photoshop.");
    return 0;
  }
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

async function captureSelectedLayer(documentId, layerId) {
  // Photoshop requires the imaging read to run inside a modal scope. Copy the
  // pixels and release that scope before the potentially long neural render.
  return core.executeAsModal(async () => {
    const current = app.activeDocument;
    if (!current || current.id !== documentId ||
        current.activeLayers.length !== 1 || current.activeLayers[0].id !== layerId)
      throw new Error("The selected document or layer changed before capture.");
    const selected = current.activeLayers[0];
    async function readPixels(readDocumentId, readLayerId) {
      const captured = await imaging.getPixels({
        documentID: readDocumentId,
        layerID: readLayerId,
        componentSize: 8,
        colorSpace: "RGB",
        colorProfile: "sRGB IEC61966-2.1",
        applyAlpha: false
      });
      try {
        const width = captured.imageData.width;
        const height = captured.imageData.height;
        if (width < 64 || height < 64)
          throw new Error("DLSS Neural Rendering needs at least 64 × 64 pixels in the selected layer.");
        if (width > 8192 || height > 8192)
          throw new Error(`The selected layer is ${width} × ${height} pixels. DLSS Neural Rendering supports up to 8192 pixels per side. Resize the selected layer first.`);
        const pixelCount = width * height;
        if (!pixelCount || pixelCount > 64000000)
          throw new Error("The selected layer is empty or exceeds 64 megapixels.");
        const components = captured.imageData.components;
        if (components !== 3 && components !== 4)
          throw new Error("This layer could not be read as RGB pixels.");
        const source = await captured.imageData.getData({ chunky: true });
        if (source.length !== pixelCount * components)
          throw new Error("Photoshop returned an incomplete pixel buffer for this layer.");
        return { width, height, source, components, bounds: captured.sourceBounds };
      } finally {
        captured.imageData.dispose();
      }
    }
    try {
      return await readPixels(documentId, layerId);
    } catch (error) {
      if (!/could not update smart object files/i.test(String(error))) throw error;
      if (selected.kind === constants.LayerKind.NORMAL) {
        appendLog("The layered document blocked pixel capture; isolating the selected pixel layer.");
        const originalBounds = selected.boundsNoEffects;
        const layerWidth = originalBounds.right - originalBounds.left;
        const layerHeight = originalBounds.bottom - originalBounds.top;
        if (layerWidth > 8192 || layerHeight > 8192 || layerWidth * layerHeight > 64000000)
          throw new Error(`The selected layer is ${layerWidth} × ${layerHeight} pixels. DLSS Neural Rendering supports up to 8192 pixels per side and 64 megapixels.`);
        if (current.width > 8192 || current.height > 8192 || current.width * current.height > 64000000)
          throw new Error("The document canvas is too large for isolated capture. Use a smaller canvas for this layer.");
        let scratch;
        try {
          scratch = await app.createDocument({
            width: current.width, height: current.height, resolution: current.resolution,
            mode: "RGBColorMode", fill: "transparent", depth: 8,
            name: "DLSS temporary layer capture"
          });
          const copy = await selected.duplicate(scratch);
          for (const other of [...scratch.layers])
            if (other.id !== copy.id) await other.delete();
          const isolated = await readPixels(scratch.id, copy.id);
          if (isolated.width !== layerWidth || isolated.height !== layerHeight)
            throw new Error("The selected layer changed size when isolated for capture.");
          isolated.bounds = originalBounds;
          return isolated;
        } catch (fallbackError) {
          throw new Error("Photoshop could not read the selected pixel layer, even in an isolated document. Photoshop reported: " + String(fallbackError));
        } finally {
          try {
            if (scratch) await scratch.closeWithoutSaving();
          } finally {
            app.activeDocument = current;
            if (current.activeLayers.length !== 1 || current.activeLayers[0].id !== layerId)
              await action.batchPlay([{ _obj: "select", _target: [{ _ref: "layer", _id: layerId }], makeVisible: false }], {});
          }
        }
      }
      if (selected.kind !== constants.LayerKind.SMARTOBJECT)
        throw new Error("Photoshop could not read this layer type. Select a flattened pixel layer and retry. Photoshop reported: " + String(error));
      appendLog("Photoshop could not read the Smart Object directly; trying a temporary rasterized copy.");
      let temporary;
      try {
        temporary = await selected.duplicate();
        await temporary.rasterize(constants.RasterizeType.ENTIRELAYER);
        return await readPixels(documentId, temporary.id);
      } catch (fallbackError) {
        throw new Error("Photoshop could not read this Smart Object, even from a temporary rasterized copy. Relink any missing source file in the Layers panel, then retry. Photoshop reported: " + String(fallbackError));
      } finally {
        try {
          if (temporary) await temporary.delete();
        } finally {
          await action.batchPlay([{ _obj: "select", _target: [{ _ref: "layer", _id: layerId }], makeVisible: false }], {});
        }
      }
    }
  }, { commandName: "Read selected layer" });
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

async function insertResult(documentId, sourceId, pixels, width, height, bounds, name) {
  await core.executeAsModal(async () => {
    const current = app.activeDocument;
    if (!current || current.id !== documentId ||
        current.activeLayers.length !== 1 || current.activeLayers[0].id !== sourceId)
      throw new Error("The selected document or layer changed during rendering.");
    const source = current.activeLayers[0];
    const imageData = await imaging.createImageDataFromBuffer(pixels, {
      width, height, components: 4, colorSpace: "RGB",
      colorProfile: "sRGB IEC61966-2.1"
    });
    try {
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
      await layer.move(source, constants.ElementPlacement.PLACEBEFORE);
      await convertActiveToSmartObject(current, layer.id, name);
    } finally {
      imageData.dispose();
    }
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

const srgbToLinear = new Float32Array(256);
for (let value = 0; value < 256; value++) {
  const encoded = value / 255;
  srgbToLinear[value] = encoded <= 0.04045
    ? encoded / 12.92
    : Math.pow((encoded + 0.055) / 1.055, 2.4);
}

function linearToByte(value) {
  const linear = Math.max(0, Math.min(1, value));
  const encoded = linear <= 0.0031308
    ? linear * 12.92
    : 1.055 * Math.pow(linear, 1 / 2.4) - 0.055;
  return Math.round(encoded * 255);
}

function composePixels(source, neural, components, mix, pixelCount) {
  const output = new Uint8Array(pixelCount * 4);
  if (mix < 100) {
    const originalWeight = 100 - mix;
    for (let p = 0, s = 0, n = 0, o = 0; p < pixelCount; p++, s += components, n += 3, o += 4) {
      output[o] = Math.round((source[s] * originalWeight + neural[n] * mix) / 100);
      output[o + 1] = Math.round((source[s + 1] * originalWeight + neural[n + 1] * mix) / 100);
      output[o + 2] = Math.round((source[s + 2] * originalWeight + neural[n + 2] * mix) / 100);
      output[o + 3] = components === 4 ? source[s + 3] : 255;
    }
    return output;
  }
  if (mix === 100) {
    for (let p = 0, s = 0, n = 0, o = 0; p < pixelCount; p++, s += components, n += 3, o += 4) {
      output[o] = neural[n]; output[o + 1] = neural[n + 1]; output[o + 2] = neural[n + 2];
      output[o + 3] = components === 4 ? source[s + 3] : 255;
    }
    return output;
  }

  // Match the video player's 100–200% Mix: extend the model's luminance ratio
  // in linear light, bound it to 0.5–2×, and keep the neural frame's hue.
  const exponent = mix / 100 - 1;
  const floor = 1 / 512;
  for (let p = 0, s = 0, n = 0, o = 0; p < pixelCount; p++, s += components, n += 3, o += 4) {
    let nr = srgbToLinear[neural[n]], ng = srgbToLinear[neural[n + 1]], nb = srgbToLinear[neural[n + 2]];
    const rr = srgbToLinear[source[s]], rg = srgbToLinear[source[s + 1]], rb = srgbToLinear[source[s + 2]];
    const neuralLuma = 0.2126 * nr + 0.7152 * ng + 0.0722 * nb;
    const referenceLuma = 0.2126 * rr + 0.7152 * rg + 0.0722 * rb;
    const ratio = Math.max(0.5, Math.min(2, (neuralLuma + floor) / (referenceLuma + floor)));
    const scale = exponent === 1 ? ratio : Math.pow(ratio, exponent);
    nr *= scale; ng *= scale; nb *= scale;
    const peak = Math.max(nr, ng, nb);
    if (peak > 1) { nr /= peak; ng /= peak; nb /= peak; }
    output[o] = linearToByte(nr); output[o + 1] = linearToByte(ng); output[o + 2] = linearToByte(nb);
    output[o + 3] = components === 4 ? source[s + 3] : 255;
  }
  return output;
}

async function processSelectedLayer() {
  if (busy) return;
  busy = true;
  button.disabled = true;
  button.textContent = "Processing selected layer…";
  try {
    const mix = clampMix(number.value);
    number.value = slider.value = String(mix);
    const { document, layer } = selectedLayer();
    const name = `DLSS - ${mix}% Mix`;
    appendLog(`Selected layer: ${layer.name} (${layer.kind})`);
    if (mix === 0) {
      setStatus("Creating Smart Object…");
      await duplicateAtZero(document.id, layer.id, name);
      setStatus(`Done: ${name}`, "done");
      return;
    }

    setStatus("Reading selected layer…");
    const captured = await captureSelectedLayer(document.id, layer.id);
    const { width, height, source, components } = captured;
    appendLog(`Layer size: ${width} × ${height}`);
    const pixelCount = width * height;
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
    const output = composePixels(source, neural, components, mix, pixelCount);
    setStatus("Adding Smart Object…");
    await insertResult(document.id, layer.id, output, width, height, captured.bounds, name);
    setStatus(`Done: ${name}`, "done");
  } catch (error) {
    setStatus("Failed: " + (error && error.message ? error.message : String(error)), "error");
    console.error(error);
  } finally {
    stopProgress();
    busy = false;
    button.disabled = false;
    button.textContent = "Apply to selected layer";
  }
}

button.addEventListener("click", processSelectedLayer);
