#Requires -Modules Az.Accounts, Az.Compute, Az.Network, Az.Resources
<#
.SYNOPSIS
    Erstellt eine Windows 11-VM aus einer Shared Image Gallery (SIG) mit Trusted Launch (Gen2, SecureBoot, vTPM)
    und erneuert anschließend einmalig den EFI-Bootloader per RunCommand.

.DESCRIPTION
    - Findet die Shared Image Gallery per Name automatisch im Abo (wenn RG leer ist) oder nutzt die angegebene RG.
    - Listet Image-Definitionen und -Versionen (neueste zuerst) — optional nicht-interaktiv via -ImageDefinitionName/-ImageVersionName.
      Bevorzugt Versionen, die in der Zielregion als TargetRegion repliziert sind.
    - Erstellt die VM mit (optional) Trusted Launch, aktiviert Accelerated Networking (falls unterstützt),
      hängt die NIC an das gewünschte VNet/Subnet, aktiviert Boot Diagnostics.
    - Nach dem Deployment: EFI-Bootloader-Fix via bcdboot (RunCommand). Mit -SkipBootFix unterdrückbar.
    - Enthält einen Namens-Sanitizer, der VM-Ressourcenname & ComputerName auf NetBIOS-Regeln bringt (≤15 Zeichen etc.).
      Optional eigener -ComputerNameOverride.

.PARAMETER SubscriptionId
    Azure Subscription-ID.

.PARAMETER Location
    Zielregion (z. B. 'westeurope') und Filter für Image-Versionen (TargetRegions).

.PARAMETER RgTarget
    Resource Group für VM und NIC.

.PARAMETER RgNetwork
    (Optional) Resource Group des VNets; leer = Abo-weit suchen.

.PARAMETER VnetName
    Name des VNets.

.PARAMETER SubnetName
    Name des Subnetzes.

.PARAMETER VmName
    Gewünschter Basisname. Wird auf gültigen Windows-Computernamen saniert. Dient als ResourceName & ComputerName (sofern kein Override).

.PARAMETER ComputerNameOverride
    Optionaler expliziter Windows-Computername (wird ebenfalls saniert). Wenn leer, wird der sanitisierte VmName verwendet.

.PARAMETER VmSize
    VM-Größe (z. B. 'Standard_D8ds_v5').

.PARAMETER Tags
    Hashtable mit Tags für VM und NIC.

.PARAMETER GalleryResourceGroup
    RG der Shared Image Gallery. Leer lassen, um automatisch zu ermitteln.

.PARAMETER GalleryName
    Name der Shared Image Gallery.

.PARAMETER ImageDefinitionName
    Optional: exakte Image-Definition wählen (nicht interaktiv).

.PARAMETER ImageVersionName
    Optional: exakte Image-Version wählen (nicht interaktiv). Muss zur Definition passen.

.PARAMETER EnableTrustedLaunch
    Boolean, ob Trusted Launch (Gen2, SecureBoot, vTPM) gesetzt wird. Default: $true.

.PARAMETER AdminCredential
    PSCredential für den lokalen Admin der neuen VM. Fehlt es, erfolgt eine Eingabeaufforderung.

.PARAMETER SkipBootFix
    Unterdrückt den nachgelagerten EFI-Bootloader-Fix (bcdboot).

.PARAMETER Force
    Überspringt die interaktive Bestätigung vor der Erstellung.

.EXAMPLE
    # Interaktiv mit Defaults (neuste Version, Bootfix danach):
    .\Deploy-W11-FromSIG.ps1

.EXAMPLE
    # Nicht-interaktiv: bestimmte Definition/Version, Bootfix aus, ohne Rückfrage:
    .\Deploy-W11-FromSIG.ps1 -ImageDefinitionName 'W11-AVD' -ImageVersionName '1.0.15' -SkipBootFix -Force

.NOTES
    Voraussetzungen:
      - Windows PowerShell 5.1
      - Az-Module: Az.Accounts, Az.Compute, Az.Network, Az.Resources
      - Berechtigungen auf Ziel-RGs, Netzwerk und die Shared Image Gallery
