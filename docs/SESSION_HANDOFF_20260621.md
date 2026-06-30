# Session Handoff — 2026-06-21

> 讀完程式碼後讀這份。這裡只寫「從程式碼看不出來的東西」。

---

## 環境與路徑規則（絕對不能違反）

- `C:\claudework\cat5201` → Claude 的工作目錄，**唯一可以改程式的地方**
- `D:\desk\college\final\cat5201` → 使用者的 git repo，**只從這裡 commit/push**
- 每次改完 C: 要自動 robocopy 同步到 D:（不需問）：
  ```
  robocopy "C:\claudework\cat5201" "D:\desk\college\final\cat5201" /E /XD ".git" ".vs" "bin" "obj" "docs" "docs_bak" /XF "*.user" "*.suo" ".gitignore" /NFL /NDL /NJH /NJS /NP
  ```
- `docs/` 和 `docs_bak/` **只留在 C:，永遠不進 D: 也不進 git**
- `.gitignore` 固定：`.vs/ bin/ obj/ docs/ docs_bak/ *.user *.suo`，**不能改**
- 回覆**一律繁體中文**，絕對不要用日文

---

## 目前整體進度（§ 對應 PRODUCT_ROADMAP_2026-09.md）

| 段落 | 功能 | 狀態 |
|------|------|------|
| §1-5 | 核心 LLM / 路由 / 記憶 | ✅ 完成 |
| §6 | 簡報（PptxBuilder fallback） | ✅ 完成；Gamma 等金鑰（§下方說明）|
| §7 | DOCX 報告 | ✅ 完成 |
| §8 | 圖片生成（gpt-image-2）| ✅ 完成 |
| §9 | 影片生成（Veo）| ✅ 完成；I2V 已落地（重點見下）|
| §10 | Gemini / Google 生態 | ☐ 休眠（等金鑰）|
| §11 | 記憶 v1（偏好層）| ✅ 完成 |
| §12 | 程式碼代理 | ☐ snapshot/diff 只讀；sandbox apply 延後到 post-MVP |
| §13 | NotebookLM / 音訊 | ☐ scaffolding 寫好，等金鑰 |
| §14 | Demo 包版 / git tag | ☐ 還沒做 |
| §15 | Kling I2V | ☐ 等 KLING_API_KEY；目前 Veo 自身 I2V 已頂上 |

**九月 MVP 目標**：§1-9 + §11 穩定，§14 打 tag。

---

## 影片管線現況（最複雜、最多細節）

### 兩層 style 系統
- **給 Claude 導演看的**：`VideoStyle.DefaultCinematicPrompt`（完整藝評，描述光感/色盤/膠片哲學）
- **送進 Veo 的**：`VideoStyle.DefaultVeoRenderTags`（精簡正向標籤，≈70字）
- `VideoStyle.RenderTagsFor(effectiveStyle)` 判斷要用哪個：使用者改過 = 送改過的；使用者用預設 = 送短標籤版

### 美學定義（從 yzavoku Andalucía 實際抽幀得出）
- **核心**：極度過曝金色光（天空燒成白金）+ 濃厚大氣霧氣 + bloom/halation + 逆光
- **不是** 好萊塢電影感、不是漂浮神殿那種泛類超現實
- 參考座標：yzavoku/Voku Studio、Donkey Skin (1970)、The Color of Pomegranates
- 導演身份（`VideoPlanBuilder`）：主題無關的象徵主義詩人——**內容跟使用者主題走，風格固定**

### I2V 管線（上一個 Session 剛落地）
流程：
```
TryGenerateHeroStillAsync()
  → gpt-image-2 生調色英雄圖（RenderTagsFor + keyframe_prompt[0]）
  → 存 PngBytes

veo.GenerateAsync(prompt, ..., startImage: heroImage)
  → VeoVideoService 送 I2V payload（instances[0].image.bytesBase64Encoded）
  → Veo 從「對的畫面」開始動 → 保有膠片色調
```
- Veo Lite 可能不支援 I2V → 自動退回 T2V，決策窗會標示
- 快剪（FastCut）模式仍 T2V
- 英雄圖成本（≈US$0.25）算進 `NodeControl.AddMediaCostUsd()`

