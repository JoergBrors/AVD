#Requires -Modules Az.Accounts, Az.Compute, Az.Network, Az.Resources
<#
.SYNOPSIS
    Erstellt aus einer bestehenden Windows-"Golden-Image"-VM eine neue Version in einer Azure Compute Gallery (SIG),
    ohne die Quell-VM zu verändern (non-destructive).

.DESCRIPTION
    Ablauf:
      1) Interaktive SIG-Auswahl oder neue SIG-Erstellung
      2) Snapshot der OS-Managed-Disk der Quell-VM erstellen.
      3) Aus Snapshot eine Staging-OS-Disk provisionieren.
      4) Staging-VM (mit NIC im angegebenen VNet/Subnet) aus dieser Disk erstellen.
         - Sicherheitsprofil (Trusted Launch / UEFI) wird von der Quelle übernommen.
      5) Sysprep in der Staging-VM per RunCommand: /generalize /oobe /shutdown /mode:vm
      6) VM-Zustand "generalized" setzen.
      7) Managed Image aus der generalisierten Staging-VM erzeugen.
      8) SIG Image Version erstellen (Replikation in TargetRegions, Storage-Tier konfigurierbar).
      9) Optional Staging-Ressourcen aufräumen.

.PARAMETER SubscriptionId
    Azure Subscription-ID.

.PARAMETER Location
    Primäre Region (z. B. 'westeurope'). Muss zur Gallery-Definition passen.

.PARAMETER SourceVmName
    Name der Golden-Image-VM (Original).

.PARAMETER SourceVmRg
    Resource Group der Golden-Image-VM.

.PARAMETER StagingRg
    Resource Group für temporäre Staging-Ressourcen (VM, Disk, NIC, Snapshot).

.PARAMETER VnetName
    VNet-Name für Staging-VM.

.PARAMETER VnetRg
    Resource Group des VNets.

.PARAMETER SubnetName
    Subnet-Name im VNet für Staging-VM.

.PARAMETER GalleryName
    Name der Azure Compute Gallery (optional - wird über Dialog ausgewählt/erstellt).

.PARAMETER GalleryRg
    Resource Group der Gallery (optional - wird über Dialog ausgewählt/erstellt).

.PARAMETER ImageDefinitionName
    Name der bestehenden Gallery-Image-Definition (optional - wird über Dialog ausgewählt/erstellt).

.PARAMETER ImageVersion
    Neue Versionsnummer (SemVer-Format empfohlen, z. B. '1.0.42' oder Datumsformat '2025.08.17').

.PARAMETER TargetRegions
    Liste der Zielregionen für die Replikation (einschließlich Primärregion, falls gewünscht).

.PARAMETER ReplicaCount
    Anzahl Replikate pro Zielregion (Default 1).

.PARAMETER StorageAccountType
    Storage-Tier der Version (z. B. 'Standard_LRS', 'Standard_ZRS', 'Premium_LRS').

.PARAMETER ExcludeFromLatest
    Wenn gesetzt, wird die Version nicht als "latest" berücksichtigt.

.PARAMETER EndOfLife
    Optionales End-of-life Datum (yyyy-MM-dd).

.PARAMETER StagingVmSize
    Größe der Staging-VM (Default: 'Standard_D4ds_v5').

.PARAMETER CleanUp
    Wenn gesetzt, werden Staging-VM, NIC, PIP (falls angelegt), Staging-OS-Disk und Snapshot nach erfolgreicher Veröffentlichung gelöscht.
    Das Managed Image wird nach Erstellung der Gallery-Version ebenfalls entfernt.

.NOTES
    - Quell-VM bleibt unverändert.
    - Erfordert Netzwerkzugriff nur für Staging-VM-Bereitstellung (RunCommand funktioniert ohne externe Erreichbarkeit).
    - Falls BitLocker aktiv war, sollte die Staging-VM dennoch booten können; Sysprep entfernt Maschinenbindung.
    - Für sehr große OS-Disks empfiehlt sich ZRS-Snapshots in passenden Regionen.

