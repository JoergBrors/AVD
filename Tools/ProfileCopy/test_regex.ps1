# Test Script für Regex-Pattern Validation
Write-Host "Testing Regex Patterns for ZIP File Recognition" -ForegroundColor Green

# Test Dateien simulieren
$testFiles = @(
    "QGISProfiles_Default_v3.0_20251008-1355.zip",
    "QGISProfiles_LTR_v3.2_20251008-1400.zip", 
    "QGISProfiles_Portable_v3.1_20251008-1405.zip",
    "QGISProfiles_Custom_v2.9_20251008-1410.zip",
    "QGISProfiles_v3.0_20251008-1355.zip",  # Altes Format
    "SomeOtherFile.zip",  # Soll nicht matchen
    "QGISProfiles_incomplete.zip"  # Soll nicht matchen
)

# Regex Pattern aus dem Code
$newPattern = "^QGISProfiles_(.+?)_(.+?)_(\d{8}-\d{4})\.zip$"
$oldPattern = "^QGISProfiles_(.+?)_(\d{8}-\d{4})\.zip$"

Write-Host "`nTesting New Format Pattern: $newPattern" -ForegroundColor Yellow
foreach ($file in $testFiles) {
    if ($file -match $newPattern) {
        $scenario = $matches[1]
        $version = $matches[2] 
        $timestamp = $matches[3]
        Write-Host "✅ MATCH: $file -> Scenario: '$scenario', Version: '$version', Timestamp: '$timestamp'" -ForegroundColor Green
    } else {
        Write-Host "❌ NO MATCH: $file" -ForegroundColor Red
    }
}

Write-Host "`nTesting Old Format Pattern: $oldPattern" -ForegroundColor Yellow
foreach ($file in $testFiles) {
    if ($file -match $oldPattern) {
        $version = $matches[1]
        $timestamp = $matches[2]
        Write-Host "✅ MATCH (Old): $file -> Version: '$version', Timestamp: '$timestamp'" -ForegroundColor Green
    } else {
        Write-Host "❌ NO MATCH (Old): $file" -ForegroundColor Red
    }
}

Write-Host "`nExpected Results:" -ForegroundColor Cyan
Write-Host "- New format should match first 4 files" 
Write-Host "- Old format should match the 5th file (v3.0)"
Write-Host "- Last 2 files should not match either pattern"