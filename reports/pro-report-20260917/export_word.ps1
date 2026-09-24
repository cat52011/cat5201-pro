$ErrorActionPreference = 'Stop'
$reportDir = $PSScriptRoot
$reportPath = Join-Path $reportDir 'cat5201_pro_完整專題報告.docx'
$reportPdf = Join-Path $reportDir 'qa\word-render.pdf'
$wordApp = $null
$wordDoc = $null
try {
    $wordApp = New-Object -ComObject Word.Application
    $wordApp.Visible = $false
    $wordApp.DisplayAlerts = 0
    $wordDoc = $wordApp.Documents.Open($reportPath, $false, $true)
    $wordDoc.Repaginate()
    $wordDoc.ExportAsFixedFormat($reportPdf, 17)
    Write-Output ('Pages: ' + $wordDoc.ComputeStatistics(2))
    Write-Output $reportPdf
} finally {
    if ($wordDoc) { $wordDoc.Close(0) }
    if ($wordApp) { $wordApp.Quit() }
}