#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$Location,

    [Parameter(Mandatory)][string]$SourceVmName,
    [Parameter(Mandatory)][string]$SourceVmRg,

    [Parameter(Mandatory)][string]$StagingRg,

    [Parameter(Mandatory)][string]$VnetName,
    [Parameter(Mandatory)][string]$VnetRg,
    [Parameter(Mandatory)][string]$SubnetName,

    [string]$GalleryName,
    [string]$GalleryRg,
    [string]$ImageDefinitionName,
    [Parameter(Mandatory)][string]$ImageVersion,

    [string[]]$TargetRegions = @(),
    [int]$ReplicaCount = 1,
    [ValidateSet('Standard_LRS','Standard_ZRS','Premium_LRS')]
    [string]$StorageAccountType = 'Standard_LRS',
    [switch]$ExcludeFromLatest,
    [string]$EndOfLife,

    [string]$StagingVmSize = 'Standard_D4ds_v5',
    [switch]$CleanUp
)

# --- Robustere TLS-Handshake-Einstellung (PS 5.1) ---
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- Login / Subscription ---
if (-not (Get-AzContext)) { Connect-AzAccount -DeviceCode | Out-Null }
Select-AzSubscription -SubscriptionId $SubscriptionId | Out-Null

# --- Helper Functions ---
function Wait-ForVmPowerState {
    param(
        [Parameter(Mandatory)][string]$VmName,
        [Parameter(Mandatory)][string]$VmRg,
        [Parameter(Mandatory)][string]$DesiredState, # 'VM running' | 'VM deallocated' | 'VM stopped'
        [int]$TimeoutSec = 1800,
        [int]$PollSec = 15
    )
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $instance = Get-AzVM -Name $VmName -ResourceGroupName $VmRg -Status -ErrorAction Stop
        $state = ($instance.Statuses | Where-Object { $_.Code -like "PowerState/*" }).DisplayStatus
        if ($state -eq $DesiredState) { return $true }
        Start-Sleep -Seconds $PollSec
    }
    throw "Timeout: VM '$VmName' hat den Zustand '$DesiredState' nicht innerhalb ${TimeoutSec}s erreicht."
}

function New-Name {
    param([string]$Prefix)
    $rand = (Get-Random -Maximum 99999).ToString('00000')
    return "$Prefix$rand"
}

function Show-SigSelectionDialog {
    Write-Host "`n=== Azure Compute Gallery (SIG) Auswahl ===" -ForegroundColor Yellow
    
    # Alle verfügbaren SIGs in der Subscription auflisten
    try {
        $galleries = Get-AzGallery -ErrorAction Stop
    } catch {
        Write-Host "Fehler beim Abrufen der SIGs: $($_.Exception.Message)" -ForegroundColor Red
        $galleries = @()
    }
    
    if ($galleries.Count -eq 0) {
        Write-Host "Keine bestehenden SIGs gefunden." -ForegroundColor Yellow
        $createNew = Read-Host "Möchten Sie eine neue SIG erstellen? (j/n)"
        if ($createNew -eq 'j' -or $createNew -eq 'J' -or $createNew -eq 'y' -or $createNew -eq 'Y') {
            return $null  # Signal für neue SIG-Erstellung
        } else {
            throw "Keine SIG ausgewählt oder erstellt."
        }
    }
    
    Write-Host "`nVerfügbare SIGs:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $galleries.Count; $i++) {
        $gallery = $galleries[$i]
        Write-Host "[$($i+1)] $($gallery.Name) (RG: $($gallery.ResourceGroupName), Location: $($gallery.Location))"
    }
    Write-Host "[0] Neue SIG erstellen" -ForegroundColor Green
    
    do {
        $selection = Read-Host "`nBitte wählen Sie eine Option (0-$($galleries.Count))"
        $selectionInt = 0
        $validSelection = [int]::TryParse($selection, [ref]$selectionInt)
    } while (-not $validSelection -or $selectionInt -lt 0 -or $selectionInt -gt $galleries.Count)
    
    if ($selectionInt -eq 0) {
        return $null  # Signal für neue SIG-Erstellung
    } else {
        return $galleries[$selectionInt - 1]
    }
}

