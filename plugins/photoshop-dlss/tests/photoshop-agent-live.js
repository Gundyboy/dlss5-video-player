// Include after panel.js in an isolated development plugin only. Point that
// plugin's endpoint/allowlist at a private bridge port. This fixture never
// modifies a user's document and is not included in release packages.
globalThis.__dlssAgentFixture = { status: "creating" };
(async () => {
  const originalDocument = app.activeDocument;
  let fixture;
  try {
    await core.executeAsModal(async () => {
      fixture = await app.createDocument({ width: 512, height: 256, mode: "RGBColorMode", fill: "transparent", depth: 8, name: "DLSS agent verification" });
      const layer = await fixture.createPixelLayer({ name: "Owned agent test source" });
      const pixels = new Uint8Array(512 * 256 * 4);
      for (let y = 0; y < 256; y++) for (let x = 0; x < 512; x++) {
        const i = (y * 512 + x) * 4;
        pixels[i] = 40 + Math.floor(x / 4); pixels[i + 1] = 50 + Math.floor(y / 3);
        pixels[i + 2] = 100; pixels[i + 3] = 255;
      }
      const data = await imaging.createImageDataFromBuffer(pixels, { width: 512, height: 256, components: 4, colorSpace: "RGB", colorProfile: "sRGB IEC61966-2.1" });
      try { await imaging.putPixels({ documentID: fixture.id, layerID: layer.id, imageData: data, targetBounds: { left: 0, top: 0 }, replace: true }); }
      finally { data.dispose(); }
      globalThis.__dlssAgentFixture = { status: "ready", documentId: fixture.id, sourceLayerId: layer.id, originalLayerCount: fixture.layers.length };
    }, { commandName: "Create private DLSS agent fixture" });
    globalThis.__dlssAgentSelectSource = async () => core.executeAsModal(async () => {
      app.activeDocument = fixture;
      await action.batchPlay([{ _obj: "select", _target: [{ _ref: "layer", _id: globalThis.__dlssAgentFixture.sourceLayerId }], makeVisible: false }], {});
      return { documentId: fixture.id, layerId: fixture.activeLayers[0].id };
    }, { commandName: "Select private agent test source" });
    globalThis.__dlssAgentCleanup = async () => {
      stopAgentControl();
      const returnTo = app.activeDocument && app.activeDocument.id !== fixture.id ? app.activeDocument : originalDocument;
      await core.executeAsModal(async () => {
        await fixture.closeWithoutSaving();
        if (returnTo) app.activeDocument = returnTo;
      }, { commandName: "Close private DLSS agent fixture" });
      globalThis.__dlssAgentFixture.status = "closed";
      return globalThis.__dlssAgentFixture;
    };
    startAgentControl();
  } catch (error) { globalThis.__dlssAgentFixture = { status: "failed", error: String(error) }; }
})();
