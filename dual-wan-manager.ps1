<#
.SYNOPSIS
    Inspects, configures, and load-balances dual Ethernet + Wi-Fi connections on Windows.

.NOTES
    Author: Omkar Waghmare
    License: MIT

.DESCRIPTION
    Supports four modes:
      -Status       (Default, no admin) Inspects physical adapters, link speeds, subnets,
                    default gateways, interface metrics, and routing warnings.
      -EqualMetrics (Requires Admin) Sets equal InterfaceMetric on Ethernet and Wi-Fi so
                    Windows keeps both default routes active for per-connection routing.
      -Proxy        (No admin needed) Starts the local Node.js Dual-WAN HTTP/HTTPS + SOCKS5
                    load-balancing proxy (dual-wan-proxy.js). Optionally sets Windows
                    User Proxy (-SetSystemProxy) and cleanly restores it on exit.
      -Restore      Restores original interface metrics (if elevated) and disables the local
                    Windows User Proxy.

.EXAMPLE
    .\dual-wan-manager.ps1 -Mode Status
    .\dual-wan-manager.ps1 -Mode Proxy -SetSystemProxy -EthernetWeight 1 -WifiWeight 2
    .\dual-wan-manager.ps1 -Mode EqualMetrics
    .\dual-wan-manager.ps1 -Mode Restore
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Proxy', 'EqualMetrics', 'Restore')]
    [string]$Mode = 'Status',

    [string]$EthernetAlias,
    [string]$WifiAlias,

    [ValidateRange(1, 20)]
    [int]$EthernetWeight = 1,

    [ValidateRange(1, 20)]
    [int]$WifiWeight = 1,

    [ValidateSet('round-robin', 'least-busy')]
    [string]$Strategy = 'round-robin',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 8080,

    [ValidateRange(0, 65535)]
    [int]$SocksPort = 1080,

    [switch]$SetSystemProxy,

    [ValidateRange(1, 9999)]
    [int]$Metric = 20
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BackupPath = Join-Path $ScriptDir 'dual-wan-backup.json'
$ProxyScript = Join-Path $ScriptDir 'dual-wan-proxy.js'
$InternetSettingsRegPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Notify-WinInetProxyChanged {
    try {
        if (-not ('WinInetNative.ProxyRefresh' -as [type])) {
            Add-Type -Namespace 'WinInetNative' -Name 'ProxyRefresh' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
public static extern bool InternetSetOption(System.IntPtr hInternet, int dwOption, System.IntPtr lpBuffer, int dwBufferLength);
'@ | Out-Null
        }
        [void][WinInetNative.ProxyRefresh]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) # SETTINGS_CHANGED
        [void][WinInetNative.ProxyRefresh]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) # REFRESH
    } catch {
        Write-Warning "Could not notify WinINet of proxy change: $($_.Exception.Message)"
    }
}

function Get-NetworkPrefix {
    param([string]$IPv4, [int]$PrefixLength)
    if (-not $IPv4 -or $PrefixLength -le 0 -or $PrefixLength -gt 32) { return $null }
    $bytes = ([System.Net.IPAddress]::Parse($IPv4)).GetAddressBytes()
    $ipInt = [uint64][BitConverter]::ToUInt32(@($bytes[3], $bytes[2], $bytes[1], $bytes[0]), 0)
    $allOnes = [uint64]4294967295
    $mask = ($allOnes -shl (32 - $PrefixLength)) -band $allOnes
    $netInt = [uint32]($ipInt -band $mask)
    $netBytes = [BitConverter]::GetBytes($netInt)
    return "$([int]$netBytes[3]).$([int]$netBytes[2]).$([int]$netBytes[1]).$([int]$netBytes[0])/$PrefixLength"
}