function New-SigForAVD {
    param(
        [Parameter(Mandatory)][string]$Location
    )
    
    Write-Host "`n=== Neue SIG für Windows 11 AVD erstellen ===" -ForegroundColor Yellow
    
    # SIG-Details abfragen
    $sigName = Read-Host "Name der neuen SIG (z.B. 'sig-avd-westeurope')"
    if ([string]::IsNullOrWhiteSpace($sigName)) {
        $sigName = "sig-avd-$(Get-Date -Format 'yyyyMMdd')"
        Write-Host "Verwende Standard-Name: $sigName" -ForegroundColor Cyan
    }
    
    # SIG-Name validieren (nur Buchstaben, Zahlen, Punkte, Unterstriche, Bindestriche)
    if ($sigName -notmatch '^[a-zA-Z0-9._-]+$') {
        throw "SIG-Name enthält ungültige Zeichen. Nur Buchstaben, Zahlen, Punkte, Unterstriche und Bindestriche sind erlaubt."
    }
    
    $sigRgName = Read-Host "Resource Group für die SIG (ENTER für neue RG '$sigName-rg')"
    if ([string]::IsNullOrWhiteSpace($sigRgName)) {
        $sigRgName = "$sigName-rg"
    }
    
    # Resource Group erstellen oder validieren
    $rg = Get-AzResourceGroup -Name $sigRgName -ErrorAction SilentlyContinue
    if (-not $rg) {
        Write-Host "Erstelle Resource Group: $sigRgName in West Europe..." -ForegroundColor Cyan
        try {
            New-AzResourceGroup -Name $sigRgName -Location "westeurope" | Out-Null
        } catch {
            throw "Fehler beim Erstellen der Resource Group: $($_.Exception.Message)"
        }
    } else {
        Write-Host "Verwende bestehende Resource Group: $sigRgName" -ForegroundColor Cyan
    }
    
    # SIG erstellen (immer in West Europe für AVD)
    $sigLocation = "westeurope"
    Write-Host "Erstelle SIG '$sigName' in West Europe..." -ForegroundColor Cyan
    
    try {
        $sig = New-AzGallery -ResourceGroupName $sigRgName -Name $sigName -Location $sigLocation -Description "Azure Compute Gallery für Windows 11 AVD Images mit Trusted Launch und vTPM" -ErrorAction Stop
    } catch {
        throw "Fehler beim Erstellen der SIG: $($_.Exception.Message)"
    }
    
    # Windows 11 AVD Image Definition erstellen
    $imgDefName = Read-Host "Name der Image Definition (ENTER für 'W11-AVD-TrustedLaunch')"
    if ([string]::IsNullOrWhiteSpace($imgDefName)) {
        $imgDefName = "W11-AVD-TrustedLaunch"
    }
    
    Write-Host "Erstelle Image Definition '$imgDefName' für Windows 11 AVD mit Trusted Launch..." -ForegroundColor Cyan
    
    try {
        # Features für Trusted Launch und vTPM
        $features = @()
        $features += New-AzGalleryImageFeature -Name "SecurityType" -Value "TrustedLaunch"
        $features += New-AzGalleryImageFeature -Name "IsAcceleratedNetworkSupported" -Value "true"
        
        $imgDef = New-AzGalleryImageDefinition `
            -ResourceGroupName $sigRgName `
            -GalleryName $sigName `
            -Name $imgDefName `
            -Location $sigLocation `
            -Publisher "MicrosoftWindowsDesktop" `
            -Offer "Windows-11" `
            -Sku "win11-22h2-avd" `
            -OsType "Windows" `
            -OsState "Generalized" `
            -HyperVGeneration "V2" `
            -Feature $features `
            -Description "Windows 11 AVD Image mit Trusted Launch, vTPM und Escalator-Netzwerk-Support" `
            -ErrorAction Stop
            
        Write-Host "SIG und Image Definition erfolgreich erstellt!" -ForegroundColor Green
        
        return @{
            Gallery = $sig
            ImageDefinition = $imgDef
        }
    } catch {
        # Cleanup bei Fehler
        Write-Warning "Fehler beim Erstellen der Image Definition: $($_.Exception.Message)"
        try {
            Remove-AzGallery -ResourceGroupName $sigRgName -Name $sigName -Force -ErrorAction SilentlyContinue
        } catch {}
        throw
    }
}

# --- SIG Auswahl oder Erstellung ---
if ([string]::IsNullOrWhiteSpace($GalleryName) -or [string]::IsNullOrWhiteSpace($GalleryRg) -or [string]::IsNullOrWhiteSpace($ImageDefinitionName)) {
    try {
        $selectedGallery = Show-SigSelectionDialog
        
        if ($null -eq $selectedGallery) {
            # Neue SIG erstellen
            $sigResult = New-SigForAVD -Location $Location
            $GalleryName = $sigResult.Gallery.Name
            $GalleryRg = $sigResult.Gallery.ResourceGroupName
            $ImageDefinitionName = $sigResult.ImageDefinition.Name
            Write-Host "Verwende neue SIG: $GalleryName (RG: $GalleryRg), Definition: $ImageDefinitionName" -ForegroundColor Magenta
        } else {
            # Bestehende SIG verwenden
            $GalleryName = $selectedGallery.Name
            $GalleryRg = $selectedGallery.ResourceGroupName
            
            # Image Definitions in der ausgewählten SIG anzeigen
            try {
                $imgDefs = Get-AzGalleryImageDefinition -ResourceGroupName $GalleryRg -GalleryName $GalleryName -ErrorAction Stop
            } catch {
                throw "Fehler beim Abrufen der Image Definitions: $($_.Exception.Message)"
            }
            
            if ($imgDefs.Count -eq 0) {
                Write-Host "Keine Image Definitions in der ausgewählten SIG gefunden." -ForegroundColor Yellow
                $createNewDef = Read-Host "Möchten Sie eine neue Image Definition erstellen? (j/n)"
                if ($createNewDef -eq 'j' -or $createNewDef -eq 'J' -or $createNewDef -eq 'y' -or $createNewDef -eq 'Y') {
                    # Neue Image Definition in bestehender SIG erstellen
                    $imgDefName = Read-Host "Name der neuen Image Definition (ENTER für 'W11-AVD-TrustedLaunch')"
                    if ([string]::IsNullOrWhiteSpace($imgDefName)) {
                        $imgDefName = "W11-AVD-TrustedLaunch"
                    }
                    
                    Write-Host "Erstelle neue Image Definition '$imgDefName'..." -ForegroundColor Cyan
                    
                    $features = @()
                    $features += New-AzGalleryImageFeature -Name "SecurityType" -Value "TrustedLaunch"
                    $features += New-AzGalleryImageFeature -Name "IsAcceleratedNetworkSupported" -Value "true"
                    
                    New-AzGalleryImageDefinition `
                        -ResourceGroupName $GalleryRg `
                        -GalleryName $GalleryName `
                        -Name $imgDefName `
                        -Location $selectedGallery.Location `
                        -Publisher "MicrosoftWindowsDesktop" `
                        -Offer "Windows-11" `
                        -Sku "win11-22h2-avd" `
                        -OsType "Windows" `
                        -OsState "Generalized" `
                        -HyperVGeneration "V2" `
                        -Feature $features `
                        -Description "Windows 11 AVD Image mit Trusted Launch, vTPM und Escalator-Netzwerk-Support" | Out-Null
                    
                    $ImageDefinitionName = $imgDefName
                } else {
                    throw "Keine Image Definition ausgewählt oder erstellt."
                }
            } else {
                if ([string]::IsNullOrWhiteSpace($ImageDefinitionName)) {
                    Write-Host "`nVerfügbare Image Definitions:" -ForegroundColor Cyan
                    for ($i = 0; $i -lt $imgDefs.Count; $i++) {
                        $desc = if ($imgDefs[$i].Description) { " - $($imgDefs[$i].Description)" } else { "" }
                        Write-Host "[$($i+1)] $($imgDefs[$i].Name)$desc"
                    }
                    
                    do {
                        $selection = Read-Host "Bitte wählen Sie eine Image Definition (1-$($imgDefs.Count))"
                        $selectionInt = 0
                        $validSelection = [int]::TryParse($selection, [ref]$selectionInt)
                    } while (-not $validSelection -or $selectionInt -lt 1 -or $selectionInt -gt $imgDefs.Count)
                    
                    $ImageDefinitionName = $imgDefs[$selectionInt - 1].Name
                }
            }
            
            Write-Host "Verwende SIG: $GalleryName (RG: $GalleryRg), Definition: $ImageDefinitionName" -ForegroundColor Magenta
        }
    } catch {
        Write-Error "Fehler bei SIG-Auswahl/Erstellung: $($_.Exception.Message)"
        throw
    }
}

