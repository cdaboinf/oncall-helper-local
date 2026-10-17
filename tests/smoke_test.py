#!/usr/bin/env python3
"""End-to-end test through the real MCP server, using ONLY made-up incidents and a separate test database
(oncall_local_test). Run it after `docker compose up -d`. Standard library only."""
import json, os, subprocess, sys, time

CONTAINER = os.environ.get("ONCALL_CONTAINER", "oncall-local-mcp")   # lets the test run against a separate container

CMD = ["docker", "exec", "-i", "-e", "MONGO_DATABASE=oncall_local_test", CONTAINER, "dotnet", "OnCallHelperLocal.dll"]
results = []
def check(name, ok, detail=""):
    results.append(bool(ok)); print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))

subprocess.run(["docker", "exec", "oncall-local-mongodb", "mongosh", "--quiet", "--eval",
                "db.getSiblingDB('oncall_local_test').incidents.deleteMany({})"], capture_output=True)
# The vector index belongs to one database, and `setup` only prepared the normal one: prepare the test database too.
setup = subprocess.run(CMD + ["setup"], capture_output=True, text=True)
check("setup creates the vector index in the test database", setup.returncode == 0, setup.stdout.strip().splitlines()[-1] if setup.stdout.strip() else setup.stderr[:120])

p = subprocess.Popen(CMD, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
n = [0]
def rpc(method, params=None):
    n[0] += 1
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": n[0], "method": method, "params": params or {}}) + "\n"); p.stdin.flush()
    return json.loads(p.stdout.readline())
def call(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args})["result"]
    text = r["content"][0]["text"]
    return r.get("isError", False), (text if r.get("isError") else json.loads(text))

def callkw(name, args):
    """Like call(), but returns only the keyword list of a search_incidents result (the rest is checked separately)."""
    err, out = call(name, args)
    return err, (out["keywordMatches"] if not err and isinstance(out, dict) else out)

init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}})
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()
check("initialize", init["result"]["serverInfo"]["name"] == "oncall-helper-local" and "Searching well" in init["result"]["instructions"] and "at least once" in init["result"]["instructions"])
tool_list = rpc("tools/list")["result"]["tools"]
tools = sorted(t["name"] for t in tool_list)
check("exactly the four tools", tools == ["create_incident", "find_similar_incidents", "search_incidents", "update_incident"], ",".join(tools))
# The tool contract Claude reads and validates against must stay precise.
T = {t["name"]: t for t in tool_list}
untyped = [(n, k) for n, t in T.items() for k, v in t["inputSchema"]["properties"].items() if "type" not in v]
check("every tool parameter states its type (text parameters are not left untyped)", not untyped, str(untyped)[:100])
check("find_similar_incidents requires `description`, as text", T["find_similar_incidents"]["inputSchema"].get("required") == ["description"]
      and "string" in T["find_similar_incidents"]["inputSchema"]["properties"]["description"]["type"])
check("the required parameters of create_incident are still required", T["create_incident"]["inputSchema"].get("required") == ["title", "description", "serviceName", "environment", "severity"])
check("array items are typed as text", T["create_incident"]["inputSchema"]["properties"]["stepsTaken"]["items"].get("type") == "string")
check("tool safety hints survive (update is destructive, create is not read-only)", T["update_incident"]["annotations"]["destructiveHint"] is True and T["create_incident"]["annotations"]["readOnlyHint"] is False and T["search_incidents"]["annotations"]["readOnlyHint"] is True)
check("a missing required argument gets a clear server-side message, not a vague schema error",
      (lambda r: r[0] and "description is required" in r[1])(call("find_similar_incidents", {"description": None, "top": 2})))

seed = [  # made-up incidents, written in the style Claude would produce
 ("Card payments accepted but not posted to accounts", "payments-api", "production", "sev2",
  "Customers reported card payments were accepted but never appeared in their account balance. Our logs showed success with no errors. The downstream provider was rejecting the account sync with error codes."),
 ("Pods crash looping after release", "payments-api", "production", "sev2",
  "After a release the service pods restarted repeatedly and never became ready. Startup logs showed a database connection failure caused by a wrong database host setting in the deployment configuration."),
 ("Business file download fails with a database timeout", "business-files", "production", "sev2",
  "Staff could create a very large batch file and it showed complete, but downloading it failed with an error banner. The download query that loads the batch items timed out on the largest file."),
 ("Certificate expired on the internal gateway", "api-gateway", "production", "sev3",
  "Clients failed the TLS handshake against the internal gateway because its certificate had expired after the renewal job was disabled."),
 ("Push notifications not delivered", "notifications", "production", "sev3",
  "Mobile push notifications stopped arriving because the push provider rejected requests; the provider credentials had expired."),
]
ids = {}
for title, svc, env, sev, desc in seed:
    err, out = call("create_incident", {"title": title, "description": desc, "serviceName": svc, "environment": env, "severity": sev})
    ids[title] = out["id"] if not err else None
    check(f"create (no resolution yet): {title[:38]}", not err and out["resolution"]["stepsTaken"] == [] and "embedding" not in out)

