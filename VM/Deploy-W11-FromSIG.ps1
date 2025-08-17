<#PSScriptInfo
.VERSION        0.1.0
.GUID           94c392be-bd22-4a55-af15-b4fbba9b9d84
.AUTHOR         Jörg Brors
.COMPANYNAME    
.COPYRIGHT      (c) 2025 Jörg Brors. All rights reserved.
.TAGS           AVD,AzureSharedImageGallery,Windows11,TrustedLaunch,VMDeployment,AcceleratedNetworking,PowerShell
.LICENSEURI     https://opensource.org/licenses/MIT
.PROJECTURI     https://github.com/joergbrors/AVD
.ICONURI        
.EXTERNALMODULEDEPENDENCIES Az.Accounts,Az.Compute,Az.Network,Az.Resources
.REQUIREDSCRIPTS
.EXTERNALSCRIPTDEPENDENCIES
.DESCRIPTION    Deploy-W11-FromSIG creates and configures a Windows 11 VM from a Shared Image Gallery (SIG) for Azure Virtual Desktop (AVD) with Trusted Launch and optional post-install steps.
.RELEASENOTES
    0.1.0 - Initial release (created with assistance from ChatGPT).
#>
#Requires -Modules Az.Accounts, Az.Compute, Az.Network, Az.Resources
<#
.SYNOPSIS
    Deploys a Windows 11 virtual machine from a Shared Image Gallery (SIG). Supports Gen2 Trusted Launch
    (Secure Boot + vTPM), accelerated networking where available, boot diagnostics and a one-time EFI
    bootloader repair via RunCommand. Includes name sanitization for Windows/NetBIOS constraints and optional
    post-install script execution.

.DESCRIPTION
    This script automates the creation of a Windows 11 VM sourced from a Shared Image Gallery image version.
    It will:
      - Locate the specified Shared Image Gallery (or auto-detect it in the subscription).
      - Prefer image versions replicated to the target region (TargetRegions).
      - Optionally operate non-interactively when ImageDefinitionName and ImageVersionName are supplied.
      - Create the VM (Gen2 when Trusted Launch is enabled), attach to the specified VNet/Subnet, configure
        NIC/accelerated networking when supported, and enable boot diagnostics.
      - Optionally perform a one-time EFI bootloader repair using bcdboot via Azure RunCommand (unless skipped).
      - Optionally run a provided post-install script and set the VM timezone.
      - Sanitize the provided VM name to comply with Windows computer name and NetBIOS limits (≤15 chars,
        allowed characters) unless an override is provided.

.PARAMETER SubscriptionId
    Azure subscription ID to use for the deployment. If not provided, the current subscription context is used.

.PARAMETER Location
    Azure region (e.g. "westeurope") used as the deployment target and for filtering image TargetRegions.

.PARAMETER RgTarget
    Resource group where the VM, NIC and associated resources will be created. Required for deployment.

.PARAMETER RgNetwork
    (Optional) Resource group of the existing virtual network. If omitted, the script will search for the VNet
    name across the subscription (or use the target RG if appropriate).

.PARAMETER VnetName
    Name of the virtual network to attach the VM's NIC to.

.PARAMETER SubnetName
    Name of the subnet within the VNet for the NIC.

.PARAMETER VmName
    Desired base name for the VM. Will be sanitized and used for resource names and Windows computer name
    unless ComputerNameOverride is provided.

.PARAMETER ComputerNameOverride
    Optional explicit Windows computer name. Will be sanitized to valid Windows/NetBIOS form. If omitted the
    sanitized VmName is used.

.PARAMETER VmSize
    VM size to deploy (for example "Standard_D8ds_v5"). Must be supported in the target region. Trusted Launch
    and accelerated networking capabilities depend on the chosen size.

.PARAMETER Tags
    Hashtable of tags to apply to the VM and NIC (e.g. @{ Owner = 'Alice'; Project = 'Test' }).

.PARAMETER GalleryResourceGroup
    (Optional) Resource group of the Shared Image Gallery. If not supplied, the gallery will be searched for
    across the subscription by name.

.PARAMETER GalleryName
    Name of the Shared Image Gallery containing the desired image definitions and versions.