# Validierung der finalen Parameter
if ([string]::IsNullOrWhiteSpace($GalleryName) -or [string]::IsNullOrWhiteSpace($GalleryRg) -or [string]::IsNullOrWhiteSpace($ImageDefinitionName)) {
    throw "GalleryName, GalleryRg und ImageDefinitionName müssen alle gesetzt sein."
}

# --- Quell-VM & Eigenschaften ermitteln ---
$srcVm = Get-AzVM -Name $SourceVmName -ResourceGroupName $SourceVmRg -ErrorAction Stop
if ($srcVm.StorageProfile.OsDisk.OsType -ne 'Windows') {
    throw "Die Quell-VM muss Windows als OS haben."
}

$srcOsDiskId = $srcVm.StorageProfile.OsDisk.ManagedDisk.Id
$srcGen = $srcVm.HardwareProfile.VmSize  # nur Info
$secProfile = $srcVm.SecurityProfile
$uefi = $srcVm.UefiSettings

$useTrustedLaunch = $false
if ($secProfile -and $secProfile.SecurityType -eq 'TrustedLaunch') { $useTrustedLaunch = $true }

Write-Host "Quelle: VM '$($srcVm.Name)' (RG: $SourceVmRg) | OS-Disk: $srcOsDiskId | TrustedLaunch: $useTrustedLaunch" -ForegroundColor Cyan