# Atlas Search indexes a new document a moment after it is written, so wait until the last one is searchable.
t0 = time.time(); searchable = False
while time.time() - t0 < 90:
    err, out = call("find_similar_incidents", {"description": seed[-1][4], "top": 1})
    if not err and out and out[0]["title"] == seed[-1][0]:
        searchable = True; break
    time.sleep(1)
check("a new incident becomes searchable by similarity", searchable, f"after {time.time() - t0:.1f}s")

queries = [  # different wording on purpose
 ("people paid by card yesterday but the balance in the app never changed, nothing in our logs", "Card payments accepted but not posted to accounts"),
 ("containers keep restarting after the deploy, cannot reach the database host", "Pods crash looping after release"),
 ("sql timeout when downloading a large file even though the batch is complete", "Business file download fails with a database timeout"),
 ("'certificate has expired' when calling the internal gateway", "Certificate expired on the internal gateway"),
 ("iOS and Android pushes are not arriving, provider returns an auth error", "Push notifications not delivered"),
]
top1 = 0
for q, expected in queries:
    err, out = call("find_similar_incidents", {"description": q, "top": 3})
    ok = not err and len(out) > 0 and out[0]["title"] == expected
    top1 += ok
    check(f"similar -> {expected[:36]}", ok, ("score %.3f" % out[0]["score"]) if not err and out else str(out)[:80])
check("vector search ranks the right incident first (5 of 5)", top1 == 5, f"{top1}/5")
err, out = call("find_similar_incidents", {"description": "the office coffee machine is out of beans again", "top": 1})
err2, best = call("find_similar_incidents", {"description": queries[0][0], "top": 1})
ok = not err and not err2 and out[0]["score"] < best[0]["score"]
check("unrelated text scores below a real match", ok, f"{out[0]['score']:.3f} < {best[0]['score']:.3f}" if not err and not err2 else str(out)[:100])

target = ids["Pods crash looping after release"]
err, out = call("update_incident", {"id": target, "rootCause": "wrong database host in the deployment config", "resolutionSummary": "corrected the setting and redeployed",
                                    "stepsTaken": ["found the connection error in startup logs", "fixed the host value", "redeployed"], "resolvedBy": "on-call engineer"})
check("update adds the resolution and re-embeds (root cause and summary are searchable)", not err and out["embeddingUpdated"] is True and out["incident"]["resolution"]["rootCause"].startswith("wrong database host"))
err, out2 = call("update_incident", {"id": target, "severity": "sev1"})
check("a change that is not searchable text (severity) does not re-embed", not err and out2["embeddingUpdated"] is False and out2["incident"]["severity"] == "sev1")
err, _ = call("update_incident", {"id": target, "severity": "sev2"})   # put it back for the later filter checks
check("update keeps fields it was not given", not err and out["incident"]["serviceName"] == "payments-api" and out["incident"]["severity"] == "sev2")
err, out = call("update_incident", {"id": target, "description": "After a release the checkout service pods kept restarting. The cause was a mistyped queue address in the deployment settings, not the database."})
check("changing the description re-embeds", not err and out["embeddingUpdated"] is True)
err, out = call("find_similar_incidents", {"description": "pods restarting because of a wrong queue address in the settings", "top": 1})
check("search follows the new description", not err and out[0]["id"] == target and out[0]["resolution"]["rootCause"], out[0]["title"] if not err else str(out))

# A question about HOW something was fixed must find the incident even though the description never mentions the fix.
err, made = call("create_incident", {"title": "Report page blank for some users", "description": "Some users saw a blank report page after a release while others were fine.",
    "serviceName": "reports-ui", "environment": "production", "severity": "sev4", "rootCause": "the CDN kept serving a stale script bundle",
    "resolutionSummary": "purged the CDN cache and enabled hashed file names", "stepsTaken": ["checked bundle hash", "purged cache"], "resolvedBy": "on-call"})
t0 = time.time(); hit = None
while time.time() - t0 < 60:
    err, out = call("find_similar_incidents", {"description": "how did we fix a stale cdn cache problem", "top": 3})
    if not err and out and out[0]["title"] == "Report page blank for some users": hit = out[0]; break
    time.sleep(1)
check("a question about how it was fixed finds the incident (resolution text is searchable)", hit is not None, f"score {hit['score']:.3f}" if hit else "not found")