.PARAMETER ImageDefinitionName
    (Optional) Exact image definition name to select non-interactively. When supplied, the script will not prompt
    to choose a definition.

.PARAMETER ImageVersionName
    (Optional) Exact image version name (must correspond to the chosen ImageDefinitionName) for non-interactive use.

.PARAMETER EnableTrustedLaunch
    Switch or boolean to enable Trusted Launch (Gen2 VM with Secure Boot and vTPM). Default: $true.
    Set to $false to create a Gen1 (legacy BIOS) VM when the image supports it.

.PARAMETER AdminCredential
    PSCredential object for the local administrator account on the new VM. If omitted, the script will prompt
    for credentials.

.PARAMETER SkipBootFix
    Switch to suppress the post-deployment EFI bootloader repair (bcdboot). Use when the image does not require it.

.PARAMETER Force
    Skip interactive confirmation prompts before creating the VM. Useful for automation.

.PARAMETER PostInstallScriptPath
    Path to an optional script that will be uploaded and executed on the VM after the timezone is configured.

.PARAMETER TimeZone
    Time zone ID to set on the VM prior to running post-install tasks. Default: "W. Europe Standard Time".

.PARAMETER MultiSessionHost
    Switch indicating the VM should be prepared as a multi-session host. Default behavior = $true (treat as multi-session).

.PARAMETER ForceRestart
    Switch to automatically restart the VM at the end of the script execution without prompting.

.PARAMETER ForceStop
    Switch to automatically stop/deallocate the VM at the end of the script execution without prompting.

.EXAMPLE
    # Non-interactive: specify gallery, image definition and image version, provide credentials and tags
    $cred = Get-Credential
    .\Deploy-W11-FromSIG.ps1 -SubscriptionId "00000000-0000-0000-0000-000000000000" `
        -Location "westeurope" -RgTarget "rg-vm" -VnetName "prod-vnet" -SubnetName "snet-app" `
        -VmName "W11-APP-01" -ImageDefinitionName "win11-enterprise" -ImageVersionName "1.2.0" `
        -VmSize "Standard_D8ds_v5" -AdminCredential $cred -Tags @{Project='Demo'} -Force

.EXAMPLE
    # Interactive: auto-detect gallery, choose definition/version via prompts, use Trusted Launch (default)
    .\Deploy-W11-FromSIG.ps1 -SubscriptionId "00000000-0000-0000-0000-000000000000" `
        -Location "westeurope" -RgTarget "rg-vm" -VnetName "prod-vnet" -SubnetName "snet-app" -VmName "W11-INT-01"