# --- Snapshot erstellen ---
$snapName = New-Name -Prefix "snap-os-"
$snapConfig = New-AzSnapshotConfig -Location $Location -CreateOption CopyStart -SourceUri $srcOsDiskId
$snapshot = New-AzSnapshot -Snapshot $snapConfig -SnapshotName $snapName -ResourceGroupName $StagingRg
Write-Host "Snapshot erstellt: $($snapshot.Id)" -ForegroundColor Green

# --- Aus Snapshot eine Staging-OS-Disk bereitstellen ---
$diskName = New-Name -Prefix "osdisk-stg-"
$diskConfig = New-AzDiskConfig -SkuName 'Standard_LRS' -Location $Location -CreateOption Copy -SourceResourceId $snapshot.Id
$stagingDisk = New-AzDisk -Disk $diskConfig -ResourceGroupName $StagingRg -DiskName $diskName
Write-Host "Staging-OS-Disk erstellt: $($stagingDisk.Id)" -ForegroundColor Green

# --- Netzwerkobjekte für Staging-VM ---
$vnet  = Get-AzVirtualNetwork -Name $VnetName -ResourceGroupName $VnetRg -ErrorAction Stop
$subnet = $vnet.Subnets | Where-Object { $_.Name -eq $SubnetName }
if (-not $subnet) { throw "Subnet '$SubnetName' im VNet '$VnetName' nicht gefunden." }

