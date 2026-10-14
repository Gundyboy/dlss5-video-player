using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Fails(Action action, int status) {
    try { action(); throw new Exception("Expected rejection " + status); }
    catch (AgentControl.ApiError error) { Check(error.Status == status, "Wrong rejection"); }
}
var now = DateTimeOffset.UtcNow;
var agent = new AgentControl("test-token", () => now);
var request = new AgentControl.ApplyRequest(Guid.NewGuid().ToString("N"), 200, true);
Fails(() => agent.Submit(request, false), 503);
var instance = Guid.NewGuid().ToString("N");
var connect = JsonSerializer.SerializeToElement(agent.Connect(instance));
var session = connect.GetProperty("session").GetString()!;
var state = new AgentControl.PanelState(false, 7, 1, 1, 100, false, "0.2.15");
await agent.Poll(new(session, state, Wait: false));
Fails(() => agent.Connect(Guid.NewGuid().ToString("N")), 409);
Fails(() => agent.Submit(request with { Mix = 201 }, false), 400);
Fails(() => agent.Submit(request, true), 409);
await agent.Poll(new(session, state with { SelectedLayers = 2 }, Wait: false));
Fails(() => agent.Submit(request, false), 409);
await agent.Poll(new(session, state with { Busy = true }, Wait: false));
Fails(() => agent.Submit(request, false), 409);
await agent.Poll(new(session, state, Wait: false));
var waiting = agent.Poll(new(session, state), TimeSpan.FromSeconds(2));
Check(agent.Submit(request, false).Status == "queued", "Apply must queue");
var dispatch = JsonSerializer.SerializeToElement(await waiting);
Check(dispatch.GetProperty("job").GetProperty("Status").GetString() == "running", "Long poll must dispatch");
Check(agent.Submit(request, false).Status == "running", "Repeated submission must reuse its job");
Fails(() => agent.Submit(request with { Pano = false }, false), 409);
Fails(() => agent.Submit(request with { RequestId = Guid.NewGuid().ToString("N") }, false), 409);
var secondPoll = JsonSerializer.SerializeToElement(await agent.Poll(new(session, state, Wait: false)));
Check(secondPoll.GetProperty("job").ValueKind == JsonValueKind.Null, "An Apply must never dispatch twice");
now += TimeSpan.FromSeconds(46);
Check(agent.GetJob(request.RequestId).Status == "unknown", "Disconnect after dispatch must not claim failure");
Fails(() => agent.Submit(request with { RequestId = Guid.NewGuid().ToString("N") }, false), 503);
await agent.Poll(new(session, state, new(request.RequestId, JsonSerializer.SerializeToElement(new { ok = true, documentId = 7, sourceLayerId = 1, resultLayerId = 9 })), false));
Check(agent.GetJob(request.RequestId).Status == "completed", "Late completion must recover unknown state");
await agent.Poll(new(session, state, new(request.RequestId, JsonSerializer.SerializeToElement(new { ok = true })), false));
Check(agent.GetJob(request.RequestId).Result!.Value.GetProperty("resultLayerId").GetInt32() == 9, "Repeated acknowledgement must keep the first outcome");
var queued = request with { RequestId = Guid.NewGuid().ToString("N"), Mix = 0 };
agent.Submit(queued, false); now += TimeSpan.FromSeconds(31);
Check(agent.GetJob(queued.RequestId).Status == "failed", "Expired queued work must not apply later");
var late = JsonSerializer.SerializeToElement(await agent.Poll(new(session, state, Wait: false)));
Check(late.GetProperty("job").ValueKind == JsonValueKind.Null, "Expired work cannot dispatch");

// Exercise the actual HTTP boundary with a private listener and no Photoshop.
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
async Task<HttpResponseMessage> Send(string path, string? body = null, bool header = true, string? token = "test-token", string? origin = null) {
    var handling = Task.Run(async () => { var context = await listener.GetContextAsync(); try { context.Response.Headers["Access-Control-Allow-Origin"] = "*"; await agent.Handle(context, false); } finally { context.Response.Close(); } });
    using var message = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
    if (header) message.Headers.Add("X-DLSS-Agent", "1");
    if (token != null) message.Headers.Add("Authorization", "Bearer " + token);
    if (origin != null) message.Headers.Add("Origin", origin);
    if (body != null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await client.SendAsync(message); await handling; return response;
}
using (var response = await Send("agent/state", header: false)) Check((int)response.StatusCode == 403, "Native header required");
using (var response = await Send("agent/state", origin: "https://example.com")) {
    Check((int)response.StatusCode == 403, "Browser origins must be rejected");
    Check(!response.Headers.Contains("Access-Control-Allow-Origin"), "Agent API must not inherit wildcard CORS");
}
using (var response = await Send("agent/state", token: "wrong")) Check((int)response.StatusCode == 401, "Token required");
using (var response = await Send("agent/apply", "{\"mix\":100}")) Check((int)response.StatusCode == 400, "Missing parameters must be rejected");
using (var response = await Send("agent/apply", "{\"requestId\":\"" + Guid.NewGuid().ToString("N") + "\",\"mix\":100,\"pano\":\"false\"}")) Check((int)response.StatusCode == 400, "String booleans must be rejected");
using (var response = await Send("agent/state")) Check(response.IsSuccessStatusCode, "Authenticated status must work");
var staleSelectionApply = Send("agent/apply", "{\"requestId\":\"" + Guid.NewGuid().ToString("N") + "\",\"mix\":100,\"pano\":false}");
await Task.Delay(100);
await agent.Poll(new(session, state with { DocumentId = null, LayerId = null, SelectedLayers = 0 }, Wait: false));
using (var response = await staleSelectionApply) Check((int)response.StatusCode == 409, "Apply must refresh and reject a selection cleared after the previous poll");
listener.Stop();
Console.WriteLine("Agent control passed: selection/busy guards, long polling, single dispatch, idempotency, disconnects, late completion, expiry, strict JSON and native loopback authentication.");