.EXAMPLE
    # Disable Trusted Launch and skip the EFI boot fix (for images that don't require it)
    .\Deploy-W11-FromSIG.ps1 -SubscriptionId "..." -Location "westeurope" -RgTarget "rg-vm" `
        -VnetName "vnet1" -SubnetName "snet1" -VmName "W11-NoTL" -EnableTrustedLaunch $false -SkipBootFix -Force

.EXAMPLE
    # Provide an explicit, sanitized computer name, run a post-install script and restart automatically
    $cred = Get-Credential
    .\Deploy-W11-FromSIG.ps1 -SubscriptionId "..." -Location "westeurope" -RgTarget "rg-vm" `
        -VnetName "vnet1" -SubnetName "snet1" -VmName "base-name" -ComputerNameOverride "DESKTOP01" `
        -AdminCredential $cred -PostInstallScriptPath "C:\scripts\setup-roles.ps1" -ForceRestart -Force

.INPUTS
    None from the pipeline. Parameters accept strings, hashtables and PSCredential objects.

.OUTPUTS
    Writes details of the created Azure resources to the host and returns the VM representation returned by
    the Az.Compute creation call (PS custom/VM object). Additional information may be written to verbose/debug
    streams.

.NOTES
    - Requirements:
        * Windows PowerShell 5.1 (the script uses modules compatible with PowerShell 5.1).
        * Az modules installed and authenticated: Az.Accounts, Az.Compute, Az.Network, Az.Resources.
        * Sufficient RBAC permissions to read Shared Image Gallery contents and create NICs, VMs, public IPs
          and diagnostic storage in the target subscription/resource groups.
    - Trusted Launch requires a Gen2-capable image and supported VM sizes and regions. If enabled, the VM will
      be deployed as Generation 2 with Secure Boot and vTPM.
    - Accelerated Networking (ENA) will be enabled only if both the VM size and the target NIC/region support it.
    - The script performs a one-time bootloader repair using bcdboot via Azure RunCommand to fix EFI boot issues
      for some SIG-to-VM scenarios; this can be suppressed with -SkipBootFix.
    - The script sanitizes names to conform to Windows computer name and NetBIOS limits (no more than 15 characters,
      allowed characters), and fails early if a usable name cannot be derived.
    - If using PostInstallScriptPath, ensure the script is accessible from the machine running this deployment and
      that the VM's RunCommand execution policy and prerequisites are satisfied.

.LINK
    https://learn.microsoft.com/azure/virtual-machines/
    https://learn.microsoft.com/azure/virtual-machines/trusted-launch
    https://learn.microsoft.com/azure/virtual-machines/windows/shared-images

.AUTHOR
    Jörg Brors
    (Documentation and enhancements assisted by GitHub Copilot)
#>
param (
    [string]$SubscriptionId           = "<your-subscription-id-here>",
    [string]$Location                 = "westeurope",
    [string]$RgTarget                 = "rg-vm",
    [string]$RgNetwork                = "<your-rg-network-here>",
    [string]$VnetName                 = "<your-vnet-name-here>",
    [string]$SubnetName               = "<your-subnet-name-here>",
    [string]$VmName                   = "<your-vm-name-here>",
    [string]$ComputerNameOverride     = "",

    [string]$VmSize                     = "Standard_D8ds_v5",

    [hashtable]$Tags                    = @{ "Workload"="AVD"; "Stage"="GoldImage"; "Usage"="PROD" },

    [string]$GalleryResourceGroup       = "",
    [string]$GalleryName                = "your-gallery-name-here",

    [string]$ImageDefinitionName  = "",
    [string]$ImageVersionName     = "",

    [bool]$EnableTrustedLaunch    = $true,

    [System.Management.Automation.PSCredential]$AdminCredential,

    [switch]$SkipBootFix,
    [switch]$Force,

    # New parameters for post-install script & time zone
    [string]$PostInstallScriptPath = "",
    [string]$TimeZone = "W. Europe Standard Time", # Default time zone, can be adjusted 

    # New switch: MultiSessionHost (default behavior = $true)
    [switch]$MultiSessionHost,

    # New switches: automatic final action without interactive prompt
    [switch]$ForceRestart,
    [switch]$ForceStop
)

# Optional: enforce TLS 1.2 (helps with TLS/proxy issues on PS 5.1)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ===========================
# Helper functions
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
            throw "Gallery '$GalleryName' was not found in resource group '$ResourceGroup'. Error: $($_.Exception.Message)"
        }
    }
    $allGalleries = Get-AzGallery -ErrorAction Stop | Where-Object { $_.Name -eq $GalleryName }
    if (-not $allGalleries -or $allGalleries.Count -eq 0) {
        throw "No gallery named '$GalleryName' found in the current subscription context."
    }
    if ($allGalleries.Count -eq 1) {
        $one = $allGalleries[0]
        return [pscustomobject]@{ Gallery = $one; ResourceGroupName = $one.ResourceGroupName }
    }
    Write-Host "`nMultiple galleries with name '$GalleryName' found:" -ForegroundColor Cyan
    $idx = 0
    foreach ($g in $allGalleries) {
        $idx++
        Write-Host ("[{0}] RG: {1} | Location: {2} | Id: {3}" -f $idx, $g.ResourceGroupName, $g.Location, $g.Id)
    }
    $sel = Read-Host "Please choose a number (Enter = 1)"
    if ([string]::IsNullOrWhiteSpace($sel)) { $sel = 1 }
    if ([int]$sel -lt 1 -or [int]$sel -gt $allGalleries.Count) { throw "Invalid selection." }
    $chosen = $allGalleries[[int]$sel - 1]
    return [pscustomobject]@{ Gallery = $chosen; ResourceGroupName = $chosen.ResourceGroupName }
}

