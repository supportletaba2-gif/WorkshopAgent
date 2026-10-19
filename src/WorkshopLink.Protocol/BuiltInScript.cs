namespace WorkshopLink.Protocol;

public static class BuiltInScript
{
    public const string Text = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

        $target = $env:WLINK_HOST
        $action = $env:WLINK_ACTION
        if ([string]::IsNullOrWhiteSpace($target)) { throw 'Select a PC first.' }

        function Invoke-Command {
          param($Session, [ScriptBlock]$ScriptBlock, $ArgumentList)
          if ($null -eq $ScriptBlock) { throw 'Missing script.' }
          if ($null -eq $ArgumentList) { return & $ScriptBlock }
          if ($ArgumentList -is [object[]]) { return & $ScriptBlock @ArgumentList }
          return & $ScriptBlock $ArgumentList
        }
        $session = 'local'
        try {
          switch ($action) {
            'info' {
              $info = Invoke-Command -Session $session -ScriptBlock {
                $os = Get-CimInstance Win32_OperatingSystem
                $cs = Get-CimInstance Win32_ComputerSystem
                $cpu = @(Get-CimInstance Win32_Processor) | Select-Object -First 1
                $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
                  [pscustomobject]@{
                    DeviceID = $_.DeviceID
                    SizeGB = [math]::Round(($_.Size / 1GB), 1)
                    FreeGB = [math]::Round(($_.FreeSpace / 1GB), 1)
                  }
                })
                $diskPct = 0
                try {
                  $system = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
                  if ($null -ne $system -and $system.Size -gt 0) { $diskPct = [int][math]::Round((($system.Size - $system.FreeSpace) / $system.Size) * 100, 0) }
                } catch { }
                $ramPct = 0
                try {
                  $totalKb = [double]$os.TotalVisibleMemorySize
                  $freeKb = [double]$os.FreePhysicalMemory
                  if ($totalKb -gt 0) { $ramPct = [int][math]::Round((($totalKb - $freeKb) / $totalKb) * 100, 0) }
                } catch { }
                $cpuPct = 0
                try { $cpuPct = [int]$cpu.LoadPercentage } catch { }
                $uptime = ''
                try {
                  $span = (Get-Date) - [datetime]$os.LastBootUpTime
                  $uptime = ([int]$span.TotalDays).ToString() + 'd ' + $span.Hours.ToString() + 'h'
                } catch { }
                $gateway = ''
                try { $gateway = [string](Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Select-Object -First 1 -ExpandProperty NextHop) } catch { }
                $dns = ''
                try {
                  $dns = [string]((Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | Select-Object -First 1).ServerAddresses -join ', ')
                } catch { }
                [pscustomobject]@{
                  Hostname = $env:COMPUTERNAME
                  Os = [string]$os.Caption
                  Version = [string]$os.Version
                  Arch = $env:PROCESSOR_ARCHITECTURE
                  Cpu = [string]$cpu.Name
                  CpuPercent = $cpuPct
                  RamGB = [math]::Round(($cs.TotalPhysicalMemory / 1GB), 1)
                  RamPercent = $ramPct
                  DiskPercent = $diskPct
                  Uptime = $uptime
                  Gateway = $gateway
                  Dns = $dns
                  User = [string]$cs.UserName
                  Who = $(
                    $signedIn = ''
                    try { $signedIn = [string][System.Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { }
                    if ([string]::IsNullOrWhiteSpace($signedIn)) { $signedIn = (([string]$env:USERDOMAIN + '\' + [string]$env:USERNAME).Trim('\')) }
                    $signedIn
                  )
                  Disks = $disks
                }
              }
              $info | ConvertTo-Json -Depth 4 -Compress
            }
            'copyto' {
              $remote = $env:WLINK_REMOTE
              $file = $env:WLINK_FILE
              if (-not (Test-Path -LiteralPath $file)) { throw 'The file was not received.' }
              $folder = Split-Path -Parent $remote
              if ($folder) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
              Copy-Item -LiteralPath $file -Destination $remote -Force
              Write-Output "Copied to $remote"
            }
            'copyfrom' {
              $remote = $env:WLINK_REMOTE
              $out = $env:WLINK_OUT
              if (-not (Test-Path -LiteralPath $remote)) { throw 'That file was not found on this PC.' }
              Copy-Item -LiteralPath $remote -Destination $out -Force
              Write-Output "Copied from $remote"
            }
            'desktop' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $caption = [string](Get-CimInstance Win32_OperatingSystem).Caption
                if ($caption -match 'Home') { throw 'This PC is Windows Home. Remote Desktop can be opened on Pro or Enterprise.' }
                $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
                if (Test-Path $policy) {
                  $blocked = (Get-ItemProperty -Path $policy -Name 'fDenyTSConnections' -ErrorAction SilentlyContinue).fDenyTSConnections
                  if ($blocked -eq 1) { throw 'A Windows policy on this PC is blocking Remote Desktop.' }
                }
                Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Type DWord -Value 0
                Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'fEnableWinStation' -Type DWord -Value 1
                Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'UserAuthentication' -Type DWord -Value 1
                Set-Service -Name 'TermService' -StartupType Automatic
                Start-Service -Name 'TermService'
                $rules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue)
                if ($rules.Count -eq 0) {
                  $rules = @(Get-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue)
                }
                if ($rules.Count -eq 0) { throw 'Windows did not find the Remote Desktop firewall rules on this PC.' }
                foreach ($rule in $rules) {
                  Set-NetFirewallRule -Name $rule.Name -Enabled True -Profile Any -Action Allow -RemoteAddress '10.0.0.0/8','172.16.0.0/12','192.168.0.0/16' | Out-Null
                }
                $port = [int](Get-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'PortNumber').PortNumber
                if ($port -lt 1 -or $port -gt 65535) { $port = 3389 }
                $named = Get-NetFirewallRule -Name 'WorkshopLink-RDP' -ErrorAction SilentlyContinue
                if (-not $named) {
                  New-NetFirewallRule -Name 'WorkshopLink-RDP' -DisplayName 'Workshop Link (Remote Desktop)' -Description 'Workshop Link Remote Desktop. Local networks only.' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -RemoteAddress '10.0.0.0/8','172.16.0.0/12','192.168.0.0/16' -Profile Any | Out-Null
                } else {
                  Set-NetFirewallRule -Name 'WorkshopLink-RDP' -Enabled True -Profile Any -Action Allow -RemoteAddress '10.0.0.0/8','172.16.0.0/12','192.168.0.0/16' -LocalPort $port
                }
                $rdpUsers = Get-LocalGroup | Where-Object { $_.SID.Value -eq 'S-1-5-32-555' } | Select-Object -First 1
                if ($rdpUsers) {
                  $rdpMembers = @(Get-LocalGroupMember -Group $rdpUsers.Name -ErrorAction SilentlyContinue)
                  $remoteAccount = ''
                  $accountFile = 'C:\ProgramData\WorkshopLink\account.txt'
                  if (Test-Path -LiteralPath $accountFile) {
                    $remoteAccount = [string](Get-Content -LiteralPath $accountFile -TotalCount 1)
                  }
                  $remoteAccount = $remoteAccount.Trim()
                  if ($remoteAccount -and -not ($rdpMembers | Where-Object { $_.Name -eq $remoteAccount -or $_.Name -like ('*\' + $remoteAccount) })) {
                    Add-LocalGroupMember -Group $rdpUsers.Name -Member $remoteAccount
                  }
                }
                $terminal = Get-CimInstance -Namespace 'root/CIMV2/TerminalServices' -ClassName 'Win32_TerminalServiceSetting'
                $allowed = Invoke-CimMethod -InputObject $terminal -MethodName 'SetAllowTSConnections' -Arguments @{ AllowTSConnections = 1; ModifyFirewallException = 1 }
                if ($allowed.ReturnValue -ne 0) { throw "Windows did not turn on Remote Desktop. Code $($allowed.ReturnValue)." }
                Start-Service -Name 'TermService'
                $listening = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
                if ($listening.Count -eq 0) { throw "Remote Desktop is enabled, but nothing is listening on port $port." }
                "WLINK_RDP $port"
                'Remote Desktop is on for this PC. Only the workshop LAN can connect. Network Level Authentication stays on.'
              }
              Write-Output $text
            }
            'winhance' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $folder = 'C:\WorkshopLink\Incoming'
                New-Item -ItemType Directory -Force -Path $folder | Out-Null
                $file = Join-Path $folder 'Winhance.Installer.exe'
                $url = 'https://github.com/memstechtips/Winhance/releases/download/v26.06.12/Winhance.Installer.exe'
                $expected = '0426D513B8840BC9E31589253A2C9D5EE56B146B2B5B0B4513BCBFBF4D40D46E'
                $have = $false
                if (Test-Path -LiteralPath $file) {
                  $have = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $expected
                  if (-not $have) { Remove-Item -LiteralPath $file -Force }
                }
                if (-not $have) {
                  Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
                }
                $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
                if ($hash -ne $expected) {
                  Remove-Item -LiteralPath $file -Force
                  throw 'The Winhance installer did not match the official GitHub release. It was not run.'
                }
                $process = New-Object System.Diagnostics.Process
                $process.StartInfo.FileName = $file
                $process.StartInfo.Arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="C:\Program Files\Winhance" /TASKS="regularinstall,regularinstall\desktopicon,regularinstall\startmenuicon"'
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.CreateNoWindow = $true
                if (-not $process.Start()) { throw 'The Winhance installer did not start.' }
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) { throw "The Winhance installer exited with code $($process.ExitCode)." }
                $exe = 'C:\Program Files\Winhance\Winhance.exe'
                if (-not (Test-Path -LiteralPath $exe)) { throw 'Winhance did not finish installing.' }
                'Winhance 26.06.12 is installed from the official GitHub release. Open the desktop, then start Winhance. Nothing on Windows was changed by this install. The tweaks stay inside Winhance until you choose them there.'
              }
              Write-Output $text
            }
            'tune' {
              $raw = [string]$env:WLINK_CMD
              function Get-SignedInSid {
                $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
                if (-not $explorer) { return $null }
                $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
                if (-not $owner.User) { return $null }
                $account = New-Object System.Security.Principal.NTAccount($owner.Domain, $owner.User)
                return $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
              }
              if (-not (Get-PSDrive HKU -ErrorAction SilentlyContinue)) { New-PSDrive -Name HKU -PSProvider Registry -Root HKEY_USERS | Out-Null }
              $sid = Get-SignedInSid
              function Read-Flag($path, $name, $onValue) {
                $item = Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue
                if (-not $item) { return 'off' }
                if ([string]$item.$name -eq [string]$onValue) { return 'on' }
                return 'off'
              }
              function Write-Tune($id, $state) { Write-Output ($id + '=' + $state) }
              if ([string]::IsNullOrWhiteSpace($raw) -or $raw -eq 'read') {
                $uac = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
                $never = ($uac.ConsentPromptBehaviorAdmin -eq 0 -and $uac.PromptOnSecureDesktop -eq 0)
                Write-Tune 'uac-never' $(if ($never) {'on'} else {'off'})
                Write-Tune 'workplace-join' (Read-Flag 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WorkplaceJoin' 'BlockAADWorkplaceJoin' 1)
                Write-Tune 'bitlocker-auto' (Read-Flag 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 1)
                Write-Tune 'wifi-sense' (Read-Flag 'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config' 'AutoConnectAllowedOEM' 0)
                Write-Tune 'maintenance' (Read-Flag 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' 'MaintenanceDisabled' 1)
                Write-Tune 'error-reporting' (Read-Flag 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled' 1)
                Write-Tune 'remote-assist' (Read-Flag 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance' 'fAllowToGetHelp' 1)
                Write-Output 'power-read=ok'
                if ($sid) { Write-Output ('user=' + $sid) } else { Write-Output 'user=none' }
              } else {
                foreach ($id in ($raw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
                  switch ($id) {
                    'uac-never' {
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' ConsentPromptBehaviorAdmin 0
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' PromptOnSecureDesktop 0
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' EnableLUA 1
                    }
                    'uac-default' {
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' ConsentPromptBehaviorAdmin 5
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' PromptOnSecureDesktop 1
                      Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' EnableLUA 1
                    }
                    'workplace-join-off' { New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WorkplaceJoin' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WorkplaceJoin' BlockAADWorkplaceJoin 1 }
                    'bitlocker-auto-off' { New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' -Force | Out-Null; Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' PreventDeviceEncryption 1 }
                    'wifi-sense-off' { New-Item 'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config' AutoConnectAllowedOEM 0 }
                    'maintenance-off' { New-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' MaintenanceDisabled 1 }
                    'error-reporting-off' { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' Disabled 1 }
                    'remote-assist-on' { Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance' fAllowToGetHelp 1 }
                    'power-balanced' { powercfg /setactive SCHEME_BALANCED | Out-Null }
                    'display-never' { powercfg /change monitor-timeout-ac 0 | Out-Null; powercfg /change monitor-timeout-dc 0 | Out-Null }
                    'disk-timeout' { powercfg /change disk-timeout-ac 20 | Out-Null; powercfg /change disk-timeout-dc 10 | Out-Null }
                    'updates-soon-on' { New-Item 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' IsContinuousInnovationOptedIn 1 }
                    'other-products-off' { New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' AllowMUUpdateService 0 }
                    'active-hours-restart-off' { New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' SetActiveHours 1 }
                    'store-autoupdate-on' { New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' AutoDownload 4 }
                    'restart-after-update-on' { New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -Force | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' NoAutoRebootWithLoggedOnUsers 0 }
                    'smart-app-off' { New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Force | Out-Null; Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' VerifiedAndReputablePolicyState 0 }
                    'notes-on' { if (-not $sid) { throw 'Sign someone in on that PC before changing notifications.' }; Set-ItemProperty "HKU:\$sid\Software\Microsoft\Windows\CurrentVersion\PushNotifications" ToastEnabled 1 }
                    'bell-off' { if (-not $sid) { throw 'Sign someone in on that PC before changing notifications.' }; New-Item "HKU:\$sid\Software\Microsoft\Windows\CurrentVersion\Explorer" -Force | Out-Null; Set-ItemProperty "HKU:\$sid\Software\Microsoft\Windows\CurrentVersion\Explorer" DisableNotificationCenter 1 }
                    'welcome-on' { if (-not $sid) { throw 'Sign someone in on that PC before changing notifications.' }; Set-ItemProperty "HKU:\$sid\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" 'SubscribedContent-310093Enabled' 1 }
                    'suggestions-on' { if (-not $sid) { throw 'Sign someone in on that PC before changing notifications.' }; Set-ItemProperty "HKU:\$sid\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" 'SubscribedContent-338388Enabled' 1 }
                    default { throw "That setting is not in the workshop list: $id" }
                  }
                  Write-Output ("Applied " + $id)
                }
              }
            }
            'inbox' {
              $raw = [string]$env:WLINK_CMD
              $split = $raw.Split(':', 2)
              if ($split.Length -ne 2 -or $split[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9._]{2,80}$') { throw 'That Windows app is not in the workshop list.' }
              $name = $split[1]
              if ($split[0] -eq 'remove') {
                Get-AppxPackage -AllUsers -Name $name -ErrorAction SilentlyContinue | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
                Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName | Out-Null }
                "Removed $name for new accounts. A person already signed in may still see it until they sign out."
              } elseif ($split[0] -eq 'add') {
                $pkg = Get-AppxPackage -AllUsers -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $pkg) { throw "$name is not on this PC to register again." }
                Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $pkg.InstallLocation 'AppXManifest.xml')
                "Registered $name."
              } else { throw 'That Windows app action is not available.' }
            }
            'feature' {
              $raw = [string]$env:WLINK_CMD
              $split = $raw.Split(':', 2)
              if ($split.Length -ne 2 -or $split[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{2,80}$') { throw 'That Windows feature is not in the workshop list.' }
              $name = $split[1]
              if ($split[0] -eq 'on') {
                $result = Enable-WindowsOptionalFeature -Online -FeatureName $name -NoRestart -All
                if ($result.RestartNeeded) { "Enabled $name. Restart this PC to finish." } else { "Enabled $name." }
              } elseif ($split[0] -eq 'off') {
                $result = Disable-WindowsOptionalFeature -Online -FeatureName $name -NoRestart
                if ($result.RestartNeeded) { "Turned off $name. Restart this PC to finish." } else { "Turned off $name." }
              } else { throw 'That Windows feature action is not available.' }
            }
            'install' {
              $id = $env:WLINK_PKG
              if ([string]::IsNullOrWhiteSpace($id)) { throw 'Choose an application.' }
              if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$') { throw 'That application is not in the workshop list.' }
              $text = Invoke-Command -Session $session -ScriptBlock {
                param($id)
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                function Select-CatalogInstaller($yaml, $versionName, $architecture) {
                  $pkgType = $null
                  $pkgScope = $null
                  $pkgSilent = $null
                  $inSwitches = $false
                  $inInstallers = $false
                  $current = $null
                  $all = @()
                  foreach ($raw in ($yaml -split "`n")) {
                    $line = $raw.TrimEnd("`r")
                    if ($line -match '^Installers:\s*$') { $inInstallers = $true; $inSwitches = $false; continue }
                    if (-not $inInstallers) {
                      if ($line -match '^InstallerType:\s*(.+)$') { $pkgType = $Matches[1].Trim() }
                      elseif ($line -match '^Scope:\s*(.+)$') { $pkgScope = $Matches[1].Trim() }
                      elseif ($line -match '^InstallerSwitches:\s*$') { $inSwitches = $true }
                      elseif ($inSwitches -and $line -match '^\s+Silent:\s*(.+)$') { $pkgSilent = $Matches[1].Trim().Trim('"').Trim("'"); $inSwitches = $false }
                      elseif ($line -match '^\S') { $inSwitches = $false }
                      continue
                    }
                    if ($line -match '^\s*-\s+Architecture:\s*(\S+)') {
                      if ($current) { $all += [pscustomobject]$current }
                      $current = @{ Architecture = $Matches[1]; Type = $pkgType; Scope = $pkgScope; Silent = $pkgSilent; Url = $null; Sha256 = $null }
                      $inSwitches = $false
                      continue
                    }
                    if (-not $current) { continue }
                    if ($line -match '^\s+InstallerType:\s*(\S+)') { $current.Type = $Matches[1].Trim(); $inSwitches = $false; continue }
                    if ($line -match '^\s+Scope:\s*(\S+)') { $current.Scope = $Matches[1].Trim(); continue }
                    if ($line -match '^\s+InstallerUrl:\s*(\S+)') { $current.Url = $Matches[1].Trim(); continue }
                    if ($line -match '^\s+InstallerSha256:\s*([A-Fa-f0-9]+)') { $current.Sha256 = $Matches[1].ToUpperInvariant(); continue }
                    if ($line -match '^\s+InstallerSwitches:\s*$') { $inSwitches = $true; continue }
                    if ($inSwitches -and $line -match '^\s+Silent:\s*(.+)$') { $current.Silent = $Matches[1].Trim().Trim('"').Trim("'"); $inSwitches = $false; continue }
                  }
                  if ($current) { $all += [pscustomobject]$current }
                  $usable = @($all | Where-Object {
                    $_.Architecture -eq $architecture -and $_.Url -and $_.Sha256 -and $_.Type -in @('wix','msi','nullsoft','inno','burn','exe') -and $_.Scope -ne 'user'
                  })
                  if ($usable.Count -eq 0) { throw "No $architecture installer was published for $versionName." }
                  $rank = @{ wix = 0; msi = 1; nullsoft = 2; inno = 3; burn = 4; exe = 5 }
                  $chosen = $usable | Sort-Object { $rank[$_.Type] } | Select-Object -First 1
                  $silent = $chosen.Silent
                  switch ($chosen.Type) {
                    'nullsoft' { if (-not $silent) { $silent = '/S' } }
                    'inno' { if (-not $silent) { $silent = '/VERYSILENT /NORESTART /SP-' } }
                    'burn' { if (-not $silent) { $silent = '/quiet /norestart' } }
                    'exe' { if (-not $silent) { throw "The $versionName installer has no silent switch." } }
                  }
                  [pscustomobject]@{ Version = $versionName; Type = $chosen.Type; Url = $chosen.Url; Sha256 = $chosen.Sha256; Silent = $silent }
                }
                function Get-CatalogInstaller($packageId) {
                  $headers = @{ 'User-Agent' = 'WorkshopLink'; Accept = 'application/vnd.github+json' }
                  $parts = $packageId.Split('.')
                  $letter = $parts[0].Substring(0, 1).ToLower()
                  $base = "manifests/$letter/" + (($parts | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
                  try { $raw = Invoke-RestMethod -Uri "https://api.github.com/repos/microsoft/winget-pkgs/contents/$base" -Headers $headers }
                  catch { throw "The package list could not be reached for $packageId." }
                  $items = @($raw)
                  if ($items.Count -eq 1 -and $items[0] -is [System.Array]) { $items = @($items[0]) }
                  $best = $null
                  $bestName = $null
                  foreach ($item in $items) {
                    if ($item.type -ne 'dir') { continue }
                    try { $version = [version]$item.name } catch { continue }
                    if ($null -eq $best -or $version -gt $best) { $best = $version; $bestName = [string]$item.name }
                  }
                  if (-not $bestName) { throw "No published installer was listed for $packageId." }
                  $yamlUrl = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/$base/$bestName/$packageId.installer.yaml"
                  $yaml = (Invoke-WebRequest -Uri $yamlUrl -UseBasicParsing -Headers @{ 'User-Agent' = 'WorkshopLink' }).Content
                  $architecture = if ($packageId -like '*.32-bit' -or $packageId -like '*.x86') { 'x86' } else { 'x64' }
                  try { Select-CatalogInstaller $yaml $bestName $architecture }
                  catch { if ($architecture -eq 'x64') { Select-CatalogInstaller $yaml $bestName 'x86' } else { throw } }
                }
                function Install-CatalogFile($file, $type, $silent) {
                  $process = New-Object System.Diagnostics.Process
                  $process.StartInfo.UseShellExecute = $false
                  $process.StartInfo.CreateNoWindow = $true
                  if ($type -eq 'wix' -or $type -eq 'msi') {
                    $process.StartInfo.FileName = "$env:SystemRoot\System32\msiexec.exe"
                    $process.StartInfo.Arguments = "/i `"$file`" /qn /norestart"
                  } else {
                    $process.StartInfo.FileName = $file
                    $process.StartInfo.Arguments = [string]$silent
                  }
                  if (-not $process.Start()) { throw 'The installer did not start.' }
                  $process.WaitForExit()
                  $process.ExitCode
                }
                $winget = $null
                $cmd = Get-Command winget -ErrorAction SilentlyContinue
                if ($cmd) { $winget = $cmd.Source }
                if (-not $winget) {
                  $found = Get-ChildItem 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller*' -Filter winget.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                  if ($found) { $winget = $found.FullName }
                }
                if ($winget) {
                  $wing = New-Object System.Diagnostics.Process
                  $wing.StartInfo.FileName = $winget
                  $wing.StartInfo.Arguments = "install --id `"$id`" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"
                  $wing.StartInfo.UseShellExecute = $false
                  $wing.StartInfo.CreateNoWindow = $true
                  if ($wing.Start()) {
                    $wing.WaitForExit()
                    if ($wing.ExitCode -eq 0) { "Installed $id with the Windows package manager."; return }
                  }
                }
                $picked = Get-CatalogInstaller $id
                $folder = 'C:\WorkshopLink\Incoming\installers'
                New-Item -ItemType Directory -Force -Path $folder | Out-Null
                $name = [IO.Path]::GetFileName(([Uri]$picked.Url).AbsolutePath)
                if ([string]::IsNullOrWhiteSpace($name)) { $name = "$id.bin" }
                $file = Join-Path $folder $name
                Invoke-WebRequest -Uri $picked.Url -OutFile $file -UseBasicParsing
                $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
                if ($hash -ne $picked.Sha256) {
                  Remove-Item -LiteralPath $file -Force
                  throw 'The installer did not match the Microsoft package list. It was not run.'
                }
                $code = Install-CatalogFile $file $picked.Type $picked.Silent
                if ($code -notin 0, 3010, 1641, 1638) { throw "The installer exited with code $code. The file is still at $file." }
                Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
                $note = switch ($code) {
                  3010 { ' Windows asked for a restart to finish.' }
                  1641 { ' Windows is restarting to finish the install.' }
                  1638 { ' It was already installed.' }
                  default { '' }
                }
                "Installed $id $($picked.Version) from the publisher. The file matched the Microsoft package list.$note"
              } -ArgumentList $id
              Write-Output $text
            }
            'uninstall' {
              $id = $env:WLINK_PKG
              if ([string]::IsNullOrWhiteSpace($id)) { throw 'Choose an application.' }
              $fragment = switch ($id) {
                'TeamViewer.TeamViewer' { 'TeamViewer' }
                'Adobe.Acrobat.Reader.32-bit' { 'Adobe Acrobat' }
                'Google.Chrome' { 'Google Chrome' }
                'Mozilla.Firefox' { 'Mozilla Firefox' }
                'Brave.Brave' { 'Brave' }
                'Microsoft.EdgeWebView2Runtime' { 'Microsoft Edge WebView2' }
                'Microsoft.DirectX' { 'DirectX' }
                'Microsoft.DotNet.DesktopRuntime.8' { '.NET Desktop Runtime 8' }
                'Microsoft.DotNet.DesktopRuntime.7' { '.NET Desktop Runtime 7' }
                'Microsoft.VCRedist.2015+.x64' { '2015-2022 Redistributable (x64)' }
                'Microsoft.VCRedist.2015+.x86' { '2015-2022 Redistributable (x86)' }
                'Microsoft.VCRedist.2013.x64' { '2013 Redistributable (x64)' }
                'Microsoft.VCRedist.2013.x86' { '2013 Redistributable (x86)' }
                'Microsoft.VCRedist.2010.x64' { '2010  x64' }
                'Microsoft.VCRedist.2010.x86' { '2010  x86' }
                'Microsoft.VCRedist.2012.x64' { '2012 Redistributable (x64)' }
                'Microsoft.VCRedist.2012.x86' { '2012 Redistributable (x86)' }
                'Microsoft.VCRedist.2008.x64' { '2008 Redistributable - x64' }
                'Microsoft.VCRedist.2008.x86' { '2008 Redistributable - x86' }
                'Microsoft.DotNet.Runtime.6' { '.NET Runtime 6' }
                'Oracle.JavaRuntimeEnvironment' { 'Java' }
                'Opera.Opera' { 'Opera' }
                'Vivaldi.Vivaldi' { 'Vivaldi' }
                'LibreWolf.LibreWolf' { 'LibreWolf' }
                default { ($id.Split('.') | Select-Object -Last 1) }
              }
              $text = Invoke-Command -Session $session -ScriptBlock {
                param($fragment)
                $ErrorActionPreference = 'Stop'
                $paths = @(
                  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
                  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
                )
                $entry = Get-ItemProperty -Path $paths -ErrorAction SilentlyContinue |
                  Where-Object { $_.DisplayName -and $_.DisplayName -like ('*' + $fragment + '*') -and -not $_.SystemComponent } |
                  Select-Object -First 1
                if (-not $entry) { "$fragment is not installed."; return }
                $cmd = $entry.QuietUninstallString
                if ([string]::IsNullOrWhiteSpace($cmd)) { $cmd = $entry.UninstallString }
                if ([string]::IsNullOrWhiteSpace($cmd)) { throw "Windows did not list an uninstall command for $fragment." }
                if ($cmd -match 'msiexec') {
                  $cmd = $cmd -replace '(?i)/I(\{)', '/X$1'
                  if ($cmd -notmatch '(?i)/qn') { $cmd = $cmd + ' /qn /norestart' }
                }
                $process = New-Object System.Diagnostics.Process
                if ($cmd -match '^\s*"([^"]+)"\s*(.*)$') {
                  $process.StartInfo.FileName = $Matches[1]
                  $process.StartInfo.Arguments = $Matches[2]
                } elseif ($cmd -match '^\s*(\S+)\s*(.*)$') {
                  $process.StartInfo.FileName = $Matches[1]
                  $process.StartInfo.Arguments = $Matches[2]
                } else {
                  throw 'Windows listed an uninstall command that could not be run.'
                }
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.CreateNoWindow = $true
                if (-not $process.Start()) { throw 'The uninstall did not start.' }
                $process.WaitForExit()
                if ($process.ExitCode -in 1605) { "$fragment is not installed."; return }
                if ($process.ExitCode -notin 0, 3010, 1641) { throw "The uninstall exited with code $($process.ExitCode)." }
                "Removed $fragment."
              } -ArgumentList $fragment
              Write-Output $text
            }
            'programs' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $paths = @(
                  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
                  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
                )
                $names = @(Get-ItemProperty -Path $paths -ErrorAction SilentlyContinue |
                  Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
                  Select-Object -ExpandProperty DisplayName -Unique |
                  Sort-Object)
                if ($names.Count -eq 0) { 'No installed applications were listed.' } else { ($names | Select-Object -First 300) -join "`n" }
              }
              Write-Output $text
            }
            'updates' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $folder = 'C:\WorkshopLink\Incoming'
                New-Item -ItemType Directory -Force -Path $folder | Out-Null
                $resultPath = Join-Path $folder 'windows-updates.txt'
                $runner = Join-Path $folder 'windows-updates.ps1'
                if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
                $scriptLines = @(
                  '$ErrorActionPreference = "Stop"'
                  '$out = "C:\WorkshopLink\Incoming\windows-updates.txt"'
                  'try {'
                  '  $service = Get-Service -Name wuauserv'
                  '  if ($service.StartType -eq "Disabled") { Set-Service -Name wuauserv -StartupType Manual }'
                  '  if ((Get-Service -Name wuauserv).Status -ne "Running") { Start-Service -Name wuauserv }'
                  '  $updateSession = New-Object -ComObject Microsoft.Update.Session'
                  '  $searcher = $updateSession.CreateUpdateSearcher()'
                  '  $searcher.Online = $true'
                  '  $found = $searcher.Search("IsInstalled=0 and IsHidden=0")'
                  '  $pending = @($found.Updates | ForEach-Object { [string]$_.Title } | Where-Object { $_ })'
                  '  $coll = New-Object -ComObject Microsoft.Update.UpdateColl'
                  '  foreach ($update in $found.Updates) {'
                  '    if (-not $update.EulaAccepted) { $update.AcceptEula() | Out-Null }'
                  '    [void]$coll.Add($update)'
                  '  }'
                  '  if ($coll.Count -eq 0) {'
                  '    "Windows Update reports nothing pending." | Set-Content -LiteralPath $out -Encoding UTF8'
                  '  } else {'
                  '    $downloader = $updateSession.CreateUpdateDownloader()'
                  '    $downloader.Updates = $coll'
                  '    $downloaded = $downloader.Download()'
                  '    $installer = $updateSession.CreateUpdateInstaller()'
                  '    $installer.Updates = $coll'
                  '    $installed = $installer.Install()'
                  '    $lines = @('
                  '      ("Installed " + $coll.Count + " update(s). Download=" + $downloaded.ResultCode + " Install=" + $installed.ResultCode + " Reboot=" + $installed.RebootRequired)'
                  '    ) + $pending'
                  '    $lines -join [Environment]::NewLine | Set-Content -LiteralPath $out -Encoding UTF8'
                  '  }'
                  '} catch {'
                  '  ("Windows Update could not be checked. " + $_.Exception.Message) | Set-Content -LiteralPath $out -Encoding UTF8'
                  '}'
                )
                $scriptLines -join "`r`n" | Set-Content -LiteralPath $runner -Encoding UTF8
                $task = 'WorkshopLink-Updates'
                Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
                $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
                $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
                Start-ScheduledTask -TaskName $task
                $deadline = (Get-Date).AddMinutes(20)
                do {
                  Start-Sleep -Seconds 2
                  if (Test-Path -LiteralPath $resultPath) { break }
                } while ((Get-Date) -lt $deadline)
                Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $runner -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Windows Update did not finish.' }
                $report = (Get-Content -LiteralPath $resultPath -Raw).Trim()
                Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
                $report
              }
              Write-Output $text
            }
            'restart' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $exe = Join-Path $env:SystemRoot 'System32\shutdown.exe'
                $output = & $exe /r /t 15 /f /c 'Workshop Link is restarting this PC.' 2>&1 | Out-String
                if ($LASTEXITCODE -ne 0) { throw ('Restart was not accepted. ' + $output.Trim()) }
                'Restart accepted. This PC restarts in 15 seconds.'
              }
              Write-Output $text
            }
            'shutdown' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $exe = Join-Path $env:SystemRoot 'System32\shutdown.exe'
                $output = & $exe /s /t 15 /f /c 'Workshop Link is shutting down this PC.' 2>&1 | Out-String
                if ($LASTEXITCODE -ne 0) { throw ('Shutdown was not accepted. ' + $output.Trim()) }
                'Shutdown accepted. This PC turns off in 15 seconds.'
              }
              Write-Output $text
            }
            'signout' {
              $text = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $explorer = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
                if (-not $explorer) { throw 'Nobody is signed in at the screen.' }
                $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
                $proc = Get-Process -Id $explorer.ProcessId
                & "$env:SystemRoot\System32\logoff.exe" $proc.SessionId
                if ($LASTEXITCODE -ne 0) { throw 'Windows did not sign that person out.' }
                $who = @($owner.Domain, $owner.User) -join '\'
                "Signed out $who."
              }
              Write-Output $text
            }
            'agentupdate' {
              $folder = 'C:\WorkshopLink\Incoming'
              New-Item -ItemType Directory -Force -Path $folder | Out-Null
              $setup = Join-Path $folder 'WorkshopAgentSetup.exe'
              $headers = @{ 'User-Agent' = 'WorkshopLink'; Accept = 'application/vnd.github+json' }
              try {
                $release = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/supportletaba2-gif/WorkshopAgent/releases/latest'
              } catch {
                throw 'The public WorkshopAgent repository has no release yet. Publish WorkshopAgentSetup.exe there, then try again.'
              }
              $asset = @($release.assets) | Where-Object { $_.name -eq 'WorkshopAgentSetup.exe' } | Select-Object -First 1
              if (-not $asset) { throw 'The latest GitHub release does not include WorkshopAgentSetup.exe.' }
              $url = [string]$asset.browser_download_url
              if ($url -notmatch '^https://(github\.com|objects\.githubusercontent\.com|release-assets\.githubusercontent\.com)/') { throw 'The download address was not GitHub.' }
              Invoke-WebRequest -Headers $headers -Uri $url -OutFile $setup
              $task = 'WorkshopLinkAgentUpdate'
              & schtasks.exe /Delete /TN $task /F | Out-Null
              & schtasks.exe /Create /TN $task /TR "`"$setup`" /quiet /norestart" /SC ONCE /ST 23:59 /RL HIGHEST /RU SYSTEM /F | Out-Null
              if ($LASTEXITCODE -ne 0) { throw 'The update could not be started.' }
              & schtasks.exe /Run /TN $task | Out-Null
              'UPDATE installing. The agent already on this PC is replaced. The password and place stay. Nobody at the PC has to press anything.'
            }
            'command' {
              $command = $env:WLINK_CMD
              if ([string]::IsNullOrWhiteSpace($command)) { throw 'There is no command to run.' }
              if ($command.Length -gt 16000) { throw 'That command is too long.' }
              $text = Invoke-Command -Session $session -ScriptBlock {
                param($command)
                $output = Invoke-Expression $command | Out-String
                if ([string]::IsNullOrWhiteSpace($output)) { 'Command finished with no output.' } else { $output }
              } -ArgumentList $command
              Write-Output $text
            }
            default { throw 'Unknown action.' }
          }
        } finally { }
        """;
}
