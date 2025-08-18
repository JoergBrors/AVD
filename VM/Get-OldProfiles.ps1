<#
Ermittelt alte, ungenutzte lokale Windows-Profile und mappt sie gegen AD (ohne RSAT).
NEU: Liest zu jedem Profil die Registry-Werte (Load/Unload-Filetime High/Low, Flags, State, FullProfile, Guid, ProfileImagePath)
      und rechnet die FILETIME-Paare in Datum/Uhrzeit um.

Ausgabe:
- $profileReport : vollständiger Report
- $oldProfiles   : Kandidaten > -Days (optional: nur disabled)

Parameterbeispiele:
  .\Get-OldProfiles.ps1 -Days 360 -OnlyDisabled -ShowCandidatesOnly -ExportCsv -Verbose
#>

[CmdletBinding()]
param(
    [int]$Days = 360,
    [switch]$OnlyDisabled,
    [switch]$ShowCandidatesOnly,
    [switch]$ExportCsv,
    [string]$CsvBasePath = ".\ProfileLogonReport",
    [string[]]$ExcludePathPatterns = @(
        '\\(Default|Public|Administrator)(\.|\\|$)',
        '^C:\\Users\\(Default|Public)(\\|$)'
    )
)

Begin {
    Write-Verbose "Start – Threshold: $Days Tage; OnlyDisabled=$OnlyDisabled"

    # --- Domain reachability (no RSAT required) ---
    try { $null = [System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain() }
    catch { Write-Error "Keine Domänenbindung/DC erreichbar: $($_.Exception.Message)"; return }

    # --- Helpers ------------------------------------------------------

    function Convert-ADFileTime {
        param([object]$Value)
        if (-not $Value) { return $null }
        try {
            if ($Value -is [long] -or $Value -is [int64]) { if ($Value -le 0) { return $null }; return [DateTime]::FromFileTime([int64]$Value) }
            if ($Value.GetType().FullName -eq 'System.__ComObject') {
                $hi = $Value.GetType().InvokeMember('HighPart','GetProperty',$null,$Value,$null)
                $lo = $Value.GetType().InvokeMember('LowPart','GetProperty',$null,$Value,$null)
                $ft = ([int64]$hi -shl 32) -bor ([uint32]$lo)
                if ($ft -le 0) { return $null }; return [DateTime]::FromFileTime($ft)
            }
            $num = [int64]$Value; if ($num -le 0) { return $null }; return [DateTime]::FromFileTime($num)
        } catch { return $null }
    }

    function Convert-FileTimeParts {
        <# Wandelt REG_DWORD High/Low (UINT32) in DateTime um. Gibt $null zurück, wenn beides 0. #>
        param(
            [Nullable[UInt32]]$High,
            [Nullable[UInt32]]$Low
        )
        if (-not $High -and -not $Low) { return $null }
        try {
            $ft = ([int64]$High -shl 32) -bor ([uint32]$Low)
            if ($ft -le 0) { return $null }
            return [DateTime]::FromFileTime($ft)
        } catch { return $null }
    }

    function Convert-SidToLdapHexString {
        param([Parameter(Mandatory)][string]$SidString)
        $sid = [System.Security.Principal.SecurityIdentifier]$SidString
        $b = New-Object byte[] ($sid.BinaryLength)
        $sid.GetBinaryForm($b,0)
        ($b | ForEach-Object { '\' + $_.ToString('X2') }) -join ''
    }

    function Get-DefaultNamingContext {
        param([Parameter(Mandatory)][string]$Dc)
        (New-Object System.DirectoryServices.DirectoryEntry("LDAP://$Dc/RootDSE")).Properties['defaultNamingContext'][0]
    }

    function Find-UserBySidOnDc {
        <# Sucht Benutzer per objectSid auf einem DC; liest UAC/Enabled + lastLogonTimestamp. #>
        param([Parameter(Mandatory)][string]$SidString,[Parameter(Mandatory)][string]$Dc)
        try {
            $baseDN = Get-DefaultNamingContext -Dc $Dc
            $hexSid = Convert-SidToLdapHexString -SidString $SidString
            $de = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$Dc/$baseDN")
            $ds = New-Object System.DirectoryServices.DirectorySearcher($de)
            $ds.Filter = "(&(objectSid=$hexSid)(objectClass=user))"
            $ds.PageSize = 1000
            foreach($p in 'distinguishedName','sAMAccountName','userAccountControl','lastLogonTimestamp','msDS-UserAccountDisabled'){ $null=$ds.PropertiesToLoad.Add($p) }
            $res = $ds.FindOne(); if (-not $res) { return $null }

            $dn  = $res.Properties['distinguishedname'][0]
            $sam = $res.Properties['samaccountname'][0]
            $uac = $null; if ($res.Properties['useraccountcontrol']) { $uac = [int]$res.Properties['useraccountcontrol'][0] }
            $enabled = $null
            if ($uac -ne $null) { $enabled = -not (($uac -band 0x0002) -ne 0) }
            elseif ($res.Properties['msds-useraccountdisabled']) { $enabled = -not [bool]$res.Properties['msds-useraccountdisabled'][0] }
            $llt = $null; if ($res.Properties['lastlogontimestamp']) { $llt = Convert-ADFileTime $res.Properties['lastlogontimestamp'][0] }

            [pscustomobject]@{
                DC                 = $Dc
                DistinguishedName  = $dn
                SamAccountName     = $sam
                UserAccountControl = $uac
                AD_Enabled         = $enabled
                LastLogonTimestamp = $llt
            }
        } catch { Write-Verbose "Find-UserBySidOnDc($Dc): $($_.Exception.Message)"; return $null }
    }

    function Read-UserLastLogonOnDc {
        <# Liest nicht repliziertes lastLogon auf einem DC für DN. #>
        param([string]$Dc,[string]$DistinguishedName)
        try {
            $de = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$Dc/$DistinguishedName")
            $ds = New-Object System.DirectoryServices.DirectorySearcher($de)
            $ds.Filter = "(distinguishedName=$DistinguishedName)"
            $ds.PageSize = 1
            $null = $ds.PropertiesToLoad.Add('lastLogon')
            $res = $ds.FindOne()
            if ($res -and $res.Properties['lastlogon']) { return Convert-ADFileTime $res.Properties['lastlogon'][0] }
            return $null
        } catch { Write-Verbose "Read-UserLastLogonOnDc($Dc): $($_.Exception.Message)"; return $null }
    }

    function Get-AdInfoBySidNet {
        <# Findet User (DN, sAM, Enabled, lastLogonTimestamp) und maximiert lastLogon über alle DCs. #>
        param([Parameter(Mandatory)][string]$Sid)
        $domain = [System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain()
        $dcs = $domain.DomainControllers | ForEach-Object { $_.Name }

        $found = $null
        foreach ($dc in $dcs) { $found = Find-UserBySidOnDc -SidString $Sid -Dc $dc; if ($found) { break } }
        if (-not $found) {
            return [pscustomobject]@{
                Found=$false; SamAccountName=$null; DistinguishedName=$null; LastLogonMax=$null; LastLogonTimestamp=$null; AD_Enabled=$null; UserAccountControl=$null
            }
        }

        $maxLL = $null
        foreach ($dc in $dcs) {
            $ll = Read-UserLastLogonOnDc -Dc $dc -DistinguishedName $found.DistinguishedName
            if ($ll -and (-not $maxLL -or $ll -gt $maxLL)) { $maxLL = $ll }
        }

        [pscustomobject]@{
            Found                = $true
            SamAccountName       = $found.SamAccountName
            DistinguishedName    = $found.DistinguishedName
            LastLogonMax         = $maxLL
            LastLogonTimestamp   = $found.LastLogonTimestamp
            AD_Enabled           = $found.AD_Enabled
            UserAccountControl   = $found.UserAccountControl
        }
    }

    function Read-ProfileListRegistry {
        <#
          Liest die Registry-Werte unter HKLM:\SOFTWARE\...\ProfileList\<SID> und konvertiert
          LocalProfileLoadTime/UnloadTime (High/Low) -> DateTime.
        #>
        param([Parameter(Mandatory)][string]$Sid)
        $keyPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
        if (-not (Test-Path $keyPath)) {
            return [pscustomobject]@{
                Reg_ProfileKeyPresent    = $false
                Reg_ProfileImagePath     = $null
                Reg_Guid                 = $null
                Reg_Flags                = $null
                Reg_State                = $null
                Reg_FullProfile          = $null
                Reg_LastLoadTime         = $null
                Reg_LastUnloadTime       = $null
            }
        }

        try {
            $p = Get-ItemProperty -Path $keyPath
            $lastLoad   = Convert-FileTimeParts -High $p.LocalProfileLoadTimeHigh   -Low $p.LocalProfileLoadTimeLow
            $lastUnload = Convert-FileTimeParts -High $p.LocalProfileUnloadTimeHigh -Low $p.LocalProfileUnloadTimeLow

            [pscustomobject]@{
                Reg_ProfileKeyPresent    = $true
                Reg_ProfileImagePath     = $p.ProfileImagePath
                Reg_Guid                 = $p.Guid
                Reg_Flags                = $p.Flags
                Reg_State                = $p.State
                Reg_FullProfile          = $p.FullProfile
                Reg_LastLoadTime         = $lastLoad
                Reg_LastUnloadTime       = $lastUnload
            }
        } catch {
            Write-Verbose "Read-ProfileListRegistry($Sid): $($_.Exception.Message)"
            [pscustomobject]@{
                Reg_ProfileKeyPresent    = $false
                Reg_ProfileImagePath     = $null
                Reg_Guid                 = $null
                Reg_Flags                = $null
                Reg_State                = $null
                Reg_FullProfile          = $null
                Reg_LastLoadTime         = $null
                Reg_LastUnloadTime       = $null
            }
        }
    }
}

Process {
    $limit = (Get-Date).AddDays(-$Days)

    # 1) Lokale Profile
    Write-Verbose "Sammle lokale Profile…"
    $profiles = Get-CimInstance Win32_UserProfile -ErrorAction Stop |
        Where-Object {
            -not $_.Special -and
            -not $_.Loaded  -and
            $_.LocalPath -like 'C:\Users\*'
        } |
        Where-Object {
            $path = $_.LocalPath
            -not ($ExcludePathPatterns | ForEach-Object { $path -match $_ } | Where-Object { $_ })
        }

    Write-Verbose ("Gefundene Profile: {0}" -f $profiles.Count)

    # 2) Report bauen (AD + Registry)
    $profileReport = foreach ($p in $profiles) {
        try {
            $sid = $p.SID
            $ad  = Get-AdInfoBySidNet -Sid $sid
            $reg = Read-ProfileListRegistry -Sid $sid

            # maßgebliches Datum (Anmeldung)
            $effective = if ($ad.LastLogonMax) { $ad.LastLogonMax } else { $ad.LastLogonTimestamp }
            $daysSince = if ($effective) { (New-TimeSpan -Start $effective -End (Get-Date)).Days } else { $null }

            [pscustomobject]@{
                # Basis
                LocalPath               = $p.LocalPath
                UserName                = (Split-Path $p.LocalPath -Leaf)
                SID                     = $sid

                # AD
                AD_Found                = $ad.Found
                AD_SamAccountName       = $ad.SamAccountName
                AD_DistinguishedName    = $ad.DistinguishedName
                AD_Enabled              = $ad.AD_Enabled
                AD_UserAccountControl   = $ad.UserAccountControl
                AD_LastLogon_Max        = $ad.LastLogonMax
                AD_LastLogonTimestamp   = $ad.LastLogonTimestamp
                EffectiveLastLogon      = $effective
                DaysSinceEffectiveLogon = $daysSince

                # Registry (ProfileList)
                Reg_ProfileKeyPresent   = $reg.Reg_ProfileKeyPresent
                Reg_ProfileImagePath    = $reg.Reg_ProfileImagePath
                Reg_Guid                = $reg.Reg_Guid
                Reg_Flags               = $reg.Reg_Flags
                Reg_State               = $reg.Reg_State
                Reg_FullProfile         = $reg.Reg_FullProfile
                Reg_LastLoadTime        = $reg.Reg_LastLoadTime
                Reg_LastUnloadTime      = $reg.Reg_LastUnloadTime
            }
        } catch {
            Write-Warning "Fehler bei Profil '$($p.LocalPath)': $($_.Exception.Message)"
            continue
        }
    }

    # 3) Kandidaten
    $oldProfiles = $profileReport | Where-Object {
        $_.DaysSinceEffectiveLogon -ne $null -and
        $_.DaysSinceEffectiveLogon -gt $Days -and
        ( -not $OnlyDisabled -or ($OnlyDisabled -and $_.AD_Enabled -eq $false) )
    } | Sort-Object DaysSinceEffectiveLogon -Descending

    # 4) Ausgabe
    if ($ShowCandidatesOnly) {
        $oldProfiles | Select-Object LocalPath,UserName,SID,AD_SamAccountName,AD_Enabled,EffectiveLastLogon,DaysSinceEffectiveLogon,Reg_LastLoadTime,Reg_LastUnloadTime
    } else {
        $profileReport |
            Sort-Object EffectiveLastLogon |
            Select-Object LocalPath,UserName,SID,AD_SamAccountName,AD_Enabled,AD_LastLogon_Max,AD_LastLogonTimestamp,EffectiveLastLogon,DaysSinceEffectiveLogon,Reg_LastLoadTime,Reg_LastUnloadTime,Reg_State,Reg_FullProfile,Reg_Guid,Reg_ProfileImagePath

        "`n--- Kandidaten (> $Days Tage{0}) ---" -f ($(if($OnlyDisabled){", nur disabled"} else {""}))
        $oldProfiles | Select-Object LocalPath,UserName,SID,AD_SamAccountName,AD_Enabled,EffectiveLastLogon,DaysSinceEffectiveLogon,Reg_LastLoadTime,Reg_LastUnloadTime
    }

    # 5) CSV (optional)
    if ($ExportCsv) {
        $reportPath = "$CsvBasePath.csv"
        $candPath   = "$CsvBasePath.Candidates_${Days}d{0}.csv" -f ($(if($OnlyDisabled){"_OnlyDisabled"} else {""}))
        $profileReport | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reportPath
        $oldProfiles   | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $candPath
        Write-Host "CSV exportiert: $reportPath" -ForegroundColor Green
        Write-Host "CSV exportiert: $candPath"   -ForegroundColor Green
    }
}

End { Write-Verbose "Fertig." }