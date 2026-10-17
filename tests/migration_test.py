#!/usr/bin/env python3
"""End-to-end test of the migration: fake hosted API (620 MADE-UP incidents) -> tools/export_hosted.py -> `import` command
in the container -> checked through MongoDB and the real MCP server. Uses the separate database oncall_local_test and
never touches real incident data. Run after `docker compose up -d --build`. Standard library only."""
import datetime as dt, json, os, stat, subprocess, sys, tempfile, threading, time, urllib.parse
from http.server import BaseHTTPRequestHandler, HTTPServer

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTAINER = os.environ.get("ONCALL_CONTAINER", "oncall-local-mcp")   # lets the test run against a separate container
PORT = 5190
TOTAL = 620
results = []
def check(name, ok, detail=""):
    results.append(bool(ok)); print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))

# ---------- made-up hosted data ----------
TEMPLATES = [
    ("payment-service", "Customers reported that card payments were accepted but did not show in their balance. Logs showed success; the downstream provider rejected the sync."),
    ("job-runner", "Nightly batch jobs did not run after the scheduler host was rebooted and did not start automatically."),
    ("api-gateway", "Clients failed the TLS handshake because the certificate had expired and automatic renewal had been disabled."),
    ("search-service", "Search returned nothing after an index rebuild failed halfway and left the alias on an empty index."),
    ("order-service", "Order lookups became very slow at peak because a new query filtered on a column with no index."),
    ("notification-service", "Emails were delayed by hours because the queue consumer stopped after a timeout and was not restarted."),
    ("web-frontend", "Users saw a broken page after a release because the CDN kept serving the previous script bundle."),
    ("billing-service", "Some customers received two invoices because the billing job ran twice after a retry during a timeout."),
    ("auth-service", "Some users could not sign in because token expiry errors appeared right after login; the server clock was ahead."),
    ("document-service", "Uploads failed with access denied after a bucket policy change removed write permission for the service role."),
    ("data-pipeline", "A migration script locked a large table and blocked writes for about ten minutes."),
    ("report-service", "Report exports timed out for large accounts because the query loaded everything into memory without paging."),
]
base = dt.datetime(2026, 9, 1, 12, 0, 0, tzinfo=dt.timezone.utc)
incidents = []
for i in range(TOTAL):
    svc, text = TEMPLATES[i % len(TEMPLATES)]
    created = base - dt.timedelta(minutes=i, milliseconds=(i * 7) % 1000)
    incidents.append({
        "id": f"{0x65a000000000000000000000 + i:024x}",
        "title": f"{svc} incident #{i}", "serviceName": svc, "environment": "production", "severity": ["sev2", "sev3", "sev4"][i % 3],
        "description": f"{text} (case {i})", "score": None,
        "createdAt": created.strftime("%Y-%m-%dT%H:%M:%S.") + f"{created.microsecond // 1000:03d}0000Z",
        "resolution": {"rootCause": f"cause {i}", "summary": f"fixed {i}", "stepsTaken": [f"step a {i}", f"step b {i}"], "resolvedBy": "on-call"}})
UNIQUE = incidents[0]   # make the first one easy to recognise
UNIQUE.update(title="Kiosk zebra widget outage", serviceName="kiosk-service",
              description="The lobby kiosk zebra widget stopped rendering because its licence file expired. Visitors saw a blank panel until the licence was renewed.")
incidents[10]["description"] = "   "                      # no description: must be skipped
incidents[11]["id"] = "not-a-valid-id"                      # invalid id: must be skipped
incidents[12]["description"] = "long " * 1400               # 7,000 characters: stored in full, embedded from the first 6,000
incidents[13]["resolution"] = None                          # unresolved incident
EXPECTED = TOTAL - 2

# ---------- fake hosted API ----------
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, code, obj):
        b = json.dumps(obj).encode(); self.send_response(code); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if self.path == "/oauth/token" and body.get("client_secret") == "fake-secret":
            return self._send(200, {"access_token": "fake-token", "expires_in": 3600})
        self._send(401, {})
    def do_GET(self):
        if self.headers.get("Authorization") != "Bearer fake-token": return self._send(401, {})
        u = urllib.parse.urlparse(self.path); qs = urllib.parse.parse_qs(u.query)
        limit = min(int(qs.get("limit", ["100"])[0]), 500)
        rows = sorted(incidents, key=lambda x: x["createdAt"], reverse=True)
        if "to" in qs:
            to = qs["to"][0].rstrip("Z")
            rows = [r for r in rows if r["createdAt"].rstrip("Z")[:23] <= to[:23]]
        self._send(200, rows[:limit])
