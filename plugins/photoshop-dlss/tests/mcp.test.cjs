const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

(async () => {
  const fixture = fs.mkdtempSync(path.join(__dirname, '../dist/mcp-test-'));
  fs.writeFileSync(path.join(fixture, 'token.txt'), 'private-fixture-token');
  let submissions = 0;
  let job;
  const requests = [];
  const server = http.createServer(async (req, res) => {
    try {
      assert.equal(req.headers['x-dlss-agent'], '1');
      assert.equal(req.headers.authorization, 'Bearer private-fixture-token');
      requests.push(req.url);
      const parts = [];
      for await (const part of req) parts.push(part);
      const route = req.url.split('?')[0];
      res.setHeader('Content-Type', 'application/json');
      if (route === '/agent/state') res.end(JSON.stringify({ connected: true, state: { busy: false, selectedLayers: 1, documentId: 7, layerId: 1 } }));
      else if (route === '/agent/apply') {
        const body = JSON.parse(Buffer.concat(parts).toString());
        assert.equal(typeof body.mix, 'number'); assert.equal(typeof body.pano, 'boolean');
        assert.ok(Number.isInteger(body.mix) && body.mix >= 0 && body.mix <= 200);
        assert.ok(Number(req.headers['content-length']) > 0, 'Submission must have a bounded Content-Length');
        submissions++;
        job = { id: body.requestId, status: 'queued', mix: body.mix, pano: body.pano };
        res.end(JSON.stringify(job));
        setTimeout(() => { job = { ...job, status: 'completed', result: { ok: true, documentId: 7, sourceLayerId: 1, resultLayerId: 9, name: `DLSS - ${body.mix}% Mix` } }; }, 50);
      } else if (route === '/agent/jobs/' + job?.id) res.end(JSON.stringify(job));
      else { res.statusCode = 404; res.end(JSON.stringify({ error: 'Unknown job' })); }
    } catch (error) { res.statusCode = 500; res.end(JSON.stringify({ error: error.message })); }
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const child = spawn(path.resolve(__dirname, '../dist/agent-build/DLSSNeuralMixMcp.exe'), [], {
    windowsHide: true, env: { ...process.env, DLSS_NEURAL_MIX_BRIDGE_URL: `http://127.0.0.1:${server.address().port}/`, DLSS_NEURAL_MIX_TOKEN_FILE: path.join(fixture, 'token.txt') },
    stdio: ['pipe', 'pipe', 'pipe']
  });
  child.stderr.on('data', () => {});
  const pending = new Map(); let sequence = 0; let buffer = '';
  child.stdout.on('data', chunk => {
    buffer += chunk;
    let split;
    while ((split = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, split); buffer = buffer.slice(split + 1);
      if (!line.trim()) continue;
      const message = JSON.parse(line); assert.equal(message.jsonrpc, '2.0', 'Stdout must contain only protocol messages');
      if (pending.has(message.id)) { const callback = pending.get(message.id); pending.delete(message.id); callback(message); }
    }
  });
  function rpc(method, params) {
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('MCP timeout: ' + method)); }, 10000);
      pending.set(id, response => { clearTimeout(timer); resolve(response); });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  }
  try {
    const initialize = await rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'dlss-regression', version: '1' } });
    assert.ok(initialize.result?.capabilities.tools, JSON.stringify(initialize));
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const listed = await rpc('tools/list', {});
    const tools = listed.result.tools;
    assert.deepEqual(tools.map(tool => tool.name).sort(), ['dlss_apply', 'dlss_status']);
    const apply = tools.find(tool => tool.name === 'dlss_apply');
    assert.deepEqual(apply.inputSchema.required.sort(), ['mix', 'pano']);
    assert.equal(apply.inputSchema.properties.mix.type, 'integer');
    assert.equal(apply.inputSchema.properties.pano.type, 'boolean');
    assert.equal(tools.find(tool => tool.name === 'dlss_status').annotations.readOnlyHint, true);
    const status = await rpc('tools/call', { name: 'dlss_status', arguments: {} });
    assert.equal(JSON.parse(status.result.content[0].text).connected, true);
    const applied = await rpc('tools/call', { name: 'dlss_apply', arguments: { mix: 200, pano: true } });
    assert.ok(!applied.result.isError, JSON.stringify(applied));
    const result = JSON.parse(applied.result.content[0].text);
    assert.equal(result.status, 'completed'); assert.equal(result.mix, 200); assert.equal(result.pano, true);
    assert.equal(result.result.resultLayerId, 9); assert.equal(result.result.sourceLayerId, 1);
    const checked = await rpc('tools/call', { name: 'dlss_status', arguments: { job_id: result.id } });
    assert.equal(JSON.parse(checked.result.content[0].text).result.resultLayerId, 9);
    for (const invalid of [{ mix: 201, pano: false }, { mix: 100, pano: 'false' }]) {
      const response = await rpc('tools/call', { name: 'dlss_apply', arguments: invalid });
      assert.ok(response.error || response.result.isError, 'Invalid settings must fail');
    }
    assert.equal(submissions, 1, 'Checking status and invalid settings must never apply again');
    assert.ok(requests.includes('/agent/state?fresh=1'));
    console.log('MCP stdio passed: legacy initialization, tool schemas, typed settings, result IDs, status polling, validation and no duplicate Apply.');
  } finally {
    child.kill(); server.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