#>

param(
    [string]$SubscriptionId      = "your-subscription-id-here",  # Ersetze durch deine Subscription-ID
    [string]$Location            = "westeurope",

    [string]$RgTarget            = "your-target-rg-here",
    [string]$RgNetwork           = "",

    [string]$VnetName            = "your-vnet-name-here",
    [string]$SubnetName          = "your-subnet-name-here",

    [string]$VmName              = "your-vm-name-here",
    [string]$ComputerNameOverride = "",

    [string]$VmSize              = "Standard_D8ds_v5",

    [hashtable]$Tags             = @{ "Workload"="AVD"; "Stage"="GoldImage"; "Usage"="PROD" },

    [string]$GalleryResourceGroup = "",
    [string]$GalleryName          = "your-gallery-name-here",

    [string]$ImageDefinitionName  = "",
    [string]$ImageVersionName     = "",

    [bool]$EnableTrustedLaunch    = $true,

    [System.Management.Automation.PSCredential]$AdminCredential,

    [switch]$SkipBootFix,
    [switch]$Force,

    # Neue Parameter für Post-Install Skript & TimeZone
    [string]$PostInstallScriptPath = "",
    [string]$TimeZone = "W. Europe Standard Time", # Standard-Zeitzone, kann angepasst werden 

    # Neuer Switch: MultiSessionHost (Default-Verhalten = $true)
    [switch]$MultiSessionHost
)

# Optional: TLS 1.2 erzwingen (hilft bei TLS/Proxy-Problemen unter PS 5.1)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ===========================
# Hilfsfunktionen
# ===========================
function Format-Tags {
    param([hashtable]$Tags)
    if (-not $Tags -or $Tags.Count -eq 0) { return "—" }
    ($Tags.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ", "
}

function Get-VNetByName {
    param(
        [string]$Name,
        [string]$Location,
        [string]$ResourceGroup
    )
    if ($ResourceGroup) {
        try { return Get-AzVirtualNetwork -Name $Name -ResourceGroupName $ResourceGroup -ErrorAction Stop } catch {}
    }
    $all = Get-AzVirtualNetwork -ErrorAction Stop | Where-Object { $_.Name -eq $Name }
    if (-not $all) { return $null }
    $inLoc = $all | Where-Object { $_.Location -eq $Location }
    if ($inLoc) { return ($inLoc | Select-Object -First 1) }
    return ($all | Select-Object -First 1)
}

function Test-AcceleratedNetworkingSupport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmSize,
        [Parameter(Mandatory)][string]$Location
    )
    try {
        $sku = Get-AzComputeResourceSku -Location $Location -ErrorAction Stop |
               Where-Object { $_.ResourceType -eq "virtualMachines" -and $_.Name -ieq $VmSize } |
               Select-Object -First 1
        if (-not $sku) { return $false }

        $cap = $sku.Capabilities | Where-Object {
            $_.Name -in @("AcceleratedNetworkingEnabled","AcceleratedNetworking","AcceleratedNetworkingSupported")
        } | Select-Object -First 1

        if ($cap -and ($cap.Value -match '^(true|True|TRUE|1)$')) { return $true }
        return $false
    }
    catch { return $false }
}

