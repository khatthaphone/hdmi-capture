# Hands a capture card between Windows and WSL with usbipd-win (task 109).
#   wsl-card.ps1 status            where each known card is (Windows / shared / attached to WSL)
#   wsl-card.ps1 attach [-Auto]    attach to WSL (-Auto keeps re-attaching after replug; runs in the foreground)
#   wsl-card.ps1 detach            give the card back to Windows
# -HardwareId picks the card (default UGREEN-25854, 1de1:f105). Attaching takes the card away from Windows:
# the Windows app shows it as disconnected until detach. One-time, elevated: install usbipd-win and
# `usbipd bind --hardware-id <VID:PID>` (bind survives reboots and doesn't take the card from Windows).
param(
    [Parameter(Position = 0)][ValidateSet("status", "attach", "detach")][string]$Action = "status",
    [string]$HardwareId = "1de1:f105",
    [string]$Distro = "Ubuntu-24.04",
    [switch]$Auto
)
$ErrorActionPreference = "Stop"

if (-not (Get-Command usbipd -ErrorAction SilentlyContinue)) {
    Write-Error "usbipd-win is not installed. Elevated: winget install --interactive --exact dorssel.usbipd-win"
}

function Get-Card {
    $state = usbipd state | ConvertFrom-Json
    $state.Devices | Where-Object { $_.InstanceId -match "VID_$($HardwareId.Split(':')[0])&PID_$($HardwareId.Split(':')[1])" } | Select-Object -First 1
}

function Show-Card($card) {
    if (-not $card) { "$HardwareId`: not plugged in"; return }
    $where = if ($card.ClientIPAddress) { "attached to WSL ($($card.ClientIPAddress))" }
             elseif ($card.PersistedGuid) { "shared (bound), in use by Windows" }
             else { "not shared: run elevated 'usbipd bind --hardware-id $HardwareId' once" }
    "$($card.BusId)  $HardwareId  $($card.Description)  →  $where"
}

$card = Get-Card
switch ($Action) {
    "status" { Show-Card $card }
    "attach" {
        if (-not $card) { Write-Error "$HardwareId is not plugged in" }
        if (-not $card.PersistedGuid) { Write-Error "$HardwareId is not shared yet: run elevated 'usbipd bind --hardware-id $HardwareId' once" }
        # A WSL session must be running for --wsl.
        wsl -d $Distro -- true | Out-Null
        if ($Auto) { usbipd attach --wsl $Distro --hardware-id $HardwareId --auto-attach }
        else {
            usbipd attach --wsl $Distro --hardware-id $HardwareId
            Start-Sleep -Seconds 2
            wsl -d $Distro -- bash -lc "ls /dev/video* 2>/dev/null || (sudo -n modprobe uvcvideo 2>/dev/null; ls /dev/video* 2>/dev/null) || echo 'no /dev/video* yet'"
            Show-Card (Get-Card)
        }
    }
    "detach" {
        if ($card -and $card.ClientIPAddress) { usbipd detach --hardware-id $HardwareId }
        Show-Card (Get-Card)
    }
}
