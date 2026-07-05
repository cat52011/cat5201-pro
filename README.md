# cat5201-pro — AI 工作台

節點畫布 × 多代理編排的桌面 AI 工作台。串接 **OpenAI / Claude / Gemini / Perplexity**，
一句指令可以自動研究、寫作、產檔（PDF / DOCX / PPTX / XLSX）、生圖、生影片——
而且**每一步 AI 決策、模型選擇、token 成本、資料來源都攤開可稽查**。

> 差異點不是「接了很多模型」，而是「**終端使用者 × 每步可稽查**」：
> 非工程師也能看懂 AI 幫你做了什麼、花了多少錢、根據是什麼。

## 主要功能

| 能力 | 說明 |
|---|---|
| 🎨 節點畫布 | 拖拉節點組工作流，上游輸出自動餵下游；支援自動拆解多階段任務 |
| 🤖 多代理編排 | general / research / file / code / presentation / media 代理，狀態機驅動，工作區產物全程可追 |
| 🔍 可稽查決策窗 | 每次執行顯示：任務判斷、模型選擇原因、fallback 軌跡、真實 token / 成本、記憶召回 |
| 📄 檔案生成 | Markdown → PDF（QuestPDF）/ DOCX / PPTX / XLSX（OpenXML），繁中字型正常 |
| 🖼️ 圖片 | gpt-image 生圖 / 改圖；簡報自動配圖 |
| 🎬 影片 | Veo 3.1 文生影片：連貫模式（原生延伸最長 148 秒）與快剪模式（ffmpeg 拼接） |
| 🧠 記憶 | 手動沉澱偏好與記憶，跨專案生效，衝突時節點優先 |
| 📱 手機鏡像 | 內嵌 web server + QR 掃碼連線：手機即時看進度、停止 / 重跑節點、回答產檔確認 |
| 💰 成本控制 | 附件本機抽文字 + 快取、接續自然停止、Auto 模式高價模型封鎖開關 |

## 快速開始

1. **需求**：Windows 10/11。開發需 .NET 8 SDK；使用打包版則免裝任何東西。
2. **金鑰**：啟動後點左下角 ⚙ → 「API」分頁，填入你的 OpenAI / Claude / Gemini / Perplexity 金鑰
   （BYO-key，金鑰以 Windows DPAPI 加密存於本機，不上傳）。
3. **執行（開發）**：
   ```powershell
   dotnet run --project cat5201.csproj
   ```
4. **打包（商品級單一 exe，目標機免裝 .NET）**：
   ```powershell
   .\publish.ps1
   # 產出 publish\win-x64\cat5201.exe
   ```
5. **測試**：
   ```powershell
   dotnet test tests\cat5201.Tests\cat5201.Tests.csproj
   ```

## 架構（一個核心 × 三介面）

```
cat5201.Core/     UI 無關核心（net8.0）：模型註冊/路由/成本、編排規劃、決策記錄、
                  輸出意圖、共用 DTO（快照/指令）—— 桌面、web、手機共用的單一真相
cat5201.csproj    WPF 桌面前端（net8.0-windows）：畫布、節點、決策窗、
                  內嵌 Kestrel 手機鏡像 server（唯讀 + 輕操控）
tests/            xUnit 測試（涵蓋輸出偵測、成本估算、模型註冊、儲存 round-trip、
                  意圖閘門、影片參數、個人化向後相容）
```

執行核心（AgentRuntime）只依賴 `IAgentHost` / `INodeContext` 抽象，不認識任何 WPF 型別——
這是往 web / 行動完整客戶端演進的地基。

## 診斷

- 未預期錯誤不會讓程式無聲消失：全域例外處理會記錄並提示。
- 日誌位置：`%LOCALAPPDATA%\cat5201-pro\logs\app-*.log`（自動保留 30 天）。

## 版本

版本號單一真相在 `cat5201.csproj` 的 `<Version>`；標題列與檔案內容頁自動顯示。