function Resolve-GalleryByName {
    param(
        [Parameter(Mandatory)][string]$GalleryName,
        [string]$ResourceGroup
    )
    if ($ResourceGroup) {
        try {
            $g = Get-AzGallery -ResourceGroupName $ResourceGroup -Name $GalleryName -ErrorAction Stop
            return [pscustomobject]@{ Gallery = $g; ResourceGroupName = $ResourceGroup }
        } catch {
            throw "Gallery '$GalleryName' wurde in Resource Group '$ResourceGroup' nicht gefunden. Fehler: $($_.Exception.Message)"
        }
    }
    $allGalleries = Get-AzGallery -ErrorAction Stop | Where-Object { $_.Name -eq $GalleryName }
    if (-not $allGalleries -or $allGalleries.Count -eq 0) {
        throw "Keine Gallery mit Name '$GalleryName' im aktuellen Subscription-Kontext gefunden."
    }
    if ($allGalleries.Count -eq 1) {
        $one = $allGalleries[0]
        return [pscustomobject]@{ Gallery = $one; ResourceGroupName = $one.ResourceGroupName }
    }
    Write-Host "`nMehrere Galleries mit Name '$GalleryName' gefunden:" -ForegroundColor Cyan
    $idx = 0
    foreach ($g in $allGalleries) {
        $idx++
        Write-Host ("[{0}] RG: {1} | Location: {2} | Id: {3}" -f $idx, $g.ResourceGroupName, $g.Location, $g.Id)
    }
    $sel = Read-Host "Bitte Nummer wählen (Enter = 1)"
    if ([string]::IsNullOrWhiteSpace($sel)) { $sel = 1 }
    if ([int]$sel -lt 1 -or [int]$sel -gt $allGalleries.Count) { throw "Ungültige Auswahl." }
    $chosen = $allGalleries[[int]$sel - 1]
    return [pscustomobject]@{ Gallery = $chosen; ResourceGroupName = $chosen.ResourceGroupName }
}

function Select-GalleryImage {
    <#
      Gibt .Definition und .Version zurück (Objekte von Az).
      Nicht-interaktiv, wenn -ImageDefinitionName/-ImageVersionName gesetzt sind.
      Filtert Versionen auf TargetRegions.Name == $Location (falls vorhanden), sonst Warnung + alle anzeigen.
    #>
    param(
        [Parameter(Mandatory)][string]$GalleryResourceGroup,
        [Parameter(Mandatory)][string]$GalleryName,
        [Parameter(Mandatory)][string]$Location,
        [string]$ImageDefinitionName,
        [string]$ImageVersionName
    )

    $defs = Get-AzGalleryImageDefinition -ResourceGroupName $GalleryResourceGroup -GalleryName $GalleryName -ErrorAction Stop
    if (-not $defs -or $defs.Count -eq 0) { throw "In der Gallery '$GalleryName' (RG: $GalleryResourceGroup) wurden keine Image-Definitionen gefunden." }

    $def = $null
    if ($ImageDefinitionName) {
        $def = $defs | Where-Object { $_.Name -eq $ImageDefinitionName } | Select-Object -First 1
        if (-not $def) { throw "Image-Definition '$ImageDefinitionName' wurde nicht gefunden." }
    } else {
        Write-Host "`nGefundene Image-Definitionen in Gallery '$GalleryName':" -ForegroundColor Cyan
        $i = 0
        foreach ($d in $defs) {
            $i++
            $gen = $d.HyperVGeneration
            Write-Host ("[{0}] {1}  (Publisher: {2} | Offer: {3} | Sku: {4} | Gen: {5})" -f $i, $d.Name, $d.Publisher, $d.Offer, $d.Sku, $gen)
        }
        $selIndex = Read-Host "Bitte Nummer der Image-Definition wählen (Enter = 1)"
        if ([string]::IsNullOrWhiteSpace($selIndex)) { $selIndex = 1 }
        if ([int]$selIndex -lt 1 -or [int]$selIndex -gt $defs.Count) { throw "Ungültige Auswahl." }
        $def = $defs[[int]$selIndex - 1]
    }

    if ($EnableTrustedLaunch -and $def.HyperVGeneration -ne "V2") {
        throw "Trusted Launch erfordert Gen2 (HyperVGeneration V2), aber die gewählte Definition ist '$($def.HyperVGeneration)'."
    }

    $allVersions = Get-AzGalleryImageVersion -ResourceGroupName $GalleryResourceGroup -GalleryName $GalleryName -GalleryImageDefinitionName $def.Name -ErrorAction Stop

    $versions = @()
    foreach ($v in $allVersions) {
        $trMatch = $false
        if ($v.PublishingProfile -and $v.PublishingProfile.TargetRegions) {
            foreach ($tr in $v.PublishingProfile.TargetRegions) {
                if ($tr.Name -eq $Location) { $trMatch = $true; break }
            }
        }
        if ($trMatch) { $versions += $v }
    }
    if (-not $versions -or $versions.Count -eq 0) {
        Write-Host ("Hinweis: Für die Definition '{0}' sind keine Versionen mit TargetRegion '{1}' vorhanden." -f $def.Name, $Location) -ForegroundColor Yellow
        Write-Host "Es werden alle Versionen angezeigt (keine Regions-Filterung). Prüfe Replikation in die Zielregion!" -ForegroundColor Yellow
        $versions = $allVersions
    }

    # Sortierung: neueste zuerst
    $versions = $versions | Sort-Object -Property @{Expression = {
        if ($_.PublishingProfile -and $_.PublishingProfile.PublishedDate) { $_.PublishingProfile.PublishedDate } else { Get-Date '1900-01-01' }
    }}, @{Expression = {
        try { [version]$_.Name } catch { [version]"0.0.0" }
    }} -Descending

    $ver = $null
    if ($ImageVersionName) {
        $ver = $versions | Where-Object { $_.Name -eq $ImageVersionName } | Select-Object -First 1
        if (-not $ver) { throw "Image-Version '$ImageVersionName' wurde für Definition '$($def.Name)' nicht gefunden (oder nicht in TargetRegion '$Location')." }
    } else {
        Write-Host ("`nVerfügbare Versionen für '{0}' (TargetRegion: {1}; neueste zuerst):" -f $def.Name, $Location) -ForegroundColor Cyan
        $j = 0
        foreach ($v in ($versions | Select-Object -First 10)) {
            $j++
            $pub = "—"
            if ($v.PublishingProfile -and $v.PublishingProfile.PublishedDate) { $pub = $v.PublishingProfile.PublishedDate.ToString("yyyy-MM-dd HH:mm") }
            Write-Host ("({0}) {1}  | Published: {2}" -f $j, $v.Name, $pub)
        }
        $verChoice = Read-Host "Bitte Versionsnummer wählen (Enter = 1 = neueste)"
        if ([string]::IsNullOrWhiteSpace($verChoice)) { $verChoice = 1 }
        $maxChoice = [Math]::Min(10, $versions.Count)
        if ([int]$verChoice -lt 1 -or [int]$verChoice -gt $maxChoice) { throw "Ungültige Auswahl." }
        $ver = $versions[[int]$verChoice - 1]
    }

    [pscustomobject]@{
        Definition = $def
        Version    = $ver
    }
}

