#!/bin/bash
# Bounded #53 web canary (authorized by Jay 2026-11-24 for his own Bandcamp account; read-only).
# Runs as root from bandcamp-web-canary.service; the observation itself runs as t3dev.
# Window: 2026-11-24T17:30Z .. 2026-12-01T18:00Z. Hourly until 2026-11-25T06:00Z, then every
# 6 hours (00/06/12/18 UTC). One GET per run (--max-requests 1), at most 40 runs in total.
# The series stops (timer disabled, identity file deleted) at the end date, at the cap, or after
# the first non-Valid outcome.
set -u
D=/home/t3dev/bc-qual/canary
R=$D/results.jsonl
END=$(date -ud 2026-12-01T18:00:00Z +%s); HOURLY_UNTIL=$(date -ud 2026-11-25T06:00:00Z +%s); NOW=$(date -u +%s)
stop_series() { rm -f $D/identity; echo "{\"utc\":\"$(date -u +%FT%TZ)\",\"series\":\"STOPPED\",\"reason\":\"$1\"}" >>$R; chown t3dev:t3dev $R; systemctl disable --now bandcamp-web-canary.timer; exit 0; }
[ "$NOW" -ge "$END" ] && stop_series end-of-window
RUNS=$(cat $R 2>/dev/null | grep -c '"mode"')
[ "$RUNS" -ge 40 ] && stop_series request-cap
if [ "$NOW" -ge "$HOURLY_UNTIL" ] && [ $(( 10#$(date -u +%H) % 6 )) -ne 0 ] && [ "${FORCE:-0}" != 1 ]; then exit 0; fi
OUT=$(runuser -u t3dev -- timeout 40 python3 -I /home/t3dev/bc-qual/repo/tools/bandcamp-evaluation/web_canary.py --live --authorized-read-only I_HAVE_SEPARATE_AUTHORIZATION --max-requests 1 --identity-file $D/identity)
echo "$OUT" | python3 -I -c 'import json,sys;r=json.load(sys.stdin);print(json.dumps(r,separators=(",",":")))' >>$R || echo "{\"utc\":\"$(date -u +%FT%TZ)\",\"outcome\":\"RunnerFailed\"}" >>$R
chown t3dev:t3dev $R
echo "$OUT" | grep -q '"outcome": "Valid"' || stop_series first-non-valid-outcome