function Get-PhysicalUplinkInfo {
    param([string]$PreferredEth, [string]$PreferredWifi)

    $adapters = Get-NetAdapter -ErrorAction Stop | Where-Object {
        $_.HardwareInterface -eq $true -and
        $_.InterfaceDescription -notmatch 'Virtual|Bluetooth|Hyper-V|VMware|VirtualBox|Tunnel|TAP|WireGuard|ZeroTier|Tailscale|et_\d+'
    }

    $eth = $null
    $wifi = $null

    if ($PreferredEth) {
        $eth = $adapters | Where-Object { $_.Name -eq $PreferredEth } | Select-Object -First 1
    } else {
        $eth = $adapters | Where-Object {
            $_.MediaType -eq '802.3' -and
            $_.PhysicalMediaType -notmatch 'Wireless|Wi-Fi|802\.11' -and
            $_.Name -notmatch 'Wi-Fi|WLAN|Wireless' -and
            $_.InterfaceDescription -notmatch 'Wireless|Wi-Fi|802\.11|WLAN'
        } |
            Sort-Object @{ Expression = { $_.Status -eq 'Up' }; Descending = $true } |
            Select-Object -First 1
    }

    if ($PreferredWifi) {
        $wifi = $adapters | Where-Object { $_.Name -eq $PreferredWifi } | Select-Object -First 1
    } else {
        $wifi = $adapters | Where-Object {
            $_.PhysicalMediaType -match '802\.11|Wireless|Native 802\.11' -or
            $_.Name -match 'Wi-Fi|WLAN|Wireless' -or
            $_.InterfaceDescription -match 'Wireless|Wi-Fi|802\.11|WLAN'
        } |
            Sort-Object @{ Expression = { $_.Status -eq 'Up' }; Descending = $true } |
            Select-Object -First 1
    }

    $result = @()
    foreach ($pair in @(@{ Role = 'Ethernet'; Adapter = $eth }, @{ Role = 'Wi-Fi'; Adapter = $wifi })) {
        $ad = $pair.Adapter
        if (-not $ad) { continue }

        $ipObj = Get-NetIPAddress -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' } |
            Select-Object -First 1
        $iface = Get-NetIPInterface -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        $routes = Get-NetRoute -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
        $gwList = @($routes | ForEach-Object { $_.NextHop })

        $subnet = if ($ipObj) { Get-NetworkPrefix -IPv4 $ipObj.IPAddress -PrefixLength $ipObj.PrefixLength } else { $null }

        $result += [PSCustomObject]@{
            Role            = $pair.Role
            Alias           = $ad.Name
            Description     = $ad.InterfaceDescription
            Status          = $ad.Status
            LinkSpeed       = $ad.LinkSpeed
            ifIndex         = $ad.ifIndex
            IPv4            = if ($ipObj) { $ipObj.IPAddress } else { $null }
            PrefixLength    = if ($ipObj) { $ipObj.PrefixLength } else { $null }
            Subnet          = $subnet
            Gateways        = ($gwList -join ', ')
            InterfaceMetric = if ($iface) { $iface.InterfaceMetric } else { $null }
            AutomaticMetric = if ($iface) { $iface.AutomaticMetric } else { $null }
        }
    }

    return $result
}

function Show-TopologyDiagnostics {
    param($Infos)

    Write-Host "`n=== Detected Dual-WAN Adapter Status ===" -ForegroundColor Cyan
    $Infos | Format-Table Role, Alias, Status, LinkSpeed, IPv4, Subnet, Gateways, InterfaceMetric, AutomaticMetric -AutoSize

    $eth = $Infos | Where-Object { $_.Role -eq 'Ethernet' } | Select-Object -First 1
    $wifi = $Infos | Where-Object { $_.Role -eq 'Wi-Fi' } | Select-Object -First 1

    if (-not $eth -or $eth.Status -ne 'Up' -or -not $eth.IPv4) {
        Write-Warning "Ethernet adapter is not connected with a valid IPv4 address."
    } elseif ($eth.LinkSpeed -eq '100 Mbps') {
        Write-Host "[Info] Ethernet link is capped at 100 Mbps (Fast Ethernet on TP-Link router/cable)." -ForegroundColor Yellow
    }

    if (-not $wifi -or $wifi.Status -ne 'Up' -or -not $wifi.IPv4) {
        Write-Warning "Wi-Fi adapter is currently disconnected or has no IPv4 address. Connect Wi-Fi to your router first."
    }

    if ($eth -and $wifi -and $eth.Subnet -and $wifi.Subnet -and ($eth.Subnet -eq $wifi.Subnet)) {
        Write-Warning @"
BOTH Ethernet ($($eth.IPv4)) and Wi-Fi ($($wifi.IPv4)) are on the SAME subnet ($($eth.Subnet)).
When TP-Link is in AP/Bridge mode on the same subnet, Windows/router ARP can conflict or favor one link.
Recommended: Put the TP-Link router in Router mode (WAN->Main Router) with its own LAN subnet (e.g. 192.168.0.1/24),
keeping Main Router on 192.168.1.1/24.
"@
    }

    $persistentDefaults = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore PersistentStore -ErrorAction SilentlyContinue
    if ($persistentDefaults) {
        Write-Host "[Info] Persistent 0.0.0.0/0 routes present in Windows:" -ForegroundColor Yellow
        $persistentDefaults | Format-Table InterfaceAlias, NextHop, RouteMetric -AutoSize
    }
}

