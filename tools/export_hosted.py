#!/usr/bin/env python3
"""Export every incident from the hosted app (the AWS API) to a JSON file, ready for the local `import` command.

Run it on the machine that is allowed to reach the hosted API. Standard library only.

Settings come from environment variables, so no secret is ever typed into a command or stored in a file:
  ONCALL_API_BASE_URL      default https://api.oncallhelper.com
  ONCALL_API_TOKEN         a ready bearer token, OR
  ONCALL_AUTH0_DOMAIN, ONCALL_AUTH0_CLIENT_ID, ONCALL_AUTH0_CLIENT_SECRET   (machine-to-machine login)
  ONCALL_AUTH0_AUDIENCE    optional, default http://localhost:5172 (the API identifier)

The output file contains your real incident data. It is written with owner-only permissions into ./exports/, which is
ignored by Git. This script prints counts only, never incident content.
"""
import datetime as dt, json, os, stat, sys, urllib.error, urllib.parse, urllib.request

PAGE = 500  # the API caps a search at 500 results, newest first


def env(name, default=None):
    value = os.environ.get(name, "").strip()
    return value or default


def get_token():
    token = env("ONCALL_API_TOKEN")
    if token:
        return token
    domain, cid, secret = env("ONCALL_AUTH0_DOMAIN"), env("ONCALL_AUTH0_CLIENT_ID"), env("ONCALL_AUTH0_CLIENT_SECRET")
    if not (domain and cid and secret):
        sys.exit("Set ONCALL_API_TOKEN, or ONCALL_AUTH0_DOMAIN + ONCALL_AUTH0_CLIENT_ID + ONCALL_AUTH0_CLIENT_SECRET.")
    base = domain.rstrip("/") if "://" in domain else "https://" + domain.rstrip("/")
    body = json.dumps({"grant_type": "client_credentials", "client_id": cid, "client_secret": secret,
                       "audience": env("ONCALL_AUTH0_AUDIENCE", "http://localhost:5172")}).encode()
    req = urllib.request.Request(base + "/oauth/token", data=body, method="POST", headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read())["access_token"]
    except urllib.error.HTTPError as e:
        sys.exit(f"Auth0 rejected the token request (HTTP {e.code}). Check the client id and secret.")


def fetch(base, token, to=None):
    qs = {"limit": PAGE}
    if to is not None:
        qs["to"] = to.strftime("%Y-%m-%dT%H:%M:%S.") + f"{to.microsecond // 1000:03d}Z"
    req = urllib.request.Request(f"{base}/api/incidents?{urllib.parse.urlencode(qs)}",
                                 headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        sys.exit(f"The API answered HTTP {e.code}." + (" The token was not accepted." if e.code in (401, 403) else ""))
    except urllib.error.URLError as e:
        sys.exit(f"Could not reach the API at {base}: {e.reason}")


def parse_time(text):
    text = text.rstrip("Z")
    if "." in text:  # .NET writes 7 fractional digits; Python wants at most 6
        head, frac = text.split(".")
        text = f"{head}.{frac[:6]}"
    return dt.datetime.fromisoformat(text)


def main():
    base = env("ONCALL_API_BASE_URL", "https://api.oncallhelper.com").rstrip("/")
    token = get_token()

    incidents, seen, to = [], set(), None
    while True:
        page = fetch(base, token, to)
        fresh = [i for i in page if i["id"] not in seen]
        for i in fresh:
            seen.add(i["id"])
            incidents.append({k: i.get(k) for k in
                              ("id", "title", "serviceName", "environment", "severity", "description", "createdAt", "resolution")})
        print(f"fetched {len(page)} ({len(fresh)} new), total {len(incidents)}")
        if len(page) < PAGE or not fresh:
            break
        # The API has no offset: walk back in time from the oldest incident seen so far (1 ms earlier).
        to = parse_time(min(i["createdAt"] for i in fresh)) - dt.timedelta(milliseconds=1)

    out_dir = os.path.join(os.getcwd(), "exports")
    os.makedirs(out_dir, mode=0o700, exist_ok=True)
    path = os.path.join(out_dir, f"hosted-incidents-{dt.datetime.now().strftime('%Y%m%d-%H%M%S')}.export.json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, stat.S_IRUSR | stat.S_IWUSR)
    with os.fdopen(fd, "w") as f:
        json.dump(incidents, f, indent=1)
    print(f"wrote {len(incidents)} incidents to {path}")
    print("This file holds real incident data: keep it out of Git and delete it after the import.")


if __name__ == "__main__":
    main()
