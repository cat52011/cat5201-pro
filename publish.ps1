# cat5201-pro 商品級打包（§14 Package demo-ready version）
# 用法：在 repo 根目錄執行  .\publish.ps1
# 產出：publish\win-x64\cat5201.exe —— 自含式單一執行檔，目標機器**不需安裝 .NET**，複製即用。
#
# 參數說明：
#   --self-contained true      內含 .NET 8 + WPF + ASP.NET Core 執行階段（手機鏡像需要），體積較大但零依賴
#   -p:PublishSingleFile=true  打成單一 exe（原生 DLL 首次啟動自解壓）
#   -p:IncludeNativeLibrariesForSelfExtract=true  WebView2 / QuestPDF 等原生庫一併內嵌

$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "publish\win-x64"

dotnet publish (Join-Path $PSScriptRoot "cat5201.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "publish 失敗（exit $LASTEXITCODE）" }

$exe = Join-Path $out "cat5201.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "✅ 打包完成：$exe（$size MB）" -ForegroundColor Green
    Write-Host "   複製整個 publish\win-x64 資料夾到目標機器即可執行（無需安裝 .NET）。"
} else {
    throw "找不到輸出 exe：$exe"
}