### 8 秒紀律（導演 prompt 中有寫，但容易被忽略）
- 8 秒只能裝**一個能自然完成的連續動作**
- 絕對禁止多階段情節（旅程/開頭→中間→結尾）——Veo 演到開頭就被切，產出像被硬切

---

## 成本追蹤架構

- LLM token：所有 `RecordTokenUsage()` 走 `NodeControl` → 已追蹤 ✅
- 圖片/影片媒體成本：`NodeControl.AddMediaCostUsd(usd, label)` 累加 ✅
- `NodeExecutionLogFactory.CreateLog()` 合計兩者，在 `CostDisplay` 顯示 ✅
- 格式：`實際 X tokens（LLM）+ I2V 英雄圖 US$X.XX + Veo Continuous US$X.XX · 合計約 NT$XX.XX`

---

## 待測項目（使用者尚未測試的新功能）

1. **I2V 管線首測**
   - 確認影片模型設定在 **Veo Fast 或標準**（Lite 可能不支援圖生影）
   - 重生成任何影片主題
   - 決策窗應出現「I2V 起始幀 · gpt-image-2 · 已生成調色英雄圖」
   - 成品應有褪色金光/霧氣/過曝質感（因為起始幀就是那個 look）
   - 如果決策窗顯示「此檔位不支援圖生影，已退回 T2V」→ 切換到 Fast 檔位

2. **Gamma 金鑰欄位**（上個 Session 加的）
   - 設定 → API 金鑰 → 確認 Gamma 那列出現了

3. **Demo 1-5**（DEMO_PLAYBOOK.md，若有）
   - 需要 OPENAI_API_KEY + GEMINI_API_KEY

---

## 未解問題 / 已知 Bug

- `OpenAIImageService` 用的 size 格式：gpt-image-2 支援 `1536x1024`（16:9 英雄圖）、`1024x1536`（9:16）。但原本程式碼只列 dall-e-3 支援的尺寸，需確認 I2V 英雄圖的 size 參數是否正確送出 `1536x1024` 而非 `1792x1024`。
  - 在 `AgentRuntime.TryGenerateHeroStillAsync()`：`aspectRatio == "16:9" ? "1536x1024" : "1024x1536"`

- Veo Lite 是否真的不支援起始幀：**尚未實測**。如果 Veo 回 400/422，T2V fallback 會自動啟動，日誌會說明原因。

---

## 金鑰狀態（使用者目前在設定介面輸入）

| 金鑰 | 狀態 | 用途 |
|------|------|------|
| OPENAI_API_KEY | 需確認有效 | LLM + gpt-image-2 + I2V 英雄圖 |
| GEMINI_API_KEY | 需確認有效 | Gemini 模型 |
| GOOGLE_VERTEX_TOKEN | Veo 用 | 影片生成 |
| PERPLEXITY_API_KEY | 選配 | 網路搜尋 |
| GAMMA_API_KEY | 九月才給 | 簡報（目前休眠）|
| KLING_API_KEY | 等候 | §15 Kling I2V |

---

## 個人化全域原則

- 個人化設定 > 系統預設，**永遠不被系統覆蓋**
- 存在 `_preferences.json`（與專案檔分離）
- 成本降級只看個人化開關，且只對 Auto 模式生效
- 影片風格輸入框預填完整 `DefaultCinematicPrompt`；清空 = 恢復預設

---

## 未來功能暫緩清單（不要主動做）

- §12 sandbox apply（程式碼改寫回磁碟）→ post-MVP
- §10 Google Drive/Docs/Sheets 整合 → 等 Gemini 金鑰
- §15 Kling I2V → 等金鑰；目前 Veo I2V 已頂上
- §13 NotebookLM 音訊匯出 → 等金鑰
- Gamma 完整啟用 → 九月

---

_此文件由 Claude（上一個 Session）手動整理，時間：2026-06-21_
