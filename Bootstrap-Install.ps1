# =====================================================================
# C3D Print Bridge — ONE-PASTE bootstrap.
# Paste this ENTIRE block into PowerShell (right-click to paste).
#
# WHY IT ASKS FOR A LOGIN OR A TOKEN:
# The bridge code lives in a PRIVATE GitHub repo, so GitHub will not
# hand it out to an anonymous download link — that is the point of
# keeping it private. A truly token-free one-liner is impossible
# while the repo stays private. This bootstrap is the closest honest
# thing: ONE paste, plus at most ONE login/token step.
#
# It tries, in order:
#   1. GitHub CLI (gh) already signed in  -> clone, no token needed.
#   2. gh installed but not signed in     -> gh browser-code login,
#      then clone. (This is the cleanest path: no token to create.)
#   3. No gh, but winget available        -> install gh, then as (2).
#   4. Otherwise                          -> ask ONCE for a read-only
#      fine-grained token (Contents: read-only, scoped to this repo
#      only), use it for the download, then forget it.
#
# The token is held in memory ONLY for the download call. It is
# NEVER written to disk, never added to the installer or the repo,
# and never echoed. If you close this window, the token is gone.
# =====================================================================

$ErrorActionPreference = 'Stop'
$repo = 'creat3dworld/c3d-print-bridge'
$installRoot = "$env:USERPROFILE\C3DBridge"
$sourceDir = "$installRoot\source"
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

function Test-GhAuth {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $false }
    gh auth status 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

$haveSource = $false

# --- Paths 1-3: GitHub CLI --------------------------------------------
if (Get-Command gh -ErrorAction SilentlyContinue) {
    if (-not (Test-GhAuth)) {
        Write-Host 'Signing into GitHub (a browser code login — follow the prompts)...' -ForegroundColor Cyan
        gh auth login
    }
} elseif (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host 'Installing GitHub CLI (needed to fetch the private repo)...' -ForegroundColor Cyan
    try {
        winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements | Out-Null
        $env:PATH = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe\GitHub CLI*\;$env:PATH"
        if (-not (Test-GhAuth)) {
            Write-Host 'Signing into GitHub (a browser code login — follow the prompts)...' -ForegroundColor Cyan
            gh auth login
        }
    } catch {
        Write-Host "GitHub CLI install did not work ($($_.Exception.Message)) — falling back to a token download." -ForegroundColor Yellow
    }
}

if ((Get-Command gh -ErrorAction SilentlyContinue) -and (Test-GhAuth)) {
    try {
        if (Test-Path (Join-Path $sourceDir '.git')) {
            Write-Host 'Updating the existing bridge source...'
            git -C $sourceDir pull | Out-Null
        } else {
            Write-Host 'Downloading the bridge source...'
            if (Test-Path $sourceDir) { Remove-Item $sourceDir -Recurse -Force }
            gh repo clone $repo $sourceDir | Out-Null
        }
        $haveSource = $true
    } catch {
        Write-Host "GitHub CLI download did not work ($($_.Exception.Message)) — falling back to a token download." -ForegroundColor Yellow
    }
}

# --- Path 4: one-time read-only token (memory only, never stored) -----
if (-not $haveSource) {
    Write-Host ''
    Write-Host 'A read-only GitHub token is needed for this one download.' -ForegroundColor Cyan
    Write-Host 'Create it at: https://github.com/settings/tokens?type=beta'
    Write-Host '  - Fine-grained token, repository access: ONLY creat3dworld/c3d-print-bridge'
    Write-Host '  - Permissions: Contents = Read-only (nothing else).'
    Write-Host 'Paste it below (input is hidden). It is used once and then forgotten.'
    $secure = Read-Host 'Token' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        $zip = Join-Path $env:TEMP 'c3d-bridge.zip'
        Invoke-RestMethod -Headers @{ Authorization = "Bearer $plain" } `
            -Uri "https://api.github.com/repos/$repo/zipball" -OutFile $zip
    } finally {
        $plain = $null
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    $tmp = Join-Path $env:TEMP 'c3d-bridge-src'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $sourceDir) { Remove-Item $sourceDir -Recurse -Force }
    Move-Item $inner.FullName $sourceDir
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Download complete. The token was used once and is now gone from memory.' -ForegroundColor Green
    $haveSource = $true
}

# --- Run the installer --------------------------------------------------
if (-not (Test-Path (Join-Path $sourceDir 'Install-C3DBridge.ps1'))) {
    throw 'Bridge source was not downloaded correctly — Install-C3DBridge.ps1 is missing.'
}
& (Join-Path $sourceDir 'Install-C3DBridge.ps1') -SourceDir $sourceDir