class Srv(HTTPServer): allow_reuse_address = True
threading.Thread(target=Srv(("127.0.0.1", PORT), H).serve_forever, daemon=True).start()

# ---------- helpers ----------
DB = "oncall_local_test"
def mongosh(js):
    r = subprocess.run(["docker", "exec", "oncall-local-mongodb", "mongosh", "--quiet", "--eval", js], capture_output=True, text=True)
    return r.stdout.strip()
def count(): return int(mongosh(f"print(db.getSiblingDB('{DB}').incidents.countDocuments())") or -1)
def run_import(path, *flags):
    with open(path) as f:
        return subprocess.run(["docker", "exec", "-i", "-e", f"MONGO_DATABASE={DB}", CONTAINER, "dotnet", "OnCallHelperLocal.dll", "import", *flags],
                              stdin=f, capture_output=True, text=True)

mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")

# ---------- 1. export ----------
work = tempfile.mkdtemp()
env = {**os.environ, "ONCALL_API_BASE_URL": f"http://127.0.0.1:{PORT}", "ONCALL_AUTH0_DOMAIN": f"http://127.0.0.1:{PORT}",
       "ONCALL_AUTH0_CLIENT_ID": "fake-id", "ONCALL_AUTH0_CLIENT_SECRET": "fake-secret"}
for k in ("ONCALL_API_TOKEN", "ONCALL_AUTH0_AUDIENCE"): env.pop(k, None)
ex = subprocess.run([sys.executable, os.path.join(REPO, "tools", "export_hosted.py")], cwd=work, env=env, capture_output=True, text=True)
files = [f for f in os.listdir(os.path.join(work, "exports"))] if os.path.isdir(os.path.join(work, "exports")) else []
check("export finishes and writes one file", ex.returncode == 0 and len(files) == 1, (ex.stdout.strip().splitlines() or [ex.stderr[:100]])[-1][:90])
path = os.path.join(work, "exports", files[0]) if files else ""
data = json.load(open(path)) if path else []
check("export pages past the 500 cap and gets every incident once", len(data) == TOTAL and len({d["id"] for d in data}) == TOTAL, f"{len(data)} fetched")
check("export file is owner-only (600) in an owner-only folder (700)",
      path and stat.S_IMODE(os.stat(path).st_mode) == 0o600 and stat.S_IMODE(os.stat(os.path.dirname(path)).st_mode) == 0o700)
check("export keeps the fields the import needs and drops the score", data and set(data[0]) == {"id", "title", "serviceName", "environment", "severity", "description", "createdAt", "resolution"})
check("export output contains counts only (no incident text)", "zebra" not in ex.stdout.lower())
bad = subprocess.run([sys.executable, os.path.join(REPO, "tools", "export_hosted.py")], cwd=work, env={**env, "ONCALL_AUTH0_CLIENT_SECRET": "wrong"}, capture_output=True, text=True)
check("a wrong secret gives a clear message and no file", bad.returncode != 0 and "rejected" in (bad.stderr + bad.stdout))

# ---------- 2. dry run, import, import again ----------
dry = run_import(path, "--dry-run")
check("dry run reports what it would do and writes nothing", dry.returncode == 0 and f"would import {EXPECTED} of {TOTAL}" in dry.stdout and count() in (0, -1), dry.stdout.strip().splitlines()[-2][:90] if dry.stdout else dry.stderr[:90])
t0 = time.time(); imp = run_import(path)
last = imp.stdout.strip().splitlines()
check("import loads everything valid", imp.returncode == 0 and f"imported {EXPECTED} of {TOTAL}" in imp.stdout, f"{time.time() - t0:.0f}s")
check("import explains what it skipped", "1 without a description, 1 with an invalid id" in imp.stdout and "failed: 0" in imp.stdout and ": 1" in last[-1], last[-1][:120] if last else imp.stderr[:100])
check("documents are in the database", count() == EXPECTED, str(count()))
again = run_import(path)
check("importing again updates instead of duplicating", again.returncode == 0 and count() == EXPECTED, str(count()))

