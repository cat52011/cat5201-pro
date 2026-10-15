# cat5201 — 已知限制（Known Limitations）
**更新：** 2026-06-20

展示前先讓老師／評審知道邊界，做預期管理。這些是「目前刻意未做」或「受外部限制」，不是 bug。

## 需要外部條件

- **Gamma 簡報設計引擎**：scaffolding 已寫好但休眠，要等 `GAMMA_API_KEY`（規劃九月）。目前用內建 PptxBuilder 商業主題輸出。
- **Kling 圖生影（I2V）**：規劃中（§15），等 `KLING_API_KEY`。目前影片走 Veo 文生影（T2V）。
- **各家 API 額度**：免費額度可能 429（尤其 gemini-2.5-pro）；防禦機制會自動切到其他可用模型，但若全部額度耗盡會直接回報失敗。

## 影片

- 單段 Veo 上限 8 秒；長片靠原生延伸接段（最長約 148 秒），延伸 API 偶爾不穩 → 預告片建議走 FastCut（ffmpeg 拼接）較穩。
- Veo Lite 檔位不支援延伸：Continuous 模式會強制單段 8 秒。
- 直式 9:16 不能做延伸來源；多段／FastCut 一律 16:9。
- 影片連貫模式已採「靜態英雄圖 → I2V（Veo 圖生影起始幀）」工作流：先用 gpt-image-2 生一張調好色的英雄圖，再餵給 Veo 當第一幀，讓動態保有色調 / 質感（最貼近 yzavoku 那種褪色膠片 look）。需要 OPENAI_API_KEY（出英雄圖）+ Veo 金鑰。
- I2V 注意：圖生影偏好 Veo Fast / 標準檔位；Lite 可能不支援起始幀（會自動退回純 T2V，並在決策窗標示）。快剪（預告片）模式仍走純 T2V。
- §15 Kling I2V 仍保留為「另一條 I2V 供應商」選項（等 KLING_API_KEY）；目前 I2V 已用 Veo 自身能力先落地。

## 程式碼代理（§12，刻意延後）

- 只到 snapshot / diff / 乾跑驗證；**不會真的把改動寫回磁碟**（sandbox apply 延後）。
- 不支援跨多檔案的 Codex 式專案編輯。
- 大型程式碼修復未最佳化，不建議當日常回歸測試。

## 架構邊界

- 單節點原子執行引擎：工作流的「逐 step 部分續跑」靠多節點 canvas 鏈達成；單節點內提供 replay / resume / regenerate 三種操作。
- 記憶為 v1：偏好 + 格式 + 語言 + 情節記憶；非長期向量記憶庫。
- 無資料庫：所有狀態在記憶體 + 本機 JSON 檔；關閉未存的專案不保留。

## 安全與金鑰

- 使用者輸入的 API 金鑰以 Windows DPAPI（CurrentUser）加密落地，僅限本機帳號解密、不上傳；但本機同帳號程式仍可解。要更高安全等級請改用系統環境變數。
- 金鑰檔複製到別台機器無法解密（DPAPI 綁帳號），會被當未設定。

## 平台

- 僅 Windows（WPF / net8.0-windows / DPAPI）。
- ffmpeg 需自行安裝（已測 8.1.1）；未裝則 FastCut 自動降級為 Continuous 並提示。