$nicName = New-Name -Prefix "nic-stg-"
$nic = New-AzNetworkInterface -Name $nicName -ResourceGroupName $StagingRg -Location $Location -SubnetId $subnet.Id

# --- Staging-VM aus vorhandener OS-Disk zusammenbauen ---
$vmName = New-Name -Prefix "imgstg-"
$vmCfg  = New-AzVMConfig -VMName $vmName -VMSize $StagingVmSize

# Sicherheitsprofil wie Quelle
if ($useTrustedLaunch) {
    $vmCfg = Set-AzVMSecurityProfile -VM $vmCfg -SecurityType "TrustedLaunch"
    $vmCfg = Set-AzVMUefi -VM $vmCfg -EnableVtpm $true -EnableSecureBoot $true
} else {
    $vmCfg = Set-AzVMSecurityProfile -VM $vmCfg -SecurityType "Standard"
    $vmCfg = Set-AzVMUefi -VM $vmCfg -EnableVtpm $false -EnableSecureBoot $false
}

# OS-Disk anhängen (Attach)
$vmCfg = Set-AzVMOSDisk -VM $vmCfg -ManagedDiskId $stagingDisk.Id -CreateOption Attach -Windows
$vmCfg = Add-AzVMNetworkInterface -VM $vmCfg -Id $nic.Id -Primary
$vmCfg = Set-AzVMBootDiagnostic -VM $vmCfg -Enable

# Hinweis: Kein OSProfile mit Credentials nötig, da wir vorhandene Disk anhängen.
New-AzVM -ResourceGroupName $StagingRg -Location $Location -VM $vmCfg -ErrorAction Stop | Out-Null
Write-Host "Staging-VM erstellt: $vmName" -ForegroundColor Green

# --- Power on & Sysprep per RunCommand ---
Start-AzVM -Name $vmName -ResourceGroupName $StagingRg | Out-Null
Wait-ForVmPowerState -VmName $vmName -VmRg $StagingRg -DesiredState 'VM running' -TimeoutSec 1800 | Out-Null

$sysprepScript = @'
# Stop ggf. laufende Windows-Updates, Defender-Scan etc. rudimentär:
Stop-Service -Name wuauserv -ErrorAction SilentlyContinue
Stop-Service -Name UsoSvc -ErrorAction SilentlyContinue

# Optional: Appx-Provisionings bereinigen kann den Sysprep stabilisieren (vorsichtig einsetzen):
# Get-AppxProvisionedPackage -Online | Remove-AppxProvisionedPackage -Online -AllUsers -ErrorAction SilentlyContinue | Out-Null

# Sysprep ausführen
$sysprep = "$env:SystemRoot\System32\Sysprep\Sysprep.exe"
if (-not (Test-Path $sysprep)) { throw "Sysprep nicht gefunden." }

# /mode:vm hilft bei VM-Images, /oobe setzt zurück, /generalize entfernt SIDs, /shutdown fährt danach herunter
& $sysprep /generalize /oobe /shutdown /mode:vm
$rc = $LASTEXITCODE
if ($rc -and $rc -ne 0) { throw "Sysprep Rückgabecode: $rc" }
'@

Write-Host "Starte Sysprep in Staging-VM (dies kann einige Minuten dauern)..." -ForegroundColor Cyan
$rcOut = Invoke-AzVMRunCommand -ResourceGroupName $StagingRg -Name $vmName -CommandId 'RunPowerShellScript' -ScriptString $sysprepScript -ErrorAction Stop
$rcOut.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }

# Warten bis heruntergefahren
Wait-ForVmPowerState -VmName $vmName -VmRg $StagingRg -DesiredState 'VM stopped' -TimeoutSec 3600 | Out-Null
Stop-AzVM -Name $vmName -ResourceGroupName $StagingRg -Force -NoWait | Out-Null
Wait-ForVmPowerState -VmName $vmName -VmRg $StagingRg -DesiredState 'VM deallocated' -TimeoutSec 1800 | Out-Null