# search_incidents carries the semantic matches with the keyword ones, so a keyword-only "no" cannot happen.
err, full = call("search_incidents", {"query": "504"})
check("a keyword search with no hits still returns semantic matches", not err and full["keywordMatches"] == [] and len(full["semanticMatches"]) > 0 and "NOT mean" in full["note"], f"{len(full['semanticMatches']) if not err else full} semantic")
err, full = call("search_incidents", {"query": "stale cdn bundle purged"})
check("semantic matches find a fix by meaning when no exact words match", not err and full["keywordMatches"] == [] and full["semanticMatches"] and full["semanticMatches"][0]["title"] == "Report page blank for some users", full["semanticMatches"][0]["title"] if not err and full["semanticMatches"] else str(full)[:80])
err, full = call("search_incidents", {"severity": "sev3"})
check("a filter-only search computes no semantic matches and says so", not err and full["semanticMatches"] == [] and "Filter-only" in full["note"] and len(full["keywordMatches"]) == 2)
err, full = call("search_incidents", {"query": "database host", "serviceName": "payments-api"})
check("semantic matches respect the filters", not err and len(full["semanticMatches"]) > 0 and all(m["serviceName"] == "payments-api" for m in full["semanticMatches"]), ",".join(sorted({m["serviceName"] for m in full["semanticMatches"]})) if not err else str(full)[:80])
err, full = call("search_incidents", {"query": "x" * 7000})   # over the 6,000-character limit
check("an over-long query still returns keyword results and explains the skipped semantic part", not err and full["semanticMatches"] == [] and "longer than" in full["note"])

err, out = callkw("search_incidents", {"serviceName": "payments-api"})
check("filter by service", not err and len(out) == 2, f"{len(out) if not err else out}")
err, out = callkw("search_incidents", {"query": "certificate", "limit": 5})
check("text search", not err and len(out) == 1 and out[0]["title"].startswith("Certificate"))
err, out = callkw("search_incidents", {"severity": "sev3", "from": "2000-01-01", "to": "2999-01-01"})
check("severity and date filters", not err and len(out) == 2)

# Claude sometimes sends a number or boolean where text is expected (an error code such as 504). That must work.
err, out = callkw("search_incidents", {"query": 504})
check("a numeric search term (query: 504 as a JSON number) is accepted", not err and out == [], str(out)[:80])
err, out = callkw("search_incidents", {"serviceName": 12345})
check("a numeric service name is accepted", not err and out == [], str(out)[:80])
err, out = callkw("search_incidents", {"query": True})
check("a boolean where text is expected is accepted", not err, str(out)[:80])
err, out = call("find_similar_incidents", {"description": 504, "top": 2})
check("a numeric description for similarity search is accepted", not err and isinstance(out, list), str(out)[:80])
err, out = callkw("search_incidents", {"query": "certificate", "limit": "5"})
check("numbers sent as text for limit still work", not err and len(out) == 1)

err, msg = call("create_incident", {"title": "x", "description": "", "serviceName": "s", "environment": "production", "severity": "sev3"})
check("empty description is rejected", err and "description is required" in msg)
err, msg = call("create_incident", {"title": "x", "description": "a" * 7000, "serviceName": "s", "environment": "production", "severity": "sev3"})
check("an over-long description is rejected with advice to summarize", err and "summarize" in msg)
err, msg = call("find_similar_incidents", {"description": "   "})
check("blank search text is rejected", err)
err, msg = call("update_incident", {"id": "64b7f0c2a1b2c3d4e5f60718", "title": "x"})
check("updating an unknown id is a clear error", err and "not found" in msg)
err, msg = call("update_incident", {"id": "not-an-id", "title": "x"})
check("a malformed id is a clear error", err and "not found" in msg)
err, msg = callkw("search_incidents", {"from": "yesterday-ish"})
check("a bad date is a clear error", err and "ISO-8601" in msg)

# `reembed` upgrades incidents saved with the old (description-only) recipe, from the stored text alone.
mongosh = lambda js: subprocess.run(["docker", "exec", "oncall-local-mongodb", "mongosh", "--quiet", "--eval", js], capture_output=True, text=True).stdout.strip()
total = int(mongosh("print(db.getSiblingDB('oncall_local_test').incidents.countDocuments())"))
mongosh("db.getSiblingDB('oncall_local_test').incidents.updateMany({}, {$set: {EmbeddingVersion: 1}})")
dry = subprocess.run(CMD + ["reembed", "--dry-run"], capture_output=True, text=True)
check("reembed --dry-run counts what is out of date and changes nothing", f"{total} need the version 2" in dry.stdout and mongosh("print(db.getSiblingDB('oncall_local_test').incidents.countDocuments({EmbeddingVersion: 1}))") == str(total), dry.stdout.strip().splitlines()[0][:90])
run = subprocess.run(CMD + ["reembed"], capture_output=True, text=True)
check("reembed rebuilds every old vector", run.returncode == 0 and f"re-embedded {total}" in run.stdout and mongosh("print(db.getSiblingDB('oncall_local_test').incidents.countDocuments({EmbeddingVersion: 2}))") == str(total), run.stdout.strip().splitlines()[-1][:80])
time.sleep(3)
err, out = call("find_similar_incidents", {"description": "how did we fix a stale cdn cache problem", "top": 1})
check("search still works after reembed", not err and out and out[0]["title"] == "Report page blank for some users")

p.terminate()
subprocess.run(["docker", "exec", "oncall-local-mongodb", "mongosh", "--quiet", "--eval", "db.getSiblingDB('oncall_local_test').dropDatabase()"], capture_output=True)
print(f"\n{sum(results)}/{len(results)} passed"); sys.exit(0 if all(results) else 1)
