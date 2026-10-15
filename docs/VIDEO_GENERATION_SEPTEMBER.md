# 影片生成 v1 — 多工具導演流程 + 啟用清單

目標：影片是**多工具協作**，由 Claude 當導演、Veo 3 出影片，其餘為配角（缺 API 就跳過）：

| 角色 | 工具 | 狀態 |
|------|------|------|
| 劇本 / 分鏡 / 旁白 / 鏡頭設計 | **Claude** | 核心，永遠執行（不需任何影片 API） |
| 關鍵畫面 / 風格定義 | Flux / Midjourney | 配角；Claude 已產出 keyframe prompt + 風格，接 API 後出圖 |
| **影片生成** | **Veo 3**（優先）/ Sora（fallback） | 休眠，給金鑰啟用 |
| 旁白配音 / 角色聲音 | ElevenLabs | 配角，缺 API 跳過 |
| 配樂 | Suno | 配角，缺 API 跳過 |

重點：**即使所有影片 API 休眠，使用者仍會拿到 Claude 產出的完整影片計畫**（劇本/分鏡/旁白/鏡頭/風格 + 工具分工表），
這是流程的核心價值。Veo 3 / Sora 啟用後，會用 Claude 計畫合成的 prompt 去生成影片。

## 多工具流程（AgentRuntime.GenerateVideoFile）

1. **Claude 導演**：`VideoPlanBuilder.BuildDirectorPrompt` → 透過既有 executor 呼叫 Claude（claude-sonnet-4-6）
   → `VideoPlanBuilder.Parse` 解析 JSON 成 `VideoPlanPayload`（title/logline/style/music_brief/scenes[]/video_prompt）。
   解析失敗退回最小計畫，不中斷。產物為 user-visible workspace artifact `video_plan`。
2. **配角**：`ElevenLabsNarrationService` / `SunoMusicService` 以 `IsConfigured` 判斷，缺金鑰記為「略過（無 API）」。
3. **影片產生器**：`VeoVideoService`（優先）→ `OpenAIVideoService`（Sora，fallback）→ 皆休眠則只交付計畫。
   工具分工與各自狀態記在 `VideoPlanPayload.ProviderRoles`，並摘要進最終答案。

## 已完成（2026-06-14，scaffolding）

- `VideoGenerationStatus.cs`：Queued / Generating / Completed / Failed / Canceled + 中文標籤。
- `VideoRequestPayload.cs`：「prompt → 影片請求」artifact（jobId / status / progress / 落地檔案路徑），user-visible。
- `OpenAIVideoService.cs`：影片 API client
  - `IsConfigured`：需 `OPENAI_API_KEY` **且** `CAT5201_VIDEO_ENABLED=1`（否則休眠）。
  - `GenerateAsync(prompt, seconds, size, onProgress, ct)`：建立 → 輪詢（每 5 秒、上限 ~10 分鐘）→ 下載 mp4。
  - 取消：`OperationCanceledException` 上拋，並 best-effort `POST /v1/videos/{id}/cancel`。
- `GeneratedFileWriter.WriteVideo()`：寫入 `_generated/*.mp4`。
- `OrchestrationPlanner`：VideoGeneration 任務加 `generate_video` 階段。
- `AgentRuntime.GenerateVideoFile()`：在最終答案後執行影片任務，更新進度、寫檔、加 workspace artifact。
- `NodeControl.SetLoadingHint()`：長任務即時進度（「影片生成中 N%」+ 已等待秒數）。
- UI：完成的 mp4 以可點擊檔案 chip 顯示，點擊用系統預設播放器開啟（`OpenGeneratedFile`，限 `_generated`）。

## 啟用步驟 — Veo 3（主要影片產生器）

端點已對官方文件校正（ai.google.dev/gemini-api/docs/video，2026-06），並以 `GET /v1beta/models` 確認金鑰可見 Veo 模型。

1. **環境變數**：只要設了 `GEMINI_API_KEY`（或 `GOOGLE_API_KEY`）即啟用——**安全閘門 `CAT5201_VEO_ENABLED` 已於 2026-06-14 移除**，有金鑰就會實際呼叫 Veo（會計費）。
   - 可選：`CAT5201_VEO_MODEL`（預設 `veo-3.0-generate-001`；其他：`veo-3.1-generate-preview` / `veo-3.0-fast-generate-001`）。
2. **重啟 App**（環境變數於程序啟動時讀取）。之後「生成影片」會實際呼叫 Veo。
3. **已校正的 API 形狀**（`VeoVideoService`）：
   - 建立：`POST .../models/{model}:predictLongRunning`，header `x-goog-api-key`，
     body `{instances:[{prompt}], parameters:{aspectRatio,resolution,durationSeconds,numberOfVideos}}` → 回 `{name: operationName}`。
   - 輪詢：`GET .../{operationName}`（header `x-goog-api-key`）→ `{done, response, error}`，`done==true` 完成。
   - 取得影片：`response.generateVideoResponse.generatedSamples[0].video.uri`（用 header 下載）。
4. **注意**：模型「列得出來」不等於「可生成」——Veo 生成通常需付費 / 啟用帳單的專案。
   真正確認要等第一支實際生成（會計費）。逾時 / 額度 / 失敗皆已優雅處理，不影響主答案與 Claude 計畫。

## 啟用步驟 — Sora（fallback 影片產生器）

1. 確認帳號具備 Sora 影片 API 權限。
2. 設 `CAT5201_VIDEO_ENABLED=1`（用既有 `OPENAI_API_KEY`）。可選 `CAT5201_VIDEO_MODEL`（預設 `sora-2`）。
3. 端對端驗證（`OpenAIVideoService`）：`POST /v1/videos` → `GET /v1/videos/{id}` → `GET /v1/videos/{id}/content`。

## 配角啟用（可選，缺則自動略過）

- **ElevenLabs 旁白**：設 `ELEVENLABS_API_KEY`，並於 `ElevenLabsNarrationService` 補實際 TTS 呼叫 + 在流程接線。
- **Suno 配樂**：設 `SUNO_API_KEY`，並於 `SunoMusicService` 補 create→poll→download + 接線。
- **Flux / Midjourney 關鍵畫面**：Claude 已產出 keyframe prompt；接上對應 image API（或沿用既有 OpenAIImageService）即可出圖。

## 微調

- `targetSeconds`（Claude 計畫的目標秒數，預設 8）、`Size`（預設 720x1280 直式）在 `AgentRuntime.GenerateVideoFile`。
- 影片 prompt 取自 `VideoPlanPayload.VideoPromptForGenerator`（Claude 合成的英文 prompt），而非原始使用者輸入。

## 已知限制 / v2

- 預覽：目前點 chip 用外部播放器開啟；inline WPF MediaElement 播放屬 v2（較重、易出相依問題）。
- 成本提示：影片以字數估 token 無意義，需真實 API usage 才有意義（與圖片同，列 Later）。
- 多段 / 長影片、分鏡、配音、字幕：v2。