# ---------- 3. what was stored ----------
sample = incidents[5]
doc = json.loads(mongosh(f"print(JSON.stringify(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}},{{Embedding:0}})))") or "{}")
want = dt.datetime.strptime(sample["createdAt"][:23] + "Z", "%Y-%m-%dT%H:%M:%S.%fZ")
created_raw = doc.get("CreatedAt", "") if doc else ""
created_raw = created_raw.get("$date", "") if isinstance(created_raw, dict) else created_raw   # mongosh prints plain ISO text
got = dt.datetime.strptime(created_raw[:23] + "Z", "%Y-%m-%dT%H:%M:%S.%fZ") if created_raw else None
check("the original id and creation date are kept", doc and got == want, f"{got} vs {want}")
check("resolution details are kept", doc and doc["Resolution"]["RootCause"] == "cause 5" and doc["Resolution"]["StepsTaken"] == ["step a 5", "step b 5"])
dims = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}}).Embedding.length)")
check("each incident was embedded again with the local model (768 dimensions)", dims == "768", dims)
ver = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}}).EmbeddingVersion)")
check("imported incidents carry the current embedding version (2)", ver == "2", ver)
long_len = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{incidents[12]['id']}')}}).Metadata.Description.length)")
check("a very long description is stored in full", long_len == "7000", long_len)
nores = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{incidents[13]['id']}')}}).Resolution === null)")
check("an unresolved incident stays unresolved", nores == "true", nores)

# ---------- 4. ask questions through the real MCP server ----------
CMD = ["docker", "exec", "-i", "-e", f"MONGO_DATABASE={DB}", CONTAINER, "dotnet", "OnCallHelperLocal.dll"]
p = subprocess.Popen(CMD, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True); n = [0]
def rpc(method, params=None):
    n[0] += 1; p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": n[0], "method": method, "params": params or {}}) + "\n"); p.stdin.flush()
    return json.loads(p.stdout.readline())
def call(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args})["result"]; t = r["content"][0]["text"]
    return r.get("isError", False), (t if r.get("isError") else json.loads(t))
def callkw(name, args):
    """Like call(), but returns only the keyword list of a search_incidents result (the rest is checked separately)."""
    err, out = call(name, args)
    return err, (out["keywordMatches"] if not err and isinstance(out, dict) else out)

rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}})
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()

t0 = time.time(); found = None
while time.time() - t0 < 120:   # the search index catches up with a bulk load after a short delay
    err, out = call("find_similar_incidents", {"description": "the kiosk in the lobby shows a blank panel because the zebra widget licence ran out", "top": 3})
    if not err and out and out[0]["id"] == UNIQUE["id"]:
        found = out; break
    time.sleep(2)
check("a question in different words finds the imported incident", found is not None, f"after {time.time() - t0:.0f}s")
check("the match carries its resolution and a score", found and found[0]["resolution"]["rootCause"] == "cause 0" and 0.5 < found[0]["score"] <= 1.0, f"{found[0]['score']:.3f}" if found else "")
err, out = call("find_similar_incidents", {"description": "Customers say card payments were accepted but the balance never changed", "top": 5})
check("a question about payments returns payment incidents", not err and len(out) == 5 and all(o["serviceName"] == "payment-service" for o in out), ",".join(sorted({o["serviceName"] for o in out})) if not err else str(out)[:80])
err, out = callkw("search_incidents", {"serviceName": "kiosk-service"})
check("keyword search sees the imported data", not err and len(out) == 1 and out[0]["title"] == "Kiosk zebra widget outage")
err, out = callkw("search_incidents", {"serviceName": "billing-service", "limit": 100})
expected_billing = sum(1 for i in incidents if i["serviceName"] == "billing-service" and i["description"].strip() and i["id"] != "not-a-valid-id")
check("filters work on the imported data", not err and len(out) == expected_billing, f"{len(out) if not err else out} of {expected_billing}")

p.terminate()
mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
print(f"\n{sum(results)}/{len(results)} passed"); sys.exit(0 if all(results) else 1)