function Get-ValidWindowsComputerName {
    param([Parameter(Mandatory)][string]$BaseName)
    $name = ($BaseName -replace '[^A-Za-z0-9-]', '-')
    $name = $name.Trim('-')
    if ([string]::IsNullOrWhiteSpace($name)) { $name = "vm" + (Get-Random -Maximum 9999) }
    if ($name.Length -gt 15) { $name = $name.Substring(0,15) }
    if ($name -match '^[0-9]+$') { $name = "vm-$name" }
    if ($name.Length -gt 15) { $name = $name.Substring(0,15) }
    $name = $name.TrimEnd('-')
    if ([string]::IsNullOrWhiteSpace($name)) { $name = "vm" + (Get-Random -Maximum 9999) }
    return $name
}

# ===========================
# Login & Subscription
# ===========================
if (-not (Get-AzContext)) { Connect-AzAccount -DeviceCode | Out-Null }
Select-AzSubscription -SubscriptionId $SubscriptionId | Out-Null

# ===========================
# Gallery ermitteln (RG auto, falls leer)
# ===========================
$galleryInfo = Resolve-GalleryByName -GalleryName $GalleryName -ResourceGroup $GalleryResourceGroup
$resolvedGalleryRg = $galleryInfo.ResourceGroupName
$resolvedGallery   = $galleryInfo.Gallery

# ===========================
# Gallery-Auswahl (Definition + Version)
# ===========================
$choice = Select-GalleryImage -GalleryResourceGroup $resolvedGalleryRg -GalleryName $resolvedGallery.Name -Location $Location -ImageDefinitionName $ImageDefinitionName -ImageVersionName $ImageVersionName
$imgDef = $choice.Definition
$imgVer = $choice.Version

