<#
.SYNOPSIS
    Checks a container on a Dockhand server for a new image and updates it.

.DESCRIPTION
    Copy this script into your own solution. It does what you would otherwise do in the Dockhand web UI
    after pushing an image: open the environment, check for updates, and update the container.

    Find the values for -Server, -Environment and -Container with:

        dockhand containers search <part of the container or image name>

    which prints the matching 'dockhand ... update container ...' line for every match.

    The update keeps the container's image tag: push the new image under the tag the container already
    uses (for example :latest), then run this script. Dockhand pulls that tag again and recreates the
    container with the same settings.

.PARAMETER Server
    Endpoint alias registered with 'dockhand endpoint add'.

.PARAMETER Environment
    Docker environment name or id (see 'dockhand -<alias> env list').

.PARAMETER Container
    Container name or id.

.PARAMETER Image
    Optional. An image to 'docker push' before the check, e.g. registry.example.com/myapp:latest.

.PARAMETER CheckOnly
    Only report whether a new image exists; do not update.

.PARAMETER Force
    Recreate the container even when Dockhand's check finds no new image.

.PARAMETER Dockhand
    Path to the dockhand executable, if it is not on PATH.

.EXAMPLE
    ./Update-DockhandContainer.ps1 -Server srv01 -Environment hhdocker01 -Container myapp

.EXAMPLE
    ./Update-DockhandContainer.ps1 -Server srv01 -Environment hhdocker01 -Container myapp -Image registry.example.com/myapp:latest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $Container,
    [string] $Image,
    [switch] $CheckOnly,
    [switch] $Force,
    [string] $Dockhand = 'dockhand'
)

$ErrorActionPreference = 'Stop'

if ($Image) {
    Write-Host "Pushing $Image ..."
    docker push $Image
    if ($LASTEXITCODE -ne 0) { throw "docker push failed with exit code $LASTEXITCODE." }
}

$arguments = @("-$Server", '--env', $Environment, 'update', 'container', $Container, '--yes', '--output', 'json')
if ($CheckOnly) { $arguments += '--check-only' }
if ($Force) { $arguments += '--force' }

Write-Host "Checking $Container on $Server / $Environment for a new image ..."

# stdout is one JSON document; messages and errors go to stderr and show up in the console.
$json = & $Dockhand @arguments
$exitCode = $LASTEXITCODE
$result = if ($json) { ($json | Out-String) | ConvertFrom-Json } else { $null }

if ($exitCode -ne 0) {
    # Exit codes: 2 usage, 3 not found, 4 authentication, 5 API error, 7 network, see the dockhand README.
    throw "dockhand failed with exit code $exitCode."
}

if ($result.updated) {
    Write-Host "Updated $($result.container.name): recreated with its latest image." -ForegroundColor Green
    foreach ($warning in @($result.warnings)) {
        if ($warning) { Write-Warning "$($warning.status): $($warning.message)" }
    }
}
elseif ($result.updateAvailable) {
    Write-Host "A new image is available for $($result.container.name) (not updated: -CheckOnly)." -ForegroundColor Yellow
}
else {
    Write-Host "$($result.container.name) is already up to date ($($result.check.status))." -ForegroundColor Green
}