function Save-Backup {
    param($Infos, [int]$LocalProxyPort = 8080)
    $existing = $null
    if (Test-Path $BackupPath) {
        try { $existing = Get-Content -Path $BackupPath -Raw | ConvertFrom-Json } catch { $existing = $null }
    }

    $interfacesBackup = if ($existing -and $existing.Interfaces) {
        $existing.Interfaces
    } else {
        @($Infos | ForEach-Object {
            @{
                Alias           = $_.Alias
                ifIndex         = $_.ifIndex
                AutomaticMetric = [string]$_.AutomaticMetric
                InterfaceMetric = $_.InterfaceMetric
            }
        })
    }

    $reg = Get-ItemProperty -Path $InternetSettingsRegPath -ErrorAction SilentlyContinue
    $currentServer = if ($null -ne $reg.ProxyServer) { [string]$reg.ProxyServer } else { '' }
    $proxyBackup = if ($existing -and $existing.SystemProxy -and $currentServer -eq "127.0.0.1:$LocalProxyPort") {
        $existing.SystemProxy
    } else {
        @{
            ProxyEnable   = if ($null -ne $reg.ProxyEnable) { [int]$reg.ProxyEnable } else { 0 }
            ProxyServer   = $currentServer
            ProxyOverride = if ($null -ne $reg.ProxyOverride) { [string]$reg.ProxyOverride } else { '<local>' }
        }
    }

    $backup = @{
        Timestamp   = (Get-Date).ToString('o')
        Interfaces  = $interfacesBackup
        SystemProxy = $proxyBackup
    }
    $backup | ConvertTo-Json -Depth 5 | Set-Content -Path $BackupPath -Encoding UTF8
}

function Set-UserSystemProxy {
    param([int]$Port)
    Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyEnable -Value 1 -Type DWord
    Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyServer -Value "127.0.0.1:$Port" -Type String
    Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyOverride -Value '<local>;192.168.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*.*;172.30.*;172.31.*' -Type String
    Notify-WinInetProxyChanged
}

function Restore-UserSystemProxy {
    if (Test-Path $BackupPath) {
        $backup = Get-Content -Path $BackupPath -Raw | ConvertFrom-Json
        if ($backup.SystemProxy) {
            Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyEnable -Value ([int]$backup.SystemProxy.ProxyEnable) -Type DWord
            $savedServer = if ($null -ne $backup.SystemProxy.ProxyServer) { [string]$backup.SystemProxy.ProxyServer } else { '' }
            Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyServer -Value $savedServer -Type String
            if ($backup.SystemProxy.ProxyOverride) {
                Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyOverride -Value ([string]$backup.SystemProxy.ProxyOverride) -Type String
            }
            Notify-WinInetProxyChanged
            return
        }
    }
    Set-ItemProperty -Path $InternetSettingsRegPath -Name ProxyEnable -Value 0 -Type DWord
    Notify-WinInetProxyChanged
}

# --- Main Execution ---
$infos = Get-PhysicalUplinkInfo -PreferredEth $EthernetAlias -PreferredWifi $WifiAlias