# ===========================
# Netzwerkobjekte ermitteln
# ===========================
$vnet = Get-VNetByName -Name $VnetName -Location $Location -ResourceGroup $RgNetwork
if (-not $vnet) { throw "VNet '$VnetName' wurde nicht gefunden." }

$subnet = $vnet.Subnets | Where-Object { $_.Name -eq $SubnetName }
if (-not $subnet) { throw "Subnet '$SubnetName' im VNet '$($vnet.Name)' nicht gefunden." }

# ===========================
# Accelerated Networking prüfen
# ===========================
$enableAccelNet = Test-AcceleratedNetworkingSupport -VmSize $VmSize -Location $Location

# ===========================
# Admin-Credentials (falls nicht übergeben)
# ===========================
if (-not $AdminCredential) {
    $adminUser = Read-Host "Bitte lokalen Admin-Benutzernamen für die neue VM eingeben"
    if ([string]::IsNullOrWhiteSpace($adminUser)) { throw "Admin-Benutzername darf nicht leer sein." }
    $adminPass = Read-Host "Bitte Passwort für '$adminUser' eingeben" -AsSecureString
    $AdminCredential = New-Object System.Management.Automation.PSCredential($adminUser, $adminPass)
}

# ===========================
# Namen sanitisieren (VM ResourceName & Windows ComputerName)
# ===========================
$vmNameOriginal = $VmName
$VmName         = Get-ValidWindowsComputerName -BaseName $vmNameOriginal

$ComputerName = $VmName
if ($ComputerNameOverride) {
    $ComputerName = Get-ValidWindowsComputerName -BaseName $ComputerNameOverride
}

if ($VmName -ne $vmNameOriginal) {
    Write-Warning ("VM-Name wurde wegen NetBIOS-Regeln angepasst: '{0}' -> '{1}'" -f $vmNameOriginal, $VmName)
}
if ($ComputerNameOverride -and ($ComputerNameOverride -ne $ComputerName)) {
    Write-Warning ("ComputerNameOverride wurde angepasst auf gültigen Namen: '{0}' -> '{1}'" -f $ComputerNameOverride, $ComputerName)
}

# ===========================
# Zusammenfassung
# ===========================
$securitySummary = ""
if ($EnableTrustedLaunch) { $securitySummary = "TrustedLaunch (SecureBoot + vTPM)" } else { $securitySummary = "Standard (explicit: SecureBoot/vTPM OFF)" }

# Nach Sanitisierung der Namen (oder an einer vergleichbaren Stelle vor Summary) 
# Bestimme effektiven Wert (Default = $true, sofern der Switch nicht explizit übergeben wurde)
if ($PSBoundParameters.ContainsKey('MultiSessionHost')) {
    # Wenn Switch gesetzt wurde, nutze seinen booleschen Wert (z.B. -MultiSessionHost:$false möglich)
    $UseMultiSessionHost = [bool]$MultiSessionHost
} else {
    # Default: true
    $UseMultiSessionHost = $true
}

Write-Host "================ SUMMARY ================" -ForegroundColor Cyan
Write-Host "Subscription : $SubscriptionId"
Write-Host "Region       : $Location"
Write-Host "Gallery      : $($resolvedGallery.Name) (RG: $resolvedGalleryRg)"
Write-Host "Image Def    : $($imgDef.Name)  (Gen: $($imgDef.HyperVGeneration))"
if ($imgVer.PublishingProfile -and $imgVer.PublishingProfile.PublishedDate) {
    Write-Host ("Image Ver    : {0}  (Published: {1})" -f $imgVer.Name, $imgVer.PublishingProfile.PublishedDate)
} else {
    Write-Host ("Image Ver    : {0}  (Published: —)" -f $imgVer.Name)
}
Write-Host "VM Name      : $VmName"
Write-Host "ComputerName : $ComputerName"
Write-Host "VM Size      : $VmSize"
Write-Host "Target RG    : $RgTarget"
Write-Host "VNet/Subnet  : $($vnet.Name) / $SubnetName (RG: $($vnet.ResourceGroupName))"
Write-Host "NIC Name     : $VmName-nic"
Write-Host "Security     : $securitySummary"
Write-Host "Accel. Net   : $enableAccelNet"
Write-Host "MultiSession : $UseMultiSessionHost"
Write-Host "Tags         : $(Format-Tags -Tags $Tags)"
Write-Host "=========================================" -ForegroundColor Cyan

