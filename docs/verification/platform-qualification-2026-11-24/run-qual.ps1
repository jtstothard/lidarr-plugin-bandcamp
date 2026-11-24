# run-qual.ps1 <status|cancel|login|renewal-status> [operation-id]
# Reads secrets into this process's environment only, runs one step, and writes
# the step's console output (which contains no secrets) to receipts\<step>-<utc>.txt.
param([Parameter(Mandatory)][ValidateSet('status','cancel','login','renewal-status')][string]$Step, [string]$OperationId)
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Jay\bc-qual'
$repo = "$root\repo"
# Secrets come from Jay's own 1Password (desktop app approval prompt), read once per run.
function OpRead([string]$ref) { $v = & op read $ref; if ($LASTEXITCODE -ne 0 -or -not $v) { throw "1Password read failed" }; $v }
$secrets = [pscustomobject]@{
    LIDARR_API_KEY = OpRead 'op://yswnxpenahjrkngs3doh2wke4m/3htfdo2kyfikb74poz4tk6yj7q/credential'
    LOGIN_USERNAME = if ($Step -eq 'login') { OpRead 'op://yswnxpenahjrkngs3doh2wke4m/rket5rwl3dw7dwu7cv4u6fi364/username' }
    LOGIN_PASSWORD = if ($Step -eq 'login') { OpRead 'op://yswnxpenahjrkngs3doh2wke4m/rket5rwl3dw7dwu7cv4u6fi364/password' }
}
$plain = $null
$env:LIDARR_URL = 'https://lidarr.trshpotato.win'
$env:LIDARR_API_KEY = $secrets.LIDARR_API_KEY
$env:QUAL_OWNER = 'Bandcamp'
$env:QUAL_FAN_ID = '67857'
$env:QUAL_HUMAN_WAIT_SECONDS = '540'
$env:PLAYWRIGHT_BROWSERS_PATH = "$root\chromium"
if ($Step -eq 'login') { $env:LOGIN_USERNAME = $secrets.LOGIN_USERNAME; $env:LOGIN_PASSWORD = $secrets.LOGIN_PASSWORD }
$secrets = $null; $plain = $null
New-Item -ItemType Directory -Force "$root\receipts" | Out-Null
$out = "$root\receipts\$Step-$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')).txt"
$helper = "$repo\src\Lidarr.Plugin.Bandcamp.CookiePush\bin\Release\net8.0\bandcamp-cookie-push.dll"
$qual = "$repo\docs\verification\platform-qualification-2026-11-24\QualCheck\bin\Release\net8.0\QualCheck.dll"
try {
    switch ($Step) {
        'status' { & dotnet $helper --client-name Bandcamp --session-status *>&1 | Tee-Object -FilePath $out }
        'renewal-status' { & dotnet $helper --client-name Bandcamp --renewal-status $OperationId *>&1 | Tee-Object -FilePath $out }
        default { & dotnet $qual $Step *>&1 | Tee-Object -FilePath $out }
    }
    "EXIT=$LASTEXITCODE" | Tee-Object -FilePath $out -Append
} finally {
    Remove-Item Env:LIDARR_API_KEY, Env:LOGIN_USERNAME, Env:LOGIN_PASSWORD -ErrorAction SilentlyContinue
}
