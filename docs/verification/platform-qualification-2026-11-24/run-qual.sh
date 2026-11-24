#!/bin/bash
# run-qual.sh <status|cancel|login|renewal-status> [operation-id]  (runs as t3dev)
# Loads secrets into this process only; output (no secrets) goes to receipts/<step>-<utc>.txt.
set -u
step=$1; op=${2:-}
root=/home/t3dev/bc-qual; repo=$root/repo
export DOTNET_ROOT=$root/dotnet PATH=$root/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
export LIDARR_URL=https://lidarr.trshpotato.win QUAL_OWNER=Bandcamp QUAL_FAN_ID=67857 QUAL_HUMAN_WAIT_SECONDS=${QUAL_HUMAN_WAIT_SECONDS:-60}
set -a; . $root/private/secrets.env; set +a
[ "$step" = login ] || unset LOGIN_USERNAME LOGIN_PASSWORD
mkdir -p $root/receipts; out=$root/receipts/$step-$(date -u +%Y%m%dT%H%M%SZ).txt
helper=$repo/src/Lidarr.Plugin.Bandcamp.CookiePush/bin/Release/net8.0/bandcamp-cookie-push.dll
qual=$repo/docs/verification/platform-qualification-2026-11-24/QualCheck/bin/Release/net8.0/QualCheck.dll
case $step in
  status) dotnet $helper --client-name Bandcamp --session-status </dev/null >$out 2>&1 ;;
  renewal-status) dotnet $helper --client-name Bandcamp --renewal-status "$op" </dev/null >$out 2>&1 ;;
  cancel|login) xvfb-run -a -s "-screen 0 1280x900x24" dotnet $qual $step </dev/null >$out 2>&1 ;;
esac
echo "EXIT=$?" >>$out
echo "XVFB_LEFT=$(pgrep -u t3dev -c Xvfb)" >>$out
echo "CHROME_LEFT=$(pgrep -u t3dev -c -f ms-playwright)" >>$out
echo "TMP_PROFILES=$(ls -d /tmp/bandcamp-cookie-push-* 2>/dev/null | wc -l)" >>$out
echo $out