if (-not $Force) {
    $confirmation = Read-Host "Soll die VM jetzt erstellt werden? (Y/N)"
    if ($confirmation -notin @("Y","y","Yes","yes","J","j")) {
        Write-Host "Abgebrochen." -ForegroundColor Yellow
        return
    }
}

# ===========================
# NIC erstellen
# ===========================
$nicName = "$VmName-nic"
$nicParams = @{
    Name              = $nicName
    ResourceGroupName = $RgTarget
    Location          = $Location
    SubnetId          = $subnet.Id
    Tag               = $Tags
}
if ($enableAccelNet) { $nicParams["EnableAcceleratedNetworking"] = $true }

$nic = New-AzNetworkInterface @nicParams

# ===========================
# VM-Konfiguration aufbauen
# ===========================
$vmConfig = New-AzVMConfig -VMName $VmName -VMSize $VmSize

# Trusted Launch / UEFI
if ($EnableTrustedLaunch) {
    $vmConfig = Set-AzVMSecurityProfile -VM $vmConfig -SecurityType "TrustedLaunch"
    $vmConfig = Set-AzVMUefi          -VM $vmConfig -EnableVtpm $true -EnableSecureBoot $true
} else {
    $vmConfig = Set-AzVMSecurityProfile -VM $vmConfig -SecurityType "Standard"
    $vmConfig = Set-AzVMUefi          -VM $vmConfig -EnableVtpm $false -EnableSecureBoot $false
}

# Quelle: SIG-Image-Version
$vmConfig = Set-AzVMSourceImage -VM $vmConfig -Id $imgVer.Id

# OS-Typ + Admin (ComputerName = sanitisierter Name oder Override)
$vmConfig = Set-AzVMOperatingSystem -VM $vmConfig -Windows -ComputerName $ComputerName -Credential $AdminCredential -ProvisionVMAgent

# NIC zuweisen
$vmConfig = Add-AzVMNetworkInterface -VM $vmConfig -Id $nic.Id -Primary

# Boot Diagnostics (Managed)
$vmConfig = Set-AzVMBootDiagnostic -VM $vmConfig -Enable

# ===========================
# VM erstellen
# ===========================
New-AzVM -ResourceGroupName $RgTarget -Location $Location -VM $vmConfig -Tag $Tags -ErrorAction Stop

Write-Host "VM '$VmName' wurde erfolgreich erstellt. Sicherheitsmodus: $securitySummary | Accelerated Networking: $enableAccelNet" -ForegroundColor Green


# Setze LicenseType für Windows 10/11 Multi-Session (erforderlich für Multi-Session-Images)
try {
    if ($UseMultiSessionHost) {
        Write-Host "Setze LicenseType = 'Windows_Client' für VM '$VmName' ..." -ForegroundColor Cyan
        $vmObj = Get-AzVM -ResourceGroupName $RgTarget -Name $VmName -ErrorAction Stop
        $vmObj.LicenseType = "Windows_Client"
        Update-AzVM -ResourceGroupName $RgTarget -VM $vmObj -ErrorAction Stop
        Write-Host "LicenseType erfolgreich gesetzt." -ForegroundColor Green
    } else {
        Write-Host "LicenseType-Update übersprungen (MultiSessionHost deaktiviert)." -ForegroundColor Yellow
    }
}
catch {
    Write-Warning "Konnte LicenseType nicht setzen: $($_.Exception.Message)"
}