switch ($Mode) {
    'Status' {
        Show-TopologyDiagnostics -Infos $infos
    }

    'EqualMetrics' {
        if (-not (Test-IsAdmin)) {
            Write-Error "EqualMetrics mode modifies Windows interface metrics and requires Administrator privileges. Re-run PowerShell as Administrator."
            exit 1
        }
        Show-TopologyDiagnostics -Infos $infos
        $active = @($infos | Where-Object { $_.Status -eq 'Up' -and $_.IPv4 })
        if ($active.Count -lt 2) {
            Write-Error "Need both Ethernet and Wi-Fi connected with IPv4 addresses before setting equal metrics."
            exit 1
        }
        Save-Backup -Infos $infos
        foreach ($item in $active) {
            Set-NetIPInterface -InterfaceIndex $item.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric $Metric
            Write-Host "Set '$($item.Alias)' ($($item.IPv4)) -> AutomaticMetric=Disabled, InterfaceMetric=$Metric" -ForegroundColor Green
        }
        Write-Host "`nDone. Note: Windows equal metrics balance *new separate connections* only (not a single download stream)." -ForegroundColor Cyan
        Write-Host "For active browser/IDM/aria2 multi-connection load balancing, run: .\dual-wan-manager.ps1 -Mode Proxy -SetSystemProxy" -ForegroundColor Cyan
    }

    'Proxy' {
        Show-TopologyDiagnostics -Infos $infos
        $active = @($infos | Where-Object { $_.Status -eq 'Up' -and $_.IPv4 })
        if ($active.Count -lt 2) {
            Write-Error "Need both Ethernet and Wi-Fi connected with IPv4 addresses to load-balance across both links."
            exit 1
        }
        if (-not (Test-Path $ProxyScript)) {
            Write-Error "Missing proxy script: $ProxyScript"
            exit 1
        }
        $nodeCmd = Get-Command node.exe -ErrorAction SilentlyContinue
        if (-not $nodeCmd) {
            Write-Error "node.exe was not found in PATH. Install Node.js or add it to PATH."
            exit 1
        }

        $uplinkSpecs = @()
        foreach ($item in $active) {
            $w = if ($item.Role -eq 'Ethernet') { $EthernetWeight } else { $WifiWeight }
            $uplinkSpecs += "$($item.Role)=$($item.IPv4):$w"
        }
        $uplinksArg = $uplinkSpecs -join ','

        if ($SetSystemProxy) {
            Save-Backup -Infos $infos -LocalProxyPort $HttpPort
            Set-UserSystemProxy -Port $HttpPort
            Write-Host "Enabled Windows User Proxy -> 127.0.0.1:$HttpPort (will auto-restore on exit)" -ForegroundColor Green
        }

        try {
            & $nodeCmd.Source $ProxyScript `
                --uplinks $uplinksArg `
                --http-port $HttpPort `
                --socks-port $SocksPort `
                --strategy $Strategy
        } finally {
            if ($SetSystemProxy) {
                Restore-UserSystemProxy
                Write-Host "Restored previous Windows User Proxy settings." -ForegroundColor Yellow
            }
        }
    }

    'Restore' {
        Restore-UserSystemProxy
        Write-Host "Windows User Proxy disabled/restored." -ForegroundColor Green

        if (Test-IsAdmin) {
            if (Test-Path $BackupPath) {
                $backup = Get-Content -Path $BackupPath -Raw | ConvertFrom-Json
                foreach ($saved in $backup.Interfaces) {
                    if ($saved.AutomaticMetric -eq 'Enabled') {
                        Set-NetIPInterface -InterfaceIndex ([int]$saved.ifIndex) -AddressFamily IPv4 -AutomaticMetric Enabled
                    } else {
                        Set-NetIPInterface -InterfaceIndex ([int]$saved.ifIndex) -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric ([int]$saved.InterfaceMetric)
                    }
                    Write-Host "Restored metric for '$($saved.Alias)'." -ForegroundColor Green
                }
            } else {
                foreach ($item in $infos) {
                    Set-NetIPInterface -InterfaceIndex $item.ifIndex -AddressFamily IPv4 -AutomaticMetric Enabled
                    Write-Host "Re-enabled AutomaticMetric for '$($item.Alias)'." -ForegroundColor Green
                }
            }
        } else {
            Write-Host "Skipped restoring interface metrics (run as Administrator if you used -Mode EqualMetrics)." -ForegroundColor Yellow
        }
    }
}
