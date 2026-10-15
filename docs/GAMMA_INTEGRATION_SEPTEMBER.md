# Gamma 簡報整合 — 九月啟用清單

目標：簡報用 **Claude（產內容）+ Gamma（產設計）** 合作，取代陽春的 PptxBuilder 排版。
程式 scaffolding 已寫好且處於**休眠**狀態 —— 沒有 API key 時行為與現在完全相同（走 PptxBuilder fallback）。

## 已完成（2026-06-13，scaffolding）

- `GammaPresentationService.cs`：Gamma Generate API v1.0 client
  - `POST /generations`（`inputText` + `format=presentation` + `exportAs=pptx` + `textMode=preserve`）
  - 輪詢 `GET /generations/{id}` 直到 `status=completed/failed`（上限 5 分鐘、每 5 秒一次）
  - `DownloadExportAsync()` 下載簽章的 `exportUrl`（.pptx，約一週內有效）
  - `IsConfigured`：讀環境變數 `GAMMA_API_KEY`，未設定回 false
- `AgentRuntime.TryAddGammaPptxAsync()`：簡報任務優先走 Gamma，失敗/未啟用則回退 PptxBuilder
  - 送給 Gamma 的 `inputText` = Claude 的 final synthesis（`execution.Text`），即「Claude 內容」
  - 成功時答案附上 `線上版（Gamma）：<gammaUrl>`

## 九月要做的事

1. **開通 Gamma 帳號**：Pro / Ultra / Team / Business 任一（API key 才會出現）。
2. **產 API key**：Gamma → Settings → Members → API key → Create（格式 `sk-gamma-xxx`）。
3. **設環境變數**：`GAMMA_API_KEY=sk-gamma-xxx`（系統環境變數，與 `OPENAI_API_KEY` 同方式）。
4. **端對端驗證**（重要，scaffolding 尚未用真實 key 跑過）：
   - 跑一個簡報任務，確認 `POST /generations` 回 `generationId`、輪詢拿到 `gammaUrl` + `exportUrl`。
   - 若欄位名稱與假設不符，主要調整點在 `GammaPresentationService.ParseStatusResponse`
     （目前假設：`status` / `gammaUrl` / `exportUrl` / `credits.{deducted,remaining}`）。
   - 確認 `status` 完成值（程式接受 completed/complete/succeeded/success）與失敗值（failed/error）。
5. **可選微調**：
   - `GammaGenerationInput.ThemeId`（套指定主題，需先呼叫 `/themes` 拿 id）。
   - `numCards`（張數）目前取 `outline.RequestedSlideCount`，0 時交給 Gamma 自動決定。
   - `textMode`：目前 `preserve`（保留 Claude 內容）；若想讓 Gamma 重寫可改 `condense`/`generate`。

## 注意事項

- **每次生成扣 Gamma credit**（回應的 `credits.deducted`）；demo 前留意額度。
- Gamma 是外部服務：送出的內容會上傳到 Gamma；`gammaUrl` 為線上連結。
- `exportUrl` 簽章連結約一週過期，所以程式是**下載後存成本地 .pptx**（chip 可離線開）。
- 失敗一律 graceful fallback 到 PptxBuilder，demo 不會開天窗。
