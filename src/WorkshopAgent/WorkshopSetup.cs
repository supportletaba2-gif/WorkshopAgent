using System.Diagnostics;
using System.Text;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal static class WorkshopSetup
{
    public static Task InstallAsync(string password, string place, string? group, IProgress<string> progress, CancellationToken cancellationToken) =>
        RunAsync(InstallScript, password, place, group, progress, cancellationToken);

    public static Task RemoveAsync(IProgress<string> progress, CancellationToken cancellationToken) =>
        RunAsync(RemoveScript, password: null, place: null, group: null, progress, cancellationToken);

    private static async Task RunAsync(
        string script,
        string? password,
        string? place,
        string? group,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
            var powershell = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -Command -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.Environment["WLINK_EXE"] = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(password))
                start.Environment["WLINK_PASS"] = password;
            if (!string.IsNullOrEmpty(place))
                start.Environment["WLINK_PLACE"] = place;
            if (AgentPlace.IsGroupKey(group))
                start.Environment["WLINK_GROUP"] = group!.Trim().ToUpperInvariant();

            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Windows PowerShell did not start.");
            await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            start.Environment.Remove("WLINK_PASS");

            var readOutput = PumpAsync(process.StandardOutput, progress, cancellationToken);
            var readError = PumpAsync(process.StandardError, progress, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            await Task.WhenAll(readOutput, readError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Setup stopped before it finished. The log above has the reason.");
    }

    private static async Task PumpAsync(StreamReader reader, IProgress<string> progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                return;
            if (line.Length > 0)
                progress.Report(line);
        }
    }

    private const string InstallScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'

        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $account = ($identity -split '\\')[-1]
        Write-Output 'STEP 1/6 Checking the signed-in account'
        if ([string]::IsNullOrWhiteSpace($account) -or $account -eq 'SYSTEM') {
          throw 'Install Workshop Link while signed in as the person who will use this PC.'
        }

        $adminGroup = Get-LocalGroup | Where-Object { $_.SID.Value -eq 'S-1-5-32-544' } | Select-Object -First 1
        if (-not $adminGroup) { throw 'The local Administrators group was not found.' }
        $adminName = $adminGroup.Name
        $members = @(Get-LocalGroupMember -Group $adminName -ErrorAction SilentlyContinue)
        $alreadyAdmin = $null -ne ($members | Where-Object { $_.Name -eq $identity -or $_.Name -like "*\$account" })
        if (-not $alreadyAdmin) {
          throw "The account $account is not a local administrator. Sign in with an administrator account and run Install again."
        }
        Write-Output 'STEP 2/6 Storing the command center password'
        $pass = $env:WLINK_PASS
        Remove-Item Env:\WLINK_PASS -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($pass) -or $pass.Length -lt 12) {
          throw 'A password of at least 12 characters is required.'
        }
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $salt = New-Object byte[] 16
        $rng.GetBytes($salt)
        $derive = New-Object Security.Cryptography.Rfc2898DeriveBytes($pass, $salt, 100000, [Security.Cryptography.HashAlgorithmName]::SHA256)
        $hashBytes = $derive.GetBytes(32)
        $keyBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($pass))
        $pass = $null
        $stateDir = 'C:\ProgramData\WorkshopLink'
        New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
        $hashLine = '100000:' + [Convert]::ToBase64String($salt) + ':' + [Convert]::ToBase64String($hashBytes)
        $hashPath = Join-Path $stateDir 'link.hash'
        Set-Content -LiteralPath $hashPath -Value $hashLine -Encoding ascii -NoNewline
        & icacls.exe $hashPath /inheritance:r /grant:r '*S-1-5-18:(R,W)' '*S-1-5-32-544:(R,W)' | Out-Null
        $keyPath = Join-Path $stateDir 'link.key'
        [System.IO.File]::WriteAllBytes($keyPath, $keyBytes)
        & icacls.exe $keyPath /inheritance:r /grant:r '*S-1-5-18:(R,W)' '*S-1-5-32-544:(R,W)' | Out-Null
        Set-Content -LiteralPath (Join-Path $stateDir 'account.txt') -Value $account -Encoding ascii
        function Test-WorkshopLocal([string]$ip) {
          if ([string]::IsNullOrWhiteSpace($ip)) { return $false }
          $parsed = $null
          if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsed)) { return $false }
          $bytes = $parsed.GetAddressBytes()
          if ($bytes.Length -ne 4) { return $false }
          if ($bytes[0] -eq 10) { return $true }
          if ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) { return $true }
          return ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
        }
        $place = $env:WLINK_PLACE
        Remove-Item Env:\WLINK_PLACE -ErrorAction SilentlyContinue
        if ($place -ne 'offsite') { $place = 'lan' }
        $localNow = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { Test-WorkshopLocal $_.IPAddress })
        if ($place -ne 'offsite' -and $localNow.Count -eq 0) {
          $place = 'offsite'
          Write-Output 'This PC has no local network address, so it will register through Cloudflare.'
        }
        Set-Content -LiteralPath (Join-Path $stateDir 'placement.txt') -Value $place -Encoding ascii -NoNewline
        $group = $env:WLINK_GROUP
        Remove-Item Env:\WLINK_GROUP -ErrorAction SilentlyContinue
        if ($group -match '^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$') {
          Set-Content -LiteralPath (Join-Path $stateDir 'group.txt') -Value $group.ToUpper() -Encoding ascii -NoNewline
          Write-Output "This PC will join group $($group.ToUpper())."
        } else {
          Write-Output 'No group key. This PC is not placed in a group.'
        }
        Write-Output "Stored the command center password for $account. The Windows password on that account was not changed."

        $leftover = Get-LocalUser -Name 'WorkshopLink' -ErrorAction SilentlyContinue
        if ($leftover) {
          Remove-LocalUser -Name 'WorkshopLink'
          Write-Output 'Removed the leftover WorkshopLink Windows account.'
        }

        function Add-IfMissing($groupSid, $label) {
          $group = Get-LocalGroup | Where-Object { $_.SID.Value -eq $groupSid } | Select-Object -First 1
          if (-not $group) { return }
          $groupMembers = @(Get-LocalGroupMember -Group $group.Name -ErrorAction SilentlyContinue)
          $present = $null -ne ($groupMembers | Where-Object { $_.Name -eq $identity -or $_.Name -like "*\$account" })
          if (-not $present) {
            Add-LocalGroupMember -Group $group.Name -Member $identity
            Write-Output "Added $account to $label."
          } else {
            Write-Output "$account is already in $label."
          }
        }
        Add-IfMissing 'S-1-5-32-544' 'Administrators'
        Add-IfMissing 'S-1-5-32-580' 'Remote Management Users'
        Add-IfMissing 'S-1-5-32-555' 'Remote Desktop Users'

        Write-Output "Using $account for Windows rights. The command center password is not a Windows logon."

        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'LocalAccountTokenFilterPolicy' -ErrorAction SilentlyContinue
        $oldWinrm = Get-NetFirewallRule -Name 'WorkshopLink-WinRM' -ErrorAction SilentlyContinue
        if ($oldWinrm) {
          try { Remove-NetFirewallRule -Name 'WorkshopLink-WinRM' -ErrorAction Stop } catch { Disable-NetFirewallRule -Name 'WorkshopLink-WinRM' -ErrorAction SilentlyContinue }
          Write-Output 'Removed the old Windows remote administration rule.'
        }
        try {
          Disable-PSRemoting -Force -ErrorAction Stop | Out-Null
          Write-Output 'Windows remote administration is not used for the command center.'
        } catch {
          Write-Output 'Windows remote administration was already off or could not be changed.'
        }

        Write-Output 'STEP 3/6 Opening the workshop firewall'
        $lanRemote = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')
        $linkRule = Get-NetFirewallRule -Name 'WorkshopLink-Link' -ErrorAction SilentlyContinue
        if (-not $linkRule) {
          New-NetFirewallRule -Name 'WorkshopLink-Link' -DisplayName 'Workshop Link (command center)' -Description 'Workshop Link command center. Local networks only.' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47633 -RemoteAddress $lanRemote -Profile Any | Out-Null
        } else {
          Set-NetFirewallRule -Name 'WorkshopLink-Link' -RemoteAddress $lanRemote -Enabled True -Profile Any -Action Allow
        }
        Write-Output 'Allowed TCP 47633 from the local network.'

        $edition = [string](Get-CimInstance Win32_OperatingSystem).Caption
        if ($edition -match 'Home') {
          Write-Output 'This PC is Windows Home. Remote Desktop stays off.'
        } else {
          $rdpPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
          $rdpBlocked = $false
          if (Test-Path $rdpPolicy) {
            $rdpBlocked = (Get-ItemProperty -Path $rdpPolicy -Name 'fDenyTSConnections' -ErrorAction SilentlyContinue).fDenyTSConnections -eq 1
          }
          if ($rdpBlocked) {
            Write-Output 'A Windows policy on this PC is blocking Remote Desktop.'
          } else {
            Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Type DWord -Value 0
            Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'fEnableWinStation' -Type DWord -Value 1
            Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'UserAuthentication' -Type DWord -Value 1
            Set-Service -Name 'TermService' -StartupType Automatic
            $terminal = Get-CimInstance -Namespace 'root/CIMV2/TerminalServices' -ClassName 'Win32_TerminalServiceSetting'
            $allowed = Invoke-CimMethod -InputObject $terminal -MethodName 'SetAllowTSConnections' -Arguments @{ AllowTSConnections = 1; ModifyFirewallException = 1 }
            if ($allowed.ReturnValue -ne 0) { throw "Windows did not turn on Remote Desktop. Code $($allowed.ReturnValue)." }
            Start-Service -Name 'TermService'
            $rdpGroup = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue)
            if ($rdpGroup.Count -eq 0) {
              $rdpGroup = @(Get-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue)
            }
            foreach ($rdpRule in $rdpGroup) {
              try {
                Set-NetFirewallRule -Name $rdpRule.Name -Enabled True -Profile Any -Action Allow -RemoteAddress $lanRemote | Out-Null
              } catch {
                Write-Output "Could not limit Remote Desktop rule '$($rdpRule.Name)'. $($_.Exception.Message)"
              }
            }
            $rdpAllow = Get-NetFirewallRule -Name 'WorkshopLink-RDP' -ErrorAction SilentlyContinue
            if (-not $rdpAllow) {
              New-NetFirewallRule -Name 'WorkshopLink-RDP' -DisplayName 'Workshop Link (Remote Desktop)' -Description 'Workshop Link Remote Desktop. Workshop LAN only.' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3389 -RemoteAddress $lanRemote -Profile Any | Out-Null
            } else {
              Set-NetFirewallRule -Name 'WorkshopLink-RDP' -Enabled True -Profile Any -Action Allow -RemoteAddress $lanRemote -LocalPort 3389
            }
            $rdpUsers = Get-LocalGroup | Where-Object { $_.SID.Value -eq 'S-1-5-32-555' } | Select-Object -First 1
            if ($rdpUsers) {
              $rdpMembers = @(Get-LocalGroupMember -Group $rdpUsers.Name -ErrorAction SilentlyContinue)
              $alreadyRdp = $null -ne ($rdpMembers | Where-Object { $_.Name -eq $identity -or $_.Name -like "*\$account" })
              if (-not $alreadyRdp) {
                Add-LocalGroupMember -Group $rdpUsers.Name -Member $identity
              }
            }
            Write-Output 'Remote Desktop is on for the workshop LAN.'
          }
        }

        Write-Output 'STEP 4/6 Copying the agent'
        $source = $env:WLINK_EXE
        if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source)) {
          throw 'The agent program file was not found.'
        }
        $destDir = 'C:\Program Files\Workshop Command Center Agent'
        $dest = Join-Path $destDir 'WorkshopAgent.exe'
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null

        foreach ($oldName in @('WorkshopLink','WorkshopCommandCenterAgent')) {
          $running = Get-Service | Where-Object { $_.Name -eq $oldName } | Select-Object -First 1
          if ($running -and $running.Status -ne 'Stopped') {
            Stop-Service -Name $oldName -Force -ErrorAction SilentlyContinue
            try { $running.WaitForStatus('Stopped', [timespan]::FromSeconds(25)) } catch { }
          }
          if ($oldName -eq 'WorkshopLink' -and $running) { & sc.exe delete WorkshopLink | Out-Null }
        }

        $sourceFull = [System.IO.Path]::GetFullPath($source)
        $destFull = [System.IO.Path]::GetFullPath($dest)
        if ($sourceFull -ne $destFull) {
          Copy-Item -LiteralPath $sourceFull -Destination $destFull -Force
          Write-Output "Copied the agent to $destFull."
        }

        function Set-AnnounceRule([string]$name, [string]$display, [string]$direction, [string]$program) {
          try {
            $rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
            if ($rule) { Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue }
            New-NetFirewallRule -Name $name -DisplayName $display -Direction $direction -Action Allow -Program $program -Protocol UDP -RemoteAddress '10.0.0.0/8','172.16.0.0/12','192.168.0.0/16' -Profile Any -ErrorAction Stop | Out-Null
          } catch {
            Write-Output ("Could not update firewall rule $name. " + $_.Exception.Message)
          }
        }
        Set-AnnounceRule 'WorkshopLink-Announce' 'Workshop Link (announce)' 'Inbound' $destFull
        Set-AnnounceRule 'WorkshopLink-Announce-Out' 'Workshop Link (announce out)' 'Outbound' $destFull
        function Set-CloudRule([string]$name, [string]$display, [string]$protocol, [string[]]$ports) {
          try {
            $rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
            if ($rule) { Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue }
            New-NetFirewallRule -Name $name -DisplayName $display -Description 'Outbound Cloudflare registration and screen relay for Workshop Link. TCP 443 matches what a normal firewall already allows.' -Direction Outbound -Action Allow -Program $destFull -Protocol $protocol -RemotePort $ports -Profile Any -ErrorAction Stop | Out-Null
          } catch {
            Write-Output ("Could not update firewall rule $name. " + $_.Exception.Message)
          }
        }
        Set-CloudRule 'WorkshopLink-Cloud' 'Workshop Link (Cloudflare)' 'TCP' @('80','443','5349')
        Set-CloudRule 'WorkshopLink-Cloud-Udp' 'Workshop Link (Cloudflare UDP)' 'UDP' @('53','123','443','3478','5349')
        Write-Output 'Allowed outbound Cloudflare registration on TCP 443.'
        try {
          $products = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | Select-Object -ExpandProperty displayName)
          if ($products.Count -gt 0) {
            Write-Output ('Security products on this PC: ' + ($products -join ', ') + '. If the command center does not list this PC, allow Workshop Link Agent in that product. Leave the product on.')
          }
        } catch { }
        Write-Output 'STEP 5/6 Creating and starting the service'
        $binary = '"' + $destFull + '" --service'
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='WorkshopCommandCenterAgent'" -ErrorAction SilentlyContinue
        if ($service) {
          & sc.exe delete WorkshopCommandCenterAgent | Out-Null
          Start-Sleep -Seconds 2
        }

        $created = $false
        for ($try = 1; $try -le 5 -and -not $created; $try++) {
          try {
            New-Service -Name 'WorkshopCommandCenterAgent' -BinaryPathName $binary -DisplayName 'Workshop Link Agent' -StartupType Automatic | Out-Null
            $created = $true
          } catch {
            if ($try -eq 5) { throw }
            Start-Sleep -Seconds 2
          }
        }
        & sc.exe config WorkshopCommandCenterAgent obj= LocalSystem | Out-Null
        & sc.exe description WorkshopCommandCenterAgent "Announces this PC and accepts command center actions after the installer password." | Out-Null
        & sc.exe failure WorkshopCommandCenterAgent reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
        # The first start unpacks the program and can take longer than one attempt.
        $started = $false
        for ($try = 1; $try -le 4 -and -not $started; $try++) {
          try {
            Start-Service -Name 'WorkshopCommandCenterAgent' -ErrorAction Stop
            $started = $true
          } catch {
            Start-Sleep -Seconds 5
            $again = Get-Service | Where-Object { $_.Name -eq 'WorkshopCommandCenterAgent' } | Select-Object -First 1
            if ($again -and $again.Status -eq 'Running') { $started = $true }
            elseif ($try -eq 4) { throw }
          }
        }
        $lanAddress = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { Test-WorkshopLocal $_.IPAddress })
        if ($place -eq 'offsite') {
          Write-Output 'This PC registers with the command center through Cloudflare. Outbound TCP 443 is enough.'
        } elseif ($lanAddress.Count -eq 0) {
          Write-Output 'This PC has no local network address yet. The command center will see it when it is on the same network.'
        }
        Write-Output 'Started the Workshop Link Agent service.'
        $shell = New-Object -ComObject WScript.Shell
        foreach ($desktop in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
          if ([string]::IsNullOrWhiteSpace($desktop)) { continue }
          try {
            $link = $shell.CreateShortcut((Join-Path $desktop 'Workshop Link Agent.lnk'))
            $link.TargetPath = $destFull
            $link.WorkingDirectory = $destDir
            $link.WindowStyle = 1
            $link.Arguments = '--status'
            $link.Description = 'Workshop Link remote maintenance'
            $link.Save()
          } catch { }
        }
        Write-Output 'Placed the Workshop Link Agent shortcut on the desktop.'
        Write-Output 'STEP 6/6 Install finished'
        Write-Output 'RESULT: installed'
        """;

    private const string RemoveScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'

        Write-Output 'STEP 1/5 Stopping the Workshop Link service'
        foreach ($serviceName in @('WorkshopCommandCenterAgent','WorkshopLink')) {
          $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
          if (-not $service) { continue }
          & sc.exe stop $serviceName | Out-Null
          $deadline = (Get-Date).AddSeconds(25)
          do {
            Start-Sleep -Milliseconds 500
            $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
          } while ($service -and $service.State -ne 'Stopped' -and (Get-Date) -lt $deadline)
          if ($service -and $service.ProcessId -gt 0) {
            Stop-Process -Id $service.ProcessId -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
          }
          & sc.exe delete $serviceName | Out-Null
          Start-Sleep -Seconds 2
        }
        Write-Output 'STEP 2/5 Deleting the Workshop Link service'
        Get-CimInstance Win32_Process -Filter "Name='WorkshopAgent.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.CommandLine -match '--service|--screen|--job|--sas|--rdp-sas' } |
          ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        foreach ($serviceName in @('WorkshopCommandCenterAgent','WorkshopLink')) {
          & sc.exe stop $serviceName | Out-Null
          Start-Sleep -Seconds 1
          & sc.exe delete $serviceName | Out-Null
        }
        if (Get-Service -Name 'WorkshopCommandCenterAgent' -ErrorAction SilentlyContinue) {
          Write-Output 'The service is still listed. Windows will drop it after the program file is removed.'
        } else {
          Write-Output 'Removed the Workshop Link Agent service.'
        }
        Get-CimInstance Win32_Process -Filter "Name='WorkshopAgent.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.CommandLine -match '--service|--screen|--job|--sas|--rdp-sas' } |
          ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Write-Output 'Stopped leftover Workshop Link processes.'

        $wg = Join-Path $env:ProgramFiles 'WireGuard\wireguard.exe'
        if (Test-Path -LiteralPath $wg) {
          & $wg /uninstalltunnelservice 'wlink-bootstrap' | Out-Null
          $tunnelDir = Join-Path $env:ProgramData 'WorkshopLink\tunnels'
          if (Test-Path -LiteralPath $tunnelDir) {
            Get-ChildItem -LiteralPath $tunnelDir -Filter '*.conf' -ErrorAction SilentlyContinue | ForEach-Object {
              & $wg /uninstalltunnelservice $_.BaseName | Out-Null
            }
            Remove-Item -LiteralPath $tunnelDir -Recurse -Force -ErrorAction SilentlyContinue
          }
          Write-Output 'Removed the workshop WireGuard tunnels.'
        }

        Write-Output 'STEP 3/5 Removing firewall rules and shortcuts'
        $stateDir = 'C:\ProgramData\WorkshopLink'
        if (Test-Path -LiteralPath $stateDir) {
          Remove-Item -LiteralPath $stateDir -Recurse -Force -ErrorAction SilentlyContinue
          Write-Output 'Removed the Workshop Link configuration.'
        }
        Unregister-ScheduledTask -TaskName 'WorkshopLink-Updates' -Confirm:$false -ErrorAction SilentlyContinue

        try {
          if (Get-Command Remove-MpPreference -ErrorAction SilentlyContinue) {
            Remove-MpPreference -ExclusionPath @('C:\Program Files\WorkshopLink','C:\ProgramData\WorkshopLink') -ErrorAction SilentlyContinue
            Remove-MpPreference -ExclusionProcess 'WorkshopAgent.exe' -ErrorAction SilentlyContinue
          }
        } catch { }
        foreach ($ruleName in @('WorkshopLink-WinRM', 'WorkshopLink-Link', 'WorkshopLink-RDP', 'WorkshopLink-Announce', 'WorkshopLink-Announce-Out', 'WorkshopLink-Announce-Setup', 'WorkshopLink-Cloud', 'WorkshopLink-Cloud-Udp')) {
          $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
          if (-not $rule) { continue }
          try {
            Remove-NetFirewallRule -Name $ruleName -ErrorAction Stop
            Write-Output "Removed firewall rule $ruleName."
          } catch {
            & netsh.exe advfirewall firewall delete rule name="$ruleName" | Out-Null
            $still = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
            if (-not $still) {
              Write-Output "Removed firewall rule $ruleName."
            } else {
              try {
                Disable-NetFirewallRule -Name $ruleName -ErrorAction Stop
                Write-Output "Could not delete firewall rule $ruleName, so it was turned off."
              } catch {
                Write-Output "Could not remove firewall rule $ruleName. Windows denied it, and removal continued."
              }
            }
          }
        }

        foreach ($desktop in @([Environment]::GetFolderPath('Desktop'), 'C:\Users\Public\Desktop')) {
          if ([string]::IsNullOrWhiteSpace($desktop)) { continue }
          $shortcut = Join-Path $desktop 'Workshop Link Agent.lnk'
          if (Test-Path -LiteralPath $shortcut) {
            Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
            Write-Output 'Removed the desktop shortcut.'
          }
        }

        try {
          Disable-PSRemoting -Force | Out-Null
          Write-Output 'Turned off Windows remote administration.'
        } catch {
          Write-Output ('Remote administration was already off or could not be changed. ' + $_.Exception.Message)
        }

        try {
          Set-Item -Path 'WSMan:\localhost\Service\IPv4Filter' -Value '*'
          Set-Item -Path 'WSMan:\localhost\Service\IPv6Filter' -Value '*'
        } catch { }

        try {
          Set-Service -Name 'WinRM' -StartupType Manual
          Stop-Service -Name 'WinRM' -Force -ErrorAction SilentlyContinue
          Write-Output 'Set the WinRM service back to manual.'
        } catch {
          Write-Output ('The WinRM service was left as it is. ' + $_.Exception.Message)
        }

        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'LocalAccountTokenFilterPolicy' -ErrorAction SilentlyContinue
        Write-Output 'Restored the default remote administrator restriction.'

        $account = Get-LocalUser -Name 'WorkshopLink' -ErrorAction SilentlyContinue
        if ($account) {
          Remove-LocalUser -Name 'WorkshopLink'
          Write-Output 'Removed the WorkshopLink account.'
        }

        Write-Output 'STEP 4/5 Deleting the program files'
        if (Get-Service -Name 'WorkshopCommandCenterAgent' -ErrorAction SilentlyContinue) {
          Write-Output 'The service is still listed. The program files will be removed where Windows allows it.'
        }
        $dest = 'C:\Program Files\Workshop Command Center Agent\WorkshopAgent.exe'
        if (Test-Path -LiteralPath $dest) {
          $same = $false
          if ($env:WLINK_EXE) {
            $same = ([System.IO.Path]::GetFullPath($env:WLINK_EXE) -eq [System.IO.Path]::GetFullPath($dest))
          }
          $removed = $false
          if (-not $same) {
            try {
              Remove-Item -LiteralPath $dest -Force -ErrorAction Stop
              $removed = $true
              Write-Output 'Removed the installed program file.'
            } catch { }
          }
          if (-not $removed) {
            $later = 'C:\ProgramData\WorkshopLink-remove.cmd'
            Set-Content -LiteralPath $later -Value "@echo off`r`ntimeout /t 4 /nobreak >nul`r`ndel /f /q `"$dest`"`r`nrd /s /q `"C:\Program Files\Workshop Command Center Agent`" 2>nul`r`nrd /s /q `"C:\Program Files\WorkshopLink`" 2>nul`r`ndel /f /q `"%~f0`"" -Encoding Ascii
            Start-Process -FilePath $later -WindowStyle Hidden
            Write-Output 'The program file is in use. It will be deleted after this window closes.'
          }
        }
        $oldDir = 'C:\Program Files\WorkshopLink'
        if (Test-Path -LiteralPath $oldDir) {
          try {
            Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction Stop
            Write-Output 'Removed the previous Workshop Link folder.'
          } catch {
            Write-Output 'The previous Workshop Link folder is still in use, so it was left in place.'
          }
        }

        Write-Output 'STEP 5/5 Uninstall finished'
        Write-Output 'RESULT: removed'
        """;
}
