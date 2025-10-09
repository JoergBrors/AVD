# Test für spezifischen Dateinamen
Write-Host "Testing specific filename: QGISProfiles_GDAL_trest_20251008-1402.zip" -ForegroundColor Green

$testFile = "QGISProfiles_GDAL_trest_20251008-1402.zip"

# Regex Pattern aus dem Code
$newPattern = "^QGISProfiles_(.+?)_(.+?)_(\d{8}-\d{4})\.zip$"
$oldPattern = "^QGISProfiles_(.+?)_(\d{8}-\d{4})\.zip$"

Write-Host "`nTesting New Format Pattern: $newPattern" -ForegroundColor Yellow
if ($testFile -match $newPattern) {
    $scenario = $matches[1]
    $version = $matches[2] 
    $timestamp = $matches[3]
    Write-Host "✅ NEW MATCH: $testFile" -ForegroundColor Green
    Write-Host "   Scenario: '$scenario'" -ForegroundColor Cyan
    Write-Host "   Version: '$version'" -ForegroundColor Cyan  
    Write-Host "   Timestamp: '$timestamp'" -ForegroundColor Cyan
} else {
    Write-Host "❌ NO MATCH (New): $testFile" -ForegroundColor Red
}

Write-Host "`nTesting Old Format Pattern: $oldPattern" -ForegroundColor Yellow
if ($testFile -match $oldPattern) {
    $version = $matches[1]
    $timestamp = $matches[2]
    Write-Host "✅ OLD MATCH: $testFile" -ForegroundColor Green
    Write-Host "   Version: '$version'" -ForegroundColor Cyan
    Write-Host "   Timestamp: '$timestamp'" -ForegroundColor Cyan
} else {
    Write-Host "❌ NO MATCH (Old): $testFile" -ForegroundColor Red
}

Write-Host "`nAnalysis:" -ForegroundColor Magenta
Write-Host "Filename parts breakdown:"
Write-Host "- Prefix: 'QGISProfiles_'"
Write-Host "- Part 1: 'GDAL'"  
Write-Host "- Part 2: 'trest'"
Write-Host "- Part 3: '20251008-1402'"
Write-Host "- Suffix: '.zip'"