# ===========================
# POST-STEP: EFI-Bootloader neu schreiben (einmalig)
# ===========================
if (-not $SkipBootFix) {
    Write-Host "Starte einmalige Reparatur des EFI-Bootloaders per RunCommand ..." -ForegroundColor Cyan
    $bootFixScript = @'
# Mount EFI System Partition (ESP) als S:, schreibe Bootdateien neu, unmount
# Fallback: direktes bcdboot ohne -s

try {
    # ESP einhängen
    & mountvol S: /S 2>&1 | Out-Null

    # bcdboot: /p überschreibt den bestehenden Eintrag statt neuen anzulegen
    & bcdboot C:\Windows /s S: /f UEFI /p /v
    $rc = $LASTEXITCODE

    # ESP wieder aushängen
    & mountvol S: /D

    if ($rc -ne 0) { throw "bcdboot mit ESP-Laufwerk schlug fehl (Code: $rc)." }
}
catch {
    # Fallback ohne expliziten ESP-Pfad
    & bcdboot C:\Windows /f UEFI /p /v
    if ($LASTEXITCODE -ne 0) {
        throw "bcdboot Fallback fehlgeschlagen (Code: $LASTEXITCODE). Fehler: $($_.Exception.Message)"
    }
}

# Timeout = 0, damit Bootmenü nicht angezeigt wird
& bcdedit /timeout 0

Write-Output "EFI-Bootdateien wurden erfolgreich erneuert und Bootmenü bereinigt."
'@
    $rcRes = Invoke-AzVMRunCommand -ResourceGroupName $RgTarget -Name $VmName -CommandId 'RunPowerShellScript' -ScriptString $bootFixScript -ErrorAction Stop
    $rcRes.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }
    Write-Host "EFI-Bootloader-Reparatur abgeschlossen." -ForegroundColor Green

    # Optionaler Neustart
    if (-not $Force) {
        $doReboot = Read-Host "Soll die VM jetzt einmal neu gestartet werden? (Y/N)"
        if ($doReboot -in @("Y","y","Yes","yes","J","j")) {
            Restart-AzVM -ResourceGroupName $RgTarget -Name $VmName -NoWait
            Write-Host "Neustart initiiert." -ForegroundColor Green
        } else {
            Write-Host "Neustart übersprungen." -ForegroundColor Yellow
        }
    } else {
        # bei Force keinen Prompt – optional kannst du hier auto-rebooten, ich lasse es neutral
        Write-Host "Neustart-Prompt übersprungen (Force aktiv)." -ForegroundColor Yellow
    }
} else {
    Write-Host "EFI-Bootloader-Fix übersprungen (SkipBootFix gesetzt)." -ForegroundColor Yellow
}
# ===========================
# POST-STEP: Optionales Post-Install Skript auf der VM ausführen
# Die TimeZone wird in der Remote-Sitzung als Variable $TimeZone gesetzt (String).
# ===========================
if ($PostInstallScriptPath) {
    if (-not (Test-Path -Path $PostInstallScriptPath -PathType Leaf)) {
        throw "PostInstallScript '$PostInstallScriptPath' wurde nicht gefunden."
    }

    Write-Host "Bereite Ausführung von Post-Install-Skript vor: $PostInstallScriptPath" -ForegroundColor Cyan

    try {
        $scriptContent = Get-Content -Path $PostInstallScriptPath -Raw -ErrorAction Stop
        # Escape single quotes, dann injizieren wir die TimeZone-Variable vorneweg
        $tzEscaped = $TimeZone -replace "'", "''"
        $injection = "$TimeZone = '$tzEscaped'`r`n"
        $fullScriptText = $injection + $scriptContent

        # Invoke-AzVMRunCommand erwartet eine string[] (ScriptString). Splitten nach Zeilen.
        $scriptLines = $fullScriptText -split "`r?`n"

        Write-Host "Sende Skript an VM '$VmName' und führe es aus..." -ForegroundColor Cyan
        $res = Invoke-AzVMRunCommand -ResourceGroupName $RgTarget -Name $VmName -CommandId 'RunPowerShellScript' -ScriptString $scriptLines -ErrorAction Stop
        $res.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }
        Write-Host "Post-Install-Skript wurde auf VM '$VmName' ausgeführt." -ForegroundColor Green
    }
    catch {
        throw "Fehler beim Ausführen des Post-Install-Skripts: $($_.Exception.Message)"
    }
}