function Select-GalleryImage {
    <#
      Returns .Definition and .Version (Az objects).
      Non-interactive if -ImageDefinitionName/-ImageVersionName are provided.
      Filters versions by TargetRegions.Name == $Location (if present), otherwise warns and shows all.
    #>
    param(
        [Parameter(Mandatory)][string]$GalleryResourceGroup,
        [Parameter(Mandatory)][string]$GalleryName,
        [Parameter(Mandatory)][string]$Location,
        [string]$ImageDefinitionName,
        [string]$ImageVersionName
    )

    $defs = Get-AzGalleryImageDefinition -ResourceGroupName $GalleryResourceGroup -GalleryName $GalleryName -ErrorAction Stop
    if (-not $defs -or $defs.Count -eq 0) { throw "No image definitions found in gallery '$GalleryName' (RG: $GalleryResourceGroup)." }

    $def = $null
    if ($ImageDefinitionName) {
        $def = $defs | Where-Object { $_.Name -eq $ImageDefinitionName } | Select-Object -First 1
        if (-not $def) { throw "Image definition '$ImageDefinitionName' was not found." }
    } else {
        Write-Host "`nFound image definitions in gallery '$GalleryName':" -ForegroundColor Cyan
        $i = 0
        foreach ($d in $defs) {
            $i++
            $gen = $d.HyperVGeneration
            Write-Host ("[{0}] {1}  (Publisher: {2} | Offer: {3} | Sku: {4} | Gen: {5})" -f $i, $d.Name, $d.Publisher, $d.Offer, $d.Sku, $gen)
        }
        $selIndex = Read-Host "Please select image definition number (Enter = 1)"
        if ([string]::IsNullOrWhiteSpace($selIndex)) { $selIndex = 1 }
        if ([int]$selIndex -lt 1 -or [int]$selIndex -gt $defs.Count) { throw "Invalid selection." }
        $def = $defs[[int]$selIndex - 1]
    }

    if ($EnableTrustedLaunch -and $def.HyperVGeneration -ne "V2") {
        throw "Trusted Launch requires Gen2 (HyperVGeneration V2), but the selected definition is '$($def.HyperVGeneration)'."
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
        Write-Host ("Note: For definition '{0}' there are no versions with TargetRegion '{1}'." -f $def.Name, $Location) -ForegroundColor Yellow
        Write-Host "Showing all versions (no region filtering). Please verify replication into the target region!" -ForegroundColor Yellow
        $versions = $allVersions
    }

    # Sort: newest first
    $versions = $versions | Sort-Object -Property @{Expression = {
        if ($_.PublishingProfile -and $_.PublishingProfile.PublishedDate) { $_.PublishingProfile.PublishedDate } else { Get-Date '1900-01-01' }
    }}, @{Expression = {
        try { [version]$_.Name } catch { [version]"0.0.0" }
    }} -Descending

    $ver = $null
    if ($ImageVersionName) {
        $ver = $versions | Where-Object { $_.Name -eq $ImageVersionName } | Select-Object -First 1
        if (-not $ver) { throw "Image version '$ImageVersionName' was not found for definition '$($def.Name)' (or not in TargetRegion '$Location')." }
    } else {
        Write-Host ("`nAvailable versions for '{0}' (TargetRegion: {1}; newest first):" -f $def.Name, $Location) -ForegroundColor Cyan
        $j = 0
        foreach ($v in ($versions | Select-Object -First 10)) {
            $j++
            $pub = "—"
            if ($v.PublishingProfile -and $v.PublishingProfile.PublishedDate) { $pub = $v.PublishingProfile.PublishedDate.ToString("yyyy-MM-dd HH:mm") }
            Write-Host ("({0}) {1}  | Published: {2}" -f $j, $v.Name, $pub)
        }
        $verChoice = Read-Host "Please select version number (Enter = 1 = newest)"
        if ([string]::IsNullOrWhiteSpace($verChoice)) { $verChoice = 1 }
        $maxChoice = [Math]::Min(10, $versions.Count)
        if ([int]$verChoice -lt 1 -or [int]$verChoice -gt $maxChoice) { throw "Invalid selection." }
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
# Resolve gallery (RG auto, if empty)
# ===========================
$galleryInfo = Resolve-GalleryByName -GalleryName $GalleryName -ResourceGroup $GalleryResourceGroup
$resolvedGalleryRg = $galleryInfo.ResourceGroupName
$resolvedGallery   = $galleryInfo.Gallery

# ===========================
# Gallery selection (definition + version)
# ===========================
$choice = Select-GalleryImage -GalleryResourceGroup $resolvedGalleryRg -GalleryName $resolvedGallery.Name -Location $Location -ImageDefinitionName $ImageDefinitionName -ImageVersionName $ImageVersionName
$imgDef = $choice.Definition
$imgVer = $choice.Version

# ===========================
# Resolve network objects
# ===========================
$vnet = Get-VNetByName -Name $VnetName -Location $Location -ResourceGroup $RgNetwork
if (-not $vnet) { throw "VNet '$VnetName' was not found." }

$subnet = $vnet.Subnets | Where-Object { $_.Name -eq $SubnetName }
if (-not $subnet) { throw "Subnet '$SubnetName' in VNet '$($vnet.Name)' was not found." }

# ===========================
# Check accelerated networking support
# ===========================
$enableAccelNet = Test-AcceleratedNetworkingSupport -VmSize $VmSize -Location $Location

# ===========================
# Admin credentials (if not provided)
# ===========================
if (-not $AdminCredential) {
    $adminUser = Read-Host "Please enter the local admin username for the new VM"
    if ([string]::IsNullOrWhiteSpace($adminUser)) { throw "Admin username must not be empty." }
    $adminPass = Read-Host "Please enter password for '$adminUser'" -AsSecureString
    $AdminCredential = New-Object System.Management.Automation.PSCredential($adminUser, $adminPass)
}

# ===========================
# Sanitize names (VM resource name & Windows computer name)
# ===========================
$vmNameOriginal = $VmName
$VmName         = Get-ValidWindowsComputerName -BaseName $vmNameOriginal

$ComputerName = $VmName
if ($ComputerNameOverride) {
    $ComputerName = Get-ValidWindowsComputerName -BaseName $ComputerNameOverride
}

if ($VmName -ne $vmNameOriginal) {
    Write-Warning ("VM name adjusted due to NetBIOS rules: '{0}' -> '{1}'" -f $vmNameOriginal, $VmName)
}
if ($ComputerNameOverride -and ($ComputerNameOverride -ne $ComputerName)) {
    Write-Warning ("ComputerNameOverride adjusted to a valid name: '{0}' -> '{1}'" -f $ComputerNameOverride, $ComputerName)
}

# ===========================
# Summary
# ===========================
$securitySummary = ""
if ($EnableTrustedLaunch) { $securitySummary = "TrustedLaunch (SecureBoot + vTPM)" } else { $securitySummary = "Standard (explicit: SecureBoot/vTPM OFF)" }

# After sanitizing names determine effective MultiSessionHost (default = true if not provided)
if ($PSBoundParameters.ContainsKey('MultiSessionHost')) {
    # If switch provided, use its boolean value (e.g. -MultiSessionHost:$false possible)
    $UseMultiSessionHost = [bool]$MultiSessionHost
} else {
    # Default: true
    $UseMultiSessionHost = $true
}

# Validation: only one of ForceRestart/ForceStop may be specified
if ($ForceRestart -and $ForceStop) {
    throw "Only one of -ForceRestart or -ForceStop may be specified."
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
# Optional: zeige, ob ForceRestart/ForceStop aktiv sind
if ($ForceRestart) { Write-Host "FinalAction   : ForceRestart" -ForegroundColor Cyan }
elseif ($ForceStop) { Write-Host "FinalAction   : ForceStop" -ForegroundColor Cyan }
Write-Host "Tags         : $(Format-Tags -Tags $Tags)"
Write-Host "=========================================" -ForegroundColor Cyan

if (-not $Force) {
    $confirmation = Read-Host "Create the VM now? (Y/N)"
    if ($confirmation -notin @("Y","y","Yes","yes","J","j")) {
        Write-Host "Cancelled." -ForegroundColor Yellow
        return
    }
}

# ===========================
# Create NIC
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
# Build VM configuration
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

# Source: SIG image version
$vmConfig = Set-AzVMSourceImage -VM $vmConfig -Id $imgVer.Id

# OS type + admin (ComputerName = sanitized name or override)
$vmConfig = Set-AzVMOperatingSystem -VM $vmConfig -Windows -ComputerName $ComputerName -Credential $AdminCredential -ProvisionVMAgent

# Assign NIC
$vmConfig = Add-AzVMNetworkInterface -VM $vmConfig -Id $nic.Id -Primary

# Boot Diagnostics (Managed)
$vmConfig = Set-AzVMBootDiagnostic -VM $vmConfig -Enable

# ===========================
# Create VM
# ===========================
New-AzVM -ResourceGroupName $RgTarget -Location $Location -VM $vmConfig -Tag $Tags -ErrorAction Stop

Write-Host "VM '$VmName' was created successfully. Security mode: $securitySummary | Accelerated Networking: $enableAccelNet" -ForegroundColor Green


# Set LicenseType for Windows 10/11 Multi-Session (required for multi-session images)
try {
    if ($UseMultiSessionHost) {
        Write-Host "Setting LicenseType = 'Windows_Client' for VM '$VmName' ..." -ForegroundColor Cyan
        $vmObj = Get-AzVM -ResourceGroupName $RgTarget -Name $VmName -ErrorAction Stop
        $vmObj.LicenseType = "Windows_Client"
        Update-AzVM -ResourceGroupName $RgTarget -VM $vmObj -ErrorAction Stop
        Write-Host "LicenseType set successfully." -ForegroundColor Green
    } else {
        Write-Host "Skipping LicenseType update (MultiSessionHost disabled)." -ForegroundColor Yellow
    }
}
catch {
    Write-Warning "Could not set LicenseType: $($_.Exception.Message)"
}

# ===========================
# POST-STEP: rewrite EFI bootloader (one-time)
# ===========================
if (-not $SkipBootFix) {
    Write-Host "Starting one-time EFI bootloader repair via RunCommand ..." -ForegroundColor Cyan
    $bootFixScript = @'
# Mount EFI System Partition (ESP) as S:, rewrite boot files, unmount
# Fallback: direct bcdboot without explicit ESP path

try {
    # mount ESP
    & mountvol S: /S 2>&1 | Out-Null

    # bcdboot: /p overwrites existing entry instead of creating a new one
    & bcdboot C:\Windows /s S: /f UEFI /p /v
    $rc = $LASTEXITCODE

    # unmount ESP
    & mountvol S: /D

    if ($rc -ne 0) { throw "bcdboot with ESP drive failed (Code: $rc)." }
}
catch {
    # Fallback without explicit ESP path
    & bcdboot C:\Windows /f UEFI /p /v
    if ($LASTEXITCODE -ne 0) {
        throw "bcdboot fallback failed (Code: $LASTEXITCODE). Error: $($_.Exception.Message)"
    }
}

# Timeout = 0 to avoid showing boot menu
& bcdedit /timeout 0

Write-Output "EFI boot files were successfully recreated and boot menu cleaned."
'@
    $rcRes = Invoke-AzVMRunCommand -ResourceGroupName $RgTarget -Name $VmName -CommandId 'RunPowerShellScript' -ScriptString $bootFixScript -ErrorAction Stop
    $rcRes.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }
    Write-Host "EFI bootloader repair completed." -ForegroundColor Green

    Write-Host "Note: Restart/Deallocate choice will be prompted at the end of the script." -ForegroundColor Cyan
} else {
    Write-Host "EFI bootloader fix skipped (SkipBootFix set)." -ForegroundColor Yellow
}

# ===========================
# Set TimeZone on the VM (always, even if no post-install script provided)
# ===========================
try {
    Write-Host "Setting time zone on VM '$VmName' to '$TimeZone' ..." -ForegroundColor Cyan

    $tzScript = @"
try {
    Set-TimeZone -Id '$TimeZone'
    Write-Output 'Time zone set: $TimeZone'
} catch {
    Write-Error "Error setting time zone: $($_.Exception.Message)"
    exit 1
}
"@

    # Pass a single string to -ScriptString (some Az versions require a string, not string[])
    $tzRes = Invoke-AzVMRunCommand -ResourceGroupName $RgTarget -Name $VmName -CommandId 'RunPowerShellScript' -ScriptString $tzScript -ErrorAction Stop
    $tzRes.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }
    Write-Host "Time zone was set on VM '$VmName'." -ForegroundColor Green
}
catch {
    Write-Warning "Could not set time zone via RunCommand: $($_.Exception.Message)"
}

# ===========================
# POST-STEP: Optional post-install script execution on the VM
# The TimeZone is injected into the remote session as variable $TimeZone (string).
# ===========================
if ($PostInstallScriptPath) {
    if (-not (Test-Path -Path $PostInstallScriptPath -PathType Leaf)) {
        throw "PostInstallScript '$PostInstallScriptPath' was not found."
    }

    Write-Host "Preparing to execute post-install script: $PostInstallScriptPath" -ForegroundColor Cyan

    try {
        # Read whole script as single string so we can inject the TimeZone var and pass one string to RunCommand
        $scriptContent = Get-Content -Path $PostInstallScriptPath -Raw -ErrorAction Stop

        # Escape single quotes in the TimeZone value and inject a variable declaration at the top
        $tzEscaped = $TimeZone -replace "'", "''"
        $injection = "`$TimeZone = '$tzEscaped'`r`n"
        $fullScriptText = $injection + $scriptContent

        Write-Host "Sending script to VM '$VmName' and executing it..." -ForegroundColor Cyan
        $res = Invoke-AzVMRunCommand `
            -ResourceGroupName $RgTarget `
            -Name $VmName `
            -CommandId 'RunPowerShellScript' `
            -ScriptString $fullScriptText `
            -ErrorAction Stop

        $res.Value | ForEach-Object { if ($_.Message) { Write-Host $_.Message } }
        Write-Host "Post-install script executed on VM '$VmName'." -ForegroundColor Green
    }
    catch {
        throw "Error executing post-install script: $($_.Exception.Message)"
    }
}

# ===========================
# Final action: Restart / Deallocate / No action
# Priority:
# 1) If ForceRestart/ForceStop set -> perform that action automatically.
# 2) Else if Force set -> skip (no action).
# 3) Else interactive menu.
# ===========================
if ($ForceRestart) {
    Write-Host "ForceRestart active: initiating restart of VM '$VmName'..." -ForegroundColor Cyan
    Restart-AzVM -ResourceGroupName $RgTarget -Name $VmName -NoWait
}
elseif ($ForceStop) {
    Write-Host "ForceStop active: initiating deallocate (stop) of VM '$VmName'..." -ForegroundColor Cyan
    Stop-AzVM -ResourceGroupName $RgTarget -Name $VmName -Force -NoWait
}
elseif ($Force) {
    Write-Host "Force active: skipping final action." -ForegroundColor Yellow
}
else {
    Write-Host "" -ForegroundColor Cyan
    Write-Host "Select final action:" -ForegroundColor Cyan
    Write-Host "  [1] Restart the VM now" -ForegroundColor Cyan
    Write-Host "  [2] Deallocate (stop) the VM now" -ForegroundColor Cyan
    Write-Host "  [3] No action (exit)  (Enter = 3)" -ForegroundColor Cyan

    $finalChoice = Read-Host "Please select a number"
    if ([string]::IsNullOrWhiteSpace($finalChoice)) { $finalChoice = "3" }

    switch ($finalChoice) {
        "1" {
            Write-Host "Initiating restart of VM '$VmName'..." -ForegroundColor Cyan
            Restart-AzVM -ResourceGroupName $RgTarget -Name $VmName -NoWait
        }
        "2" {
            Write-Host "Initiating deallocate (stop) of VM '$VmName'..." -ForegroundColor Cyan
            Stop-AzVM -ResourceGroupName $RgTarget -Name $VmName -Force -NoWait
        }
        default {
            Write-Host "No final action performed." -ForegroundColor Yellow
        }
    }
}