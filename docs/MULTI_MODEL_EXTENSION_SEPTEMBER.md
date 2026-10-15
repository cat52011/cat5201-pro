# Multi-Model 擴充 — 加新模型 / 新 Provider 清單

目標：能在**不改動 orchestration 核心**的前提下加入新模型；新增「同一家既有 provider 的模型」零核心改動，
新增「全新 provider（如 Gemini）」只需固定幾個擴充點。Gemini 已寫成**休眠擴充點**（`IsAvailable=false`），
給金鑰並實作 provider 前不會出現在選單，也不會被 fallback / capability 重導選到，行為與現在完全相同。

## 架構（2026-06-14，§10 已完成）

- **統一 registry**：`AiModelRegistry`（`All` / `Available` / `Find` / `WithCapability` / `CheapestWithCapability`）。
- **統一 provider 介面**：`IAiProvider`（`Kind` / `Supports` / `GenerateAsync` / `GenerateStreamAsync`）。
- **能力標籤**：`AiModelCapability` flags = Streaming / Images(視覺輸入) / Files / Search / LongContext / ImageGeneration / VideoGeneration / Code。
- **成本層級**：`AiCostTier`（Economy / Standard / Premium），顯示於模型選單徽章與決策窗 Model 步驟。
- **依能力＋成本的挑選**：
  - `AiCapabilityGuard`：當模型缺所需能力時，從 `Available` 依成本由便宜到貴挑出第一個具備該能力者。
  - `AiFallbackPlanner`：same-provider → task-preferred → default → 最後手段（`Available` 依成本排序）。
- **UI**：`ModelSelector` 來源改為 `AiModelRegistry.Available`；item 顯示成本徽章 + 能力 tooltip。

## 情境 A：加「既有 provider 的新模型」（零核心改動）

只要在 `AiModelRegistry._all` 加一筆 `AiModelDefinition`：填 `Id` / `DisplayName` / `Provider`
/ `Capabilities` / `CostTier` / `ServiceModel`。`IsAvailable` 預設 true 即會出現在選單、被 fallback 納入。
（如有需要，於 `ModelCostEstimator.PriceTable` 補單價，否則走 `DefaultPrice`。）

## 情境 B：加「全新 provider」（以 Gemini 為例）

擴充點固定為以下幾處：

1. **enum 值**（已先加好）：`AiProviderType.Gemini`、`AiProviderKind.Gemini`。
2. **實作 provider**：新檔 `GeminiProvider : IAiProvider`（`Kind => AiProviderKind.Gemini`），
   參考 `OpenAiProvider` / `ClaudeProvider` 的 `GenerateAsync` / `GenerateStreamAsync` 寫法。
3. **底層 service**：新檔 `GeminiChatService`（呼叫 Gemini API，讀 `GEMINI_API_KEY`）。
4. **router 接線**：`AiServiceRouter`
   - `GetProvider(route)` switch 加 `AiProviderKind.Gemini => GetGeminiProvider()`。
   - 加 `_geminiProvider` 欄位 + `GetGeminiProvider()` + `GetGeminiService()` + `EnsureServiceReady` case + `WarmupSafely`。
5. **啟用模型**：把 registry 內 `gemini-2.5-pro` 的 `IsAvailable` 改 `true`
   （`NormalizeNodeModel` 會在 `IsAvailable=false` 時退回預設模型，所以改 true 才會真的路由）。
6. **資產**：放 `Assets/Gemini_logo.png`（registry 已引用此路徑）。
7. **單價**（可選）：`ModelCostEstimator.PriceTable["gemini-2.5-pro"]`。

完成 2–6 後，Gemini 會自動出現在模型選單、被 capability 重導與 fallback 依「能力＋成本」納入考量，
**不需要改 orchestration / AgentRuntime / NodeService 任何一行**。

## 何時該真的加 Gemini / 其他專屬 AI（建議）

詳見對話中的判斷；摘要：**九月 demo 前不建議真接 Gemini**——OpenAI + Claude + Perplexity 已覆蓋
文字 / 視覺 / 搜尋 / 生成圖片，第 4 家 provider 增加金鑰、SDK、錯誤處理、額度管理與測試成本，
對 capstone demo 的邊際價值低。先留休眠擴充點，等出現「現有模型做不到、且 demo 真的需要」的能力
（例：超長影片生成、特定語言/在地化、極低成本大量呼叫）再啟用。
