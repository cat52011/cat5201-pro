# 第三方元件授權聲明（THIRD-PARTY NOTICES）

本產品使用下列第三方套件／元件。各元件版權歸其作者所有，依其原授權條款使用。

## NuGet 套件

| 套件 | 授權 | 備註 |
|---|---|---|
| QuestPDF | **QuestPDF Community License** | ⚠️ 免費使用之條件：年營收低於 100 萬美元之個人/組織。**若本產品商業化且營收超標，須改購商業授權。**（PDF 產出引擎） |
| PdfPig (UglyToad.PdfPig) | Apache-2.0 | PDF 文字抽取（附件成本優化） |
| DocumentFormat.OpenXml | MIT | DOCX / PPTX / XLSX 產出 |
| Markdig | BSD-2-Clause | Markdown 解析 |
| OpenAI (.NET SDK) | MIT | OpenAI API 用戶端 |
| QRCoder | MIT | 手機鏡像 QR 碼 |
| Microsoft.Web.WebView2 | Microsoft 專有（可再散布） | 內嵌網頁檢視 |
| System.Security.Cryptography.ProtectedData | MIT | 金鑰 DPAPI 加密 |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | 僅測試專案，不隨產品散布 |

## 執行階段 / 框架

- **.NET 8 Runtime、WPF、ASP.NET Core**：MIT（Microsoft）。自含式打包時隨附。

## 外部程式（使用者自行安裝，僅以行程呼叫）

- **ffmpeg**（影片快剪拼接）：LGPL/GPL（視 build 而定；經 winget 安裝的 Gyan.FFmpeg 為 GPL build）。
  本產品**不內嵌、不散布** ffmpeg，僅在使用者已自行安裝時以獨立行程呼叫。

## 字型

- **Microsoft JhengHei（微軟正黑體）**：隨 Windows 授權。PDF 產出時嵌入子集以確保繁中顯示，
  僅限於在已授權之 Windows 環境產生之文件。

## 外部 API 服務（使用者自備金鑰，BYO-key）

OpenAI / Anthropic Claude / Google Gemini（含 Veo）/ Perplexity / （選用）Kling、Gamma、Google Drive。
使用者輸入之內容（prompt、附件文字）會依使用者的操作送往上述服務，
受各服務商之服務條款與隱私政策約束；本產品不代管、不上傳金鑰。
