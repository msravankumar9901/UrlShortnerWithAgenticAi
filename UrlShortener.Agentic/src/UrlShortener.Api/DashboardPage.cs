namespace UrlShortener.Api;

public static class DashboardPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Agentic SDLC — URL Shortener</title>
<style>
  :root { color-scheme: light; --ink:#14212b; --muted:#5c6b76; --line:#d5dee5; --paper:#f4f7f8; --card:#fff; --teal:#0f6e6e; }
  * { box-sizing: border-box; }
  body { margin:0; font: 15px/1.45 "Segoe UI", sans-serif; color: var(--ink); background: var(--paper); }
  header { background:#10242c; color:#f3f7f6; padding: 18px 28px; }
  header h1 { margin:0; font-size: 20px; font-weight: 650; }
  header p { margin: 4px 0 0; color:#b7c7c4; font-size: 13px; }
  main { display:grid; grid-template-columns: 320px 1fr; gap: 16px; padding: 16px; }
  section { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 14px 16px; }
  h2 { margin: 0 0 10px; font-size: 15px; }
  button, .run { font: inherit; }
  button { background: var(--teal); color: white; border: 0; border-radius: 6px; padding: 6px 10px; cursor: pointer; }
  button.reject { background: #8d3a3a; }
  button:disabled { opacity: .5; cursor: default; }
  .run { display:block; width:100%; text-align:left; background:#fff; border:1px solid var(--line); border-radius:8px; padding:8px 10px; margin: 0 0 8px; cursor:pointer; }
  .run.active { border-color: var(--teal); box-shadow: inset 3px 0 0 var(--teal); }
  .muted { color: var(--muted); font-size: 12px; }
  table { width:100%; border-collapse: collapse; }
  th, td { text-align:left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; font-size: 13px; }
  .pill { display:inline-block; border-radius: 99px; padding: 1px 8px; font-size: 12px; background:#e7eef0; }
  .Succeeded { background:#d9f0df; } .Failed, .Rejected, .RolledBack { background:#f8d7d7; }
  .Running, .Ready { background:#d9e8f8; } .WaitingForApproval { background:#f8efd0; }
  .Blocked, .SafeStopped, .Stale, .Pending { background:#ece7df; }
  pre { white-space: pre-wrap; word-break: break-word; background:#10242c; color:#e7f0ee; padding:10px; border-radius:8px; max-height: 280px; overflow:auto; font-size: 12px; }
  .metrics { display:grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
  .metric { background:#f7fbfb; border:1px solid var(--line); border-radius:8px; padding:8px; }
  .metric b { display:block; font-size: 18px; }
  .approval { border:1px solid #e2d2a4; background:#fffaf0; border-radius:8px; padding:8px; margin-bottom:8px; }
  @media (max-width: 900px) { main { grid-template-columns: 1fr; } .metrics { grid-template-columns: 1fr 1fr; } }
</style>
</head>
<body>
<header>
  <h1>Governed agent run dashboard</h1>
  <p>URL shortener product API and the agentic SDLC audit trail. Humans approve high-impact stages; agents do not deploy.</p>
</header>
<main>
  <section>
    <h2>Runs</h2>
    <div id="runs" class="muted">Loading…</div>
    <h2 style="margin-top:16px">Pending approvals</h2>
    <div id="approvals" class="muted">None.</div>
  </section>
  <div>
    <section>
      <h2 id="title">Select a run</h2>
      <div id="metrics"></div>
      <table id="stages"><tbody></tbody></table>
    </section>
    <section style="margin-top:16px">
      <h2>Audit trail</h2>
      <pre id="audit">No run selected.</pre>
    </section>
  </div>
</main>
<script>
let selected = null;
function pill(status) {
  const text = status || "";
  return `<span class="pill ${text.replace(/"/g,"")}">${text}</span>`;
}
async function refresh() {
  const runs = await (await fetch("/api/runs")).json();
  const host = document.getElementById("runs");
  host.innerHTML = runs.length ? "" : "<p class='muted'>No runs yet. Start one from the CLI.</p>";
  for (const run of runs) {
    const btn = document.createElement("button");
    btn.className = "run" + (run.runId === selected ? " active" : "");
    btn.innerHTML = `<strong>${run.name || run.runId}</strong><br><span class="muted">${run.runId}</span><br>${pill(run.status)} <span class="muted">${run.scenarioType || ""}</span>`;
    btn.onclick = () => { selected = run.runId; load(run.runId); refresh(); };
    host.appendChild(btn);
  }
  const approvals = await (await fetch("/api/approvals")).json();
  const box = document.getElementById("approvals");
  box.innerHTML = approvals.length ? "" : "<p class='muted'>None.</p>";
  for (const item of approvals) {
    const div = document.createElement("div");
    div.className = "approval";
    const req = item.request || {};
    div.innerHTML = `<strong>${req.stageName || req.stageId || "stage"}</strong><div class="muted">${item.runId}</div><p>${req.summary || ""}</p>`;
    const approve = document.createElement("button");
    approve.textContent = "Approve";
    approve.onclick = () => decide(item.runId, "approve");
    const reject = document.createElement("button");
    reject.className = "reject";
    reject.textContent = "Reject";
    reject.onclick = () => decide(item.runId, "reject");
    div.append(approve, document.createTextNode(" "), reject);
    box.appendChild(div);
  }
  if (!selected && runs[0]) { selected = runs[0].runId; load(selected); }
}
async function load(id) {
  const state = await (await fetch("/api/runs/" + id)).json();
  document.getElementById("title").textContent = (state.workflowName || id) + " — " + (state.status || "");
  const body = document.querySelector("#stages tbody");
  body.innerHTML = "";
  const stages = state.stages || {};
  for (const [name, stage] of Object.entries(stages)) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${name}</td><td>${pill(stage.status)}</td><td>${stage.retryCount ?? 0} retries</td><td>${stage.failureReason || stage.outputHash || ""}</td>`;
    body.appendChild(tr);
  }
  let metrics = null;
  const metricsResponse = await fetch("/api/runs/" + id + "/metrics");
  if (metricsResponse.ok) metrics = await metricsResponse.json();
  const metricsHost = document.getElementById("metrics");
  if (!metrics) metricsHost.innerHTML = "";
  else {
    const cards = [
      ["Success", pct(metrics.successRate)],
      ["Retries", metrics.retryCount],
      ["Rollbacks", metrics.rollbackCount],
      ["MTTR", metrics.mttrMs == null ? "n/a" : Math.round(metrics.mttrMs) + " ms"],
      ["E2E", Math.round(metrics.endToEndLatencyMs || 0) + " ms"],
      ["Approval wait", Math.round(metrics.approvalWaitMs || 0) + " ms"]
    ];
    metricsHost.innerHTML = `<div class="metrics">${cards.map(c => `<div class="metric"><span class="muted">${c[0]}</span><b>${c[1]}</b></div>`).join("")}</div>`;
  }
  const audit = await (await fetch("/api/runs/" + id + "/audit")).json();
  document.getElementById("audit").textContent = (audit || []).map(e => `${e.seq} ${e.eventType} ${e.stage || ""} ${e.message || ""}`).join("\n");
}
function pct(value) { return value == null ? "n/a" : Math.round(value * 100) + "%"; }
async function decide(runId, decision) {
  await fetch("/api/approvals/" + runId, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ decision, comment: "dashboard" }) });
  await refresh();
  if (selected) await load(selected);
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
