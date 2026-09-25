// Load only in a separate development plugin, after panel.js. Exercises the
// actual Photoshop imaging API and local renderer; closes only its scratch doc.
globalThis.__dlssDiagnostic = "running";
(async () => {
  let scratch;
  try {
    let sourceId;
    await core.executeAsModal(async () => {
      scratch = await app.createDocument({ width: 512, height: 512, resolution: 72, name: "DLSS temporary render test" });
      const layer = await scratch.createPixelLayer({ name: "Test source" });
      sourceId = layer.id;
      const data = new Uint8Array(512 * 512 * 3);
      for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
        const p = (y * 512 + x) * 3;
        data[p] = x % 256; data[p + 1] = y % 256; data[p + 2] = (x + y) % 256;
      }
      const pixels = await imaging.createImageDataFromBuffer(data, {
        width: 512, height: 512, components: 3, colorSpace: "RGB", colorProfile: "sRGB IEC61966-2.1"
      });
      try { await imaging.putPixels({ documentID: scratch.id, layerID: sourceId, imageData: pixels, replace: true }); }
      finally { pixels.dispose(); }
    }, { commandName: "Create temporary DLSS test" });
    const results = [];
    for (const mix of [50, 100, 200, 0]) {
      await core.executeAsModal(async () => {
        await action.batchPlay([{ _obj: "select", _target: [{ _ref: "layer", _id: sourceId }], makeVisible: false }], {});
      }, { commandName: "Select temporary source" });
      number.value = slider.value = String(mix);
      const started = Date.now();
      await processSelectedLayer();
      const output = scratch.activeLayers[0];
      if (status.dataset.state !== "done" || output.kind !== "smartObject" || output.name !== `DLSS - ${mix}% Mix`)
        throw new Error(status.textContent);
      results.push({ mix, name: output.name, kind: output.kind, milliseconds: Date.now() - started });
    }
    return JSON.stringify({ ok: true, version: panelVersion, results,
      layers: scratch.layers.map(layer => ({ name: layer.name, kind: layer.kind })),
      log: Array.from(log.childNodes).map(node => node.textContent) });
  } catch (error) { return JSON.stringify({ ok: false, error: String(error), stack: error.stack }); }
  finally {
    if (scratch) await core.executeAsModal(async () => { await scratch.closeWithoutSaving(); },
      { commandName: "Close temporary DLSS test" });
  }
})().then(result => { globalThis.__dlssDiagnostic = result; }, error => { globalThis.__dlssDiagnostic = String(error); });
