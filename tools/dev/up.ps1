# Bring the backend up on :4000, reading secrets from .env.
# PowerShell equivalent of up.sh for Windows host execution.

$PSScriptRoot = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition
$Root = Resolve-Path "$PSScriptRoot/../.."
Set-Location $Root

if (-not (Test-Path .env)) {
    Write-Error "No .env found. Rename .env.example to .env first."
    exit 1
}

# Load .env variables
Get-Content .env | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith("#")) {
        if ($line -match "^([^=]+)=(.*)$") {
            $key = $Matches[1].Trim()
            $val = $Matches[2].Trim()
            # Strip quotes if present
            if ($val -match '^"(.*)"$') { $val = $Matches[1] }
            elseif ($val -match "^'(.*)'$") { $val = $Matches[1] }
            [System.Environment]::SetEnvironmentVariable($key, $val, 'Process')
        }
    }
}

# ConnectionStrings DefaultConnection mapping
$dbConn = [System.Environment]::GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
if ($dbConn -and $dbConn.Contains("./")) {
    $dbConnPath = "Data Source=$Root/kitto-dev.db"
    [System.Environment]::SetEnvironmentVariable("ConnectionStrings__DefaultConnection", $dbConnPath, 'Process')
}

# Set URLs and Environment
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://[::]:4000", 'Process')
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development", 'Process')
[System.Environment]::SetEnvironmentVariable("Ai__Langflow__Tweaks__PlanningInput-v4__mode", "chat", 'Process')

Write-Host "Backend on http://[::]:4000  (Ctrl+C to stop)"
dotnet run --project Life-Admin-Autopilot-Backend --no-launch-profile
