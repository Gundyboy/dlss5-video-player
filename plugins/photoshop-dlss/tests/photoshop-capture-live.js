// Development plugin only. Test actual Photoshop duplicate/rasterize/history
// APIs with simulated read failures in an owned document. No renderer/cache IO.
// In the DEVELOPMENT copy of panel.js only, replace the capture read:
//   await imaging.getPixels({
// with:
//   await (globalThis.__dlssTestRead || imaging.getPixels)({
// Include this script after panel.js. Never package the hook or this script.
globalThis.__dlssCaptureDiagnostic = "running";
(async () => {
  const originalDocument = app.activeDocument;
  const nativeRead = options => imaging.getPixels(options);
  let scratch;
  const results = [];
  function check(value, message) { if (!value) throw new Error(message); }
  try {
    await core.executeAsModal(async () => {
      scratch = await app.createDocument({ width: 128, height: 128, mode: "RGBColorMode", fill: "transparent", depth: 8, name: "DLSS isolated capture verification" });
      const layer = await scratch.createPixelLayer({ name: "Test Smart Object" });
      const rgba = new Uint8Array(64 * 64 * 4);
      for (let p = 0; p < rgba.length; p += 4) { rgba[p] = 70; rgba[p + 1] = 140; rgba[p + 2] = 210; rgba[p + 3] = 160; }
      const data = await imaging.createImageDataFromBuffer(rgba, { width: 64, height: 64, components: 4, colorSpace: "RGB", colorProfile: "sRGB IEC61966-2.1" });
      try { await imaging.putPixels({ documentID: scratch.id, layerID: layer.id, imageData: data, targetBounds: { left: 4, top: 6 }, replace: true }); }
      finally { data.dispose(); }
      await convertActiveToSmartObject(scratch, layer.id, "Test Smart Object");
    }, { commandName: "Create DLSS capture fixture" });
    const source = scratch.activeLayers[0];
    const layerCount = scratch.layers.length;
    const documentCount = app.documents.length;
    const baseline = await captureSelectedLayer(scratch.id, source.id);
    const expected = new Uint8Array(baseline.source);
    for (const mode of ["placement", "isolated-placement", "pixel-isolation", "failure"]) {
      let injected = 0;
      globalThis.__dlssTestRead = async options => {
        if (options.documentID === scratch.id && options.layerID === source.id) {
          injected++;
          throw new Error(mode === "isolated-placement" ? "Could not update smart object files ^0." : "Photoshop Error. Code: -32005. Message: Could not place the document ^0.");
        }
        if (mode === "isolated-placement" && options.documentID !== scratch.id || mode === "pixel-isolation" && options.documentID === scratch.id) {
          injected++;
          throw new Error("Photoshop Error. Code: -32005. Message: Could not place the document ^0.");
        }
        if (mode === "failure") throw new Error("Unrecoverable pixel capture failure.");
        return await nativeRead(options);
      };
      let captured;
      let error;
      try { captured = await captureSelectedLayer(scratch.id, source.id); }
      catch (failure) { error = String(failure); }
      finally { delete globalThis.__dlssTestRead; }
      check(injected > 0, "The test failed to inject a Photoshop read error.");
      check(app.activeDocument.id === scratch.id && scratch.activeLayers[0].id === source.id, "Source selection was not restored.");
      check(scratch.activeLayers[0].kind === constants.LayerKind.SMARTOBJECT && scratch.layers.length === layerCount, "The original Smart Object or layer count changed.");
      check(app.documents.length === documentCount, "A temporary document leaked.");
      if (mode === "failure") check(error && error.includes("Unrecoverable pixel capture"), "An unrecoverable read must report failure.");
      else {
        check(!error, error);
        check(captured.width === baseline.width && captured.height === baseline.height && captured.components === baseline.components, "Recovered dimensions/components changed.");
        check(captured.bounds.left === baseline.bounds.left && captured.bounds.top === baseline.bounds.top, "Recovered layer offset changed.");
        check(captured.source.length === expected.length, "Recovered buffer size changed.");
        let maxDifference = 0;
        for (let i = 0; i < expected.length; i++) maxDifference = Math.max(maxDifference, Math.abs(captured.source[i] - expected[i]));
        check(maxDifference <= 1, "Recovered pixels or alpha differ from the direct read: " + maxDifference);
      }
      results.push({ mode, injected, success: true });
    }
    return { ok: true, version: panelVersion, results };
  } catch (error) { return { ok: false, error: String(error), stack: error.stack }; }
  finally {
    delete globalThis.__dlssTestRead;
    if (scratch) await core.executeAsModal(async () => {
      await scratch.closeWithoutSaving();
      if (originalDocument) app.activeDocument = originalDocument;
    }, { commandName: "Close DLSS capture fixture" });
  }
})().then(result => { globalThis.__dlssCaptureDiagnostic = result; }, error => { globalThis.__dlssCaptureDiagnostic = { ok: false, error: String(error) }; });
