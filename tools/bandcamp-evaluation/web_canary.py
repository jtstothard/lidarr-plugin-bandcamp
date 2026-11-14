#!/usr/bin/env python3
"""Bounded, opt-in read-only observation. Default invocation performs no network I/O."""
import argparse
from datetime import datetime, timezone
from html.parser import HTMLParser
import json
from pathlib import Path
import time
import urllib.error
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Homepage(HTMLParser):
    def __init__(self):
        super().__init__()
        self.identity = None

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if values.get("id") == "HomepageApp":
            try:
                self.identity = json.loads(values.get("data-blob", "")).get("pageContext", {}).get("identity")
            except (ValueError, AttributeError):
                pass


def classify(status, body):
    if status == 429:
        return "RateLimited"
    if status >= 500:
        return "Unavailable"
    parser = Homepage()
    try:
        parser.feed(body.decode("utf-8", errors="replace"))
        identity = parser.identity
        if isinstance(identity, dict):
            fan = identity.get("fanId")
            if status == 200 and type(fan) is int and fan > 0:
                return "Valid"
            if status in (200, 401, 403) and fan is None and identity.get("isLoggedIn") is False:
                return "SignedOut"
    except Exception:
        pass
    return "ChallengeOrUnrecognized"


def run(args, opener=None):
    if not args.live:
        return {"mode": "PLAN_ONLY", "requests": 0, "note": "No account accessed. See web-canary.md; separate authorization required."}
    if args.authorized_read_only != "I_HAVE_SEPARATE_AUTHORIZATION" or not 1 <= args.max_requests <= 3:
        raise ValueError("Live observation requires separate authorization and a bound of 1 to 3 requests")
    cookie = None
    if args.identity_file:
        value = Path(args.identity_file).read_text().strip()
        if value.startswith("identity="):
            value = value[9:]
        if not value or len(value) > 16384 or any(ord(c) <= 32 or ord(c) >= 127 or c in ";," for c in value):
            raise ValueError("A minimal identity is required")
        cookie = "identity=" + value
    opener = opener or urllib.request.build_opener(NoRedirect())
    started = time.monotonic()
    observations = []
    for _ in range(args.max_requests):
        if time.monotonic() - started >= 30:
            break
        headers = {"Accept": "text/html"}
        if cookie:
            headers["Cookie"] = cookie
        request = urllib.request.Request("https://bandcamp.com/", headers=headers, method="GET")
        try:
            try:
                response = opener.open(request, timeout=min(10, max(0.1, 30 - (time.monotonic() - started))))
            except urllib.error.HTTPError as response_error:
                response = response_error
            with response:
                body = response.read(1024 * 1024 + 1)
                # Never record headers, response bodies, URLs, fan IDs, prefixes or hashes.
                identities = [h.split(";", 1)[0] for h in response.headers.get_all("Set-Cookie", []) if h.startswith("identity=")]
                observations.append({"utc": datetime.now(timezone.utc).isoformat(), "http_status": response.code,
                    "outcome": classify(response.code, body) if len(body) <= 1024 * 1024 else "BodyLimit",
                    "identity_set_cookie_seen": bool(identities),
                    "different_identity_seen": bool(cookie and any(v != cookie for v in identities))})
                # Stop on failure/challenge/rate limiting. Never retry a refused observation.
                if observations[-1]["outcome"] != "Valid":
                    break
        except Exception:
            observations.append({"utc": datetime.now(timezone.utc).isoformat(), "outcome": "TransportFailure"})
            break
    return {"mode": "READ_ONLY_OBSERVATION", "requests": len(observations), "observations": observations,
        "limits": "No login, replacement, persistence, revocation or lifetime proof. Candidate scope/validity is unproven."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--authorized-read-only")
    parser.add_argument("--identity-file")
    parser.add_argument("--max-requests", type=int, default=1)
    args = parser.parse_args()
    try:
        print(json.dumps(run(args), indent=2))
    except Exception:
        print(json.dumps({"outcome": "RefusedOrFailed", "note": "No success confirmed. Check authorization, bounds and private input locally."}))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