# --- VM als generalisiert markieren ---
Set-AzVM -ResourceGroupName $StagingRg -Name $vmName -Generalized | Out-Null
Write-Host "Staging-VM wurde generalisiert." -ForegroundColor Green

# --- Managed Image aus Staging-VM ---
$imgNameManaged = "mi-$($vmName)"
$imgCfg = New-AzImageConfig -Location $Location
$imgCfg = Set-AzImageOsDisk -Image $imgCfg -OsType Windows -OsState Generalized -ManagedDiskId $stagingDisk.Id
# Alternativ: -SourceVirtualMachineId $((Get-AzVM -Name $vmName -ResourceGroupName $StagingRg -Status).Id)
$managedImage = New-AzImage -Image $imgCfg -ImageName $imgNameManaged -ResourceGroupName $StagingRg
Write-Host "Managed Image erstellt: $($managedImage.Id)" -ForegroundColor Green

# --- Gallery-Image-Definition prüfen ---
$imgDef = Get-AzGalleryImageDefinition -ResourceGroupName $GalleryRg -GalleryName $GalleryName -Name $ImageDefinitionName -ErrorAction Stop

# --- TargetRegions-Objekte bauen ---
$regionObjs = @()
foreach ($r in $TargetRegions) {
    $regionObjs += New-AzGalleryReplicationRegion -Name $r -ReplicaCount $ReplicaCount -StorageAccountType $StorageAccountType
}
if ($regionObjs.Count -eq 0) {
    # mindestens Primärregion verwenden
    $regionObjs += New-AzGalleryReplicationRegion -Name $Location -ReplicaCount $ReplicaCount -StorageAccountType $StorageAccountType
}

# --- Image Version erstellen ---
$pubProfileParams = @{
    GalleryImageDefinitionName = $imgDef.Name
    GalleryName                = $GalleryName
    ResourceGroupName          = $GalleryRg
    Name                       = $ImageVersion
    Location                   = $Location
    PublishingProfileReplicaCount = $ReplicaCount
    TargetRegion               = $regionObjs
    SourceImageId              = $managedImage.Id
}
if ($ExcludeFromLatest) { $pubProfileParams["ExcludeFromLatest"] = $true }
if ($EndOfLife)          { $pubProfileParams["EndOfLifeDate"]     = [datetime]::Parse($EndOfLife) }

Write-Host "Erzeuge Gallery Image Version '$ImageVersion' ..." -ForegroundColor Cyan
$galleryVersion = New-AzGalleryImageVersion @pubProfileParams
Write-Host "Gallery Image Version erstellt: $($galleryVersion.Id)" -ForegroundColor Green

# --- Optionales Aufräumen ---
if ($CleanUp) {
    Write-Host "Bereinige Staging-Ressourcen ..." -ForegroundColor Cyan
    # Staging-VM (löscht NIC-Referenz)
    try { Remove-AzVM -Name $vmName -ResourceGroupName $StagingRg -Force -ErrorAction Stop } catch {}
    # NIC
    try { Remove-AzNetworkInterface -Name $nicName -ResourceGroupName $StagingRg -Force -ErrorAction Stop } catch {}
    # Managed Image (nachdem die Gallery-Version erstellt wurde, kann es entfernt werden)
    try { Remove-AzImage -ResourceGroupName $StagingRg -ImageName $imgNameManaged -Force -ErrorAction Stop } catch {}
    # OS-Disk
    try { Remove-AzDisk -ResourceGroupName $StagingRg -DiskName $diskName -Force -ErrorAction Stop } catch {}
    # Snapshot
    try { Remove-AzSnapshot -ResourceGroupName $StagingRg -SnapshotName $snapName -Force -ErrorAction Stop } catch {}

    Write-Host "Bereinigung abgeschlossen." -ForegroundColor Green
}

Write-Host "Fertig. Neue Gallery-Version: $ImageVersion" -ForegroundColor Magenta