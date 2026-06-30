# Product Roadmap To September 2026

This project is an AI workspace product, not just a chat canvas. The September target is a commercial-grade MVP that supports multi-model routing, multi-agent orchestration, workspace artifacts, automatic downstream nodes, file generation, multimodal generation, and a controlled code-agent foundation.

## Product Target

- Multi-model: OpenAI, Claude, Perplexity, and extension points for Gemini, local models, and future providers.
- Multi-agent: general, research, file, code, workflow, presentation, media, and future specialist agents.
- Multimodal: text, attachments, image generation, video generation, PDF/document/PPT outputs.
- Workspace: all intermediate artifacts are traceable, inspectable, status-aware, and reusable.
- Workflow: one instruction can create downstream nodes and continue execution automatically.
- Code agent: keep current snapshot/diff/validation foundation; deeper Codex-like editing is a later-stage focus.
- Product UX: model cost, status, failure reason, workspace, artifacts, and generated files must be understandable to a non-developer user.

## Scope Tiers: MVP / v1 / v2

依「2026 年 9 月商用 MVP」目標把範圍切成三層，避免把後期野心項當成交件門檻：

- **MVP（九月交件・可商用展示的最小完整品）**：一個非工程師使用者能看懂、能從頭跑完整流程的桌面產品。核心＝多模型路由、編排狀態機、工作流、自動下游節點、檔案/圖片/影片生成、記憶、產品化 UX、成本控制、可重複展示的 demo。對應 §1–§14 + §16。**功能面已基本完成，缺口在 §14 實機驗證打包 + 穩定版打 tag。**
- **v1（MVP 後第一個正式版・讓人天天用）**：把「能 demo」變「能日用」。對應 §18 Google 生態整合（Drive/Docs/Sheets）、§15 Kling I2V 影片精製、§7 Gamma 簡報設計引擎啟用、§12 Code Agent sandbox 套用、§17 階段一 手機唯讀鏡像。
- **v2（後期・野心版）**：完整手機客戶端（§17 階段二三）、§12 Codex 式專案編輯、多人協作、雲端同步等。

## Current Progress

Status: Phase 1.5 to Phase 2 boundary.

Done or mostly done:

- Basic node UI.
- Manual and auto model selection.
- Basic OpenAI, Claude, and Perplexity routing.
- Auto cost policy: Auto mode should not use Claude Opus or Perplexity Deep Search.
- Basic agent routing.
- Initial general, research, file, code capabilities.
- Finance research-first pipeline.
- Verified facts, source authority, quote type labeling.
- Workspace artifacts v1.
- Decision visualization v1.
- Loading spinner and timeout UX improvements.
- Code snapshot, diff, and validation foundation.

Not yet product-grade:

- Orchestrator is not yet a complete state machine.
- Automatic downstream node creation is not implemented.
- Workflow schema is not complete.
- PDF, PPT, image, and video artifact generation are connected (PDF/DOCX/PPTX/XLSX/image/video all wired, 2026-06-18).
- Memory is still foundational.
- Code agent is v0.5, not Codex-like.
- Regression testing is not standardized.
- Workspace UI still needs product-level simplification.

## Ordered Work Plan

### 0. Freeze Product Direction

- [x] Treat the project as an AI workspace product, not a simple chat tool.
- [x] Separate MVP, v1, and v2 scope. (見上方「Scope Tiers: MVP / v1 / v2」, 2026-06-27)
- [x] Keep large-file code repair optimization for later. (§12 標記為 later)
- [x] Avoid using large code-repair cases as daily regression tests. (§1 有獨立回歸清單)

### 1. Regression Test Checklist

- [x] Create and maintain a fixed regression checklist.
- [x] Finance test: TSM and MU short-term analysis.
- [x] General test: three-point Korean learning plan.
- [x] Attachment test: text attachment summary.
- [x] Multi-node test: previous node output feeds next node.
- [x] Model test: Auto cost protection.
- [x] Error test: timeout, canceled, no data.
- [x] Workspace test: artifact counts, types, visible/internal.
- [x] Add reusable test run template.
- [ ] Run this checklist after significant changes.

### 2. Orchestrator v1

- [x] Implement a task execution state machine. (OrchestrationStateMachine, 2026-06-12)
- [x] Define task types: research, write, summarize, generate_file, media, code, workflow.
- [x] Define initial default pipeline IDs for each task type.
- [x] Implement initial detect task -> select pipeline planning.
- [x] Move research-first pipeline formally into orchestrator. (AgentRuntime now executes from orchestrationPlan.CapabilityOrder / RequiredCapabilities / RequiresFreshFacts)
- [x] Ensure orchestrator writes workspace artifacts.
- [x] Ensure final synthesis reads workspace instead of stale context. (verified: RunFinalSynthesisAsync and final merge both consume workspace.BuildPromptBlock)
- [x] Add statuses: pending, running, success, failed, partial. (stage-level also has skipped; statuses update live in workspace orchestration artifact)

### 3. Workflow Schema

- [x] Define workflow artifact schema.
- [x] Include nodes, edges, task, agent, model, status.
- [x] Store each workflow step input and output.
- [x] Mark workflow support boundaries: canvas creation, replay, rerun, resume.
- [x] Support workflow replay. (WorkflowRunStore 記錄每次執行；「⟳ 重播整段」用相同輸入重跑整個工作流, 2026-06-14)
- [x] Support rerunning one step. (「↻ 重新生成答案」沿用快取的成功 workspace，跳過 capability 層只重跑 final synthesis；經由既有 SkipCapabilities flag, 2026-06-14)
- [x] Support resume from failed step. (失敗後「↻ 重新執行」記為 Resume，標記第一個未成功 step；單節點引擎下等同重試整段, 2026-06-14)
- [x] Display workflow summary in Workspace.
- 註：真正的「逐 step 部分續跑」(跳過已成功的 LLM step) 需多節點 canvas 執行鏈，屬 §4；本節在單節點原子引擎下提供 replay / resume / regenerate-answer 三種真實操作 + 執行歷史。

### 4. Automatic Downstream Nodes

- [x] Add downstream node proposal artifact without creating canvas nodes.
- [x] Add safe downstream node materialization method, not wired to automatic execution yet.
- [x] Mark materialized downstream nodes as generated/manual-run nodes.
- [x] Automatically split large tasks into downstream nodes. (BuildDownstreamPlanForNode → DownstreamNodePlanBuilder.FromTaskType by task type, 2026-06-14)
- [x] Create nodes on the canvas. (MaterializeDownstreamNodePlan lays out + adds NodeControl per proposal)
- [x] Create edges between generated nodes. (CreateCurve chains each node to the previous; RefreshConnectionsAfterLayout)
- [x] Execute generated nodes in order. (RunManualWorkflowChainAsync follows GetFirstDownstreamNode, {{input}} injects upstream output)
- [x] Show decision/workspace per generated node. (FocusDecisionNode each step; every node runs the normal orchestration+workspace pipeline)
- [x] Support stop, rerun, and skip. (2026-06-14: StopWorkflowChain cancels in-flight node via linked CTS + halts chain; SkipStepAndContinueAsync passthroughs upstream output and resumes from downstream; rerun-from-step = 右鍵「執行此節點與下游」. Context-menu items ⏹停止/⏭略過 gated by IsWorkflowChainRunning/NodeHasDownstream)
- [x] Example pipeline: research -> outline -> slides -> export. (Presentation: research-agent → outline → deck)
- [x] Example pipeline: search -> analysis -> report -> PDF. (GenerateFile pipeline research→draft→export runs end-to-end; PDF export 已於 §6 完成 2026-06-18 — 報告可直接輸出 .pdf)

### 5. Workspace and Artifact v2

- [x] Standardize artifact schema across all capabilities. (AgentWorkspaceItem 統一 schema + AgentWorkspace.BuildArtifactRecords 依 payload 型別集中推導 status/labels/source，各 capability 不必逐處填，2026-06-14)
- [x] Add artifact status: draft, ready, validated, exported, failed. (ArtifactStatus 常數 + 中文標籤 + 顏色；DeriveStatus 由 payload 推導：generated_file→exported/failed、validation→validated、final→ready、plan→draft, 2026-06-14)
- [x] Add artifact source metadata: agent, model, capability, node. (record 帶 SourceAgentId/ModelId/CapabilityId/NodeId；model 由 delegate/final/orchestration payload 推導, 2026-06-14)
- [x] Add timestamps. (CreatedAtUtc + CreatedAtLocalText 本機時間，產品卡片顯示, 2026-06-14)
- [x] Add artifact dependencies. (DependsOn + DeriveDependsOn：final 依賴 facts/search、檔案/簡報依賴 final；卡片以紫色 chip 顯示, 2026-06-14)
- [x] Add artifact preview. (record.Preview 220 字，產品卡片內嵌顯示, 2026-06-14)
- [x] Add artifact export. (每張卡片「💾 匯出」：純文字 artifact 寫成 _generated/.txt 並開啟；已落地檔案直接開啟, 2026-06-14)
- [x] Add artifact copy where useful. (卡片「📋 複製」鈕 + 右鍵選單，複製標題/類型/來源/依賴/預覽, 2026-06-14)
- [x] Make Workspace look like a product surface, not a debug dump. (CreateWorkspaceProductSurface 從結構化紀錄渲染卡片，取代 re-parse 文字行；可見/內部分組、中文 kind/format/status 徽章、來源時間列, 2026-06-14)
- 註：決策窗 Workspace step 現直接吃 NodeDecisionStepViewData.WorkspaceArtifacts（結構化）；舊文字行 CreateWorkspaceInspector 保留為 fallback（含 facts/diff/snapshot 深度檢視）。
- 附帶修正：簡報配圖不再於輸出區重複丟大圖（NodeService.ResolveInlineOutputImage：僅當圖片本身是唯一產出時才 inline 顯示；簡報/報告的封面圖已嵌 deck + 列為 chip）, 2026-06-14。

### 6. File Generation v1

- [x] Markdown report artifact. (MarkdownReportBuilder, 2026-06-12)
- [x] PDF export. (PdfReportBuilder via QuestPDF Community；Microsoft JhengHei 嵌入確保繁中正常；報告→.pdf、表格→table.pdf。煙霧測試：%PDF- 有效、91KB 含嵌入字型, 2026-06-18)
- [x] DOCX or plain text report export. (DocxReportBuilder via OpenXML：H1-3/清單/粗體/blockquote/Markdown 表格→Word 表格；GeneratedFileWriter.WriteDocx。煙霧測試：PK zip 有效, 2026-06-18)
- [x] PPT outline artifact. (PresentationOutlinePayload, 2026-06-12)
- [x] PPTX generation. (PptxBuilder OpenXML, validated 0 errors, 2026-06-13；見 §7)
- [x] Stable output file path handling. (_generated subfolder under final/file, sanitized name + timestamp)
- [x] Generated files appear as Workspace artifacts. (GeneratedFilePayload, kind=file)
- [x] Final answer can reference generated file artifacts. (answer appends 已生成檔案 + path note)

### 7. Presentation Agent

- [x] Topic -> presentation outline. (PresentationOutlineBuilder, 2026-06-12)
- [x] Outline -> slide plan. (PresentationOutlinePayload.Slides: cover/content/sources)
- [x] Slide plan -> PPTX. (PptxBuilder OpenXML, validated 0 errors, 2026-06-13)
- [x] Support 3, 5, and 10 slide outputs. (DetectRequestedSlideCount 偵測「N頁/張/中文數字」+ 作者 prompt 要求剛好 N 張 + PresentationOutlineBuilder.EnforceSlideCount 安全網（太多併入最後一張、太少拆重點最多的那張），AgentRuntime 已呼叫, 2026-06-18)
- [x] Support business presentation style. (內建商業主題：封面滿版深藍+置中大標+青色強調線+副標；內容頁深藍標題列+青線+頁尾頁碼；PptxBuilder + DeckPdfBuilder 一致。OpenXML 驗證 0 errors。Gamma 仍為九月「升級版」設計引擎, 2026-06-18)
- [x] Generate slides from research facts. (sources slide built from verified_facts; content from final synthesis)
- [x] Regenerate one slide. (右下角 ↻ 重生成，保留封面圖，2026-06-17)
- [x] Export the deck. (PPTX + DeckPDF 逐頁對應, 2026-06-17)
- Presentation v2 (Claude content + Gamma design): scaffolding done 2026-06-13 (GammaPresentationService + TryAddGammaPptxAsync, dormant until GAMMA_API_KEY set); activation steps in docs/GAMMA_INTEGRATION_SEPTEMBER.md. PptxBuilder kept as fallback.
- [x] NotebookLM B（匯出輔助）: NotebookLmBundleBuilder 把主旨/合成內容/verified_facts 來源（含網址清單）打包成 NotebookLM 可匯入的 .txt 來源包；OutputFormatDetector.WantsNotebookLmBundle 偵測關鍵字（notebooklm/匯入包…）→ GeneratePresentation 寫出檔並列為產出物，答案附說明。等 Google 發布 API 後改呼叫端即為 A 方案全自動（2026-06-18）。

### 8. Image Generation v1

- [x] Image generation capability. (OpenAIImageService + gpt-image-2, 2026-06-13)
- [x] Prompt -> image artifact. (GeneratedFilePayload Format=image, 2026-06-13)
- [x] Workspace image preview. (NodeControl OutputImageHost inline 顯示, 2026-06-13)
- [x] Image export. (寫入 _generated/.png, 2026-06-13)
- [x] Use generated images in presentations. (簡報配圖意圖偵測 → 生成封面圖嵌入 Marp deck + PPTX cover slide，PPTX 已驗證 0 錯誤, 2026-06-13)
- [x] Status: queued, generating, completed, failed. (ImageGenerationStatus enum；queued/generating 由 orchestration generate_image 階段反映，completed/failed 記在 GeneratedFilePayload.Status, 2026-06-13)
- [x] Cost hint. (圖片以「每張」計價而非 token：ModelCostEstimator.ImageCostUsd/ImageCostDisplay 依尺寸/品質查表估算單張成本；GenerateImageFile 成功 note 顯示「估算 ≈ 1 張圖 · 約 NT$X」。gpt-image-1 公布價目，gpt-image-2 取同級高品質估算, 2026-06-18)

### 9. Video Generation v1

- [x] Video generation capability. (OpenAIVideoService 封裝 OpenAI 影片/Sora API；OrchestrationTaskType.VideoGeneration + 關鍵字偵測既有，補 generate_video 階段 + AgentRuntime.GenerateVideoFile, 2026-06-14)
- [x] Prompt -> video request artifact. (VideoRequestPayload，ItemType=video_request，user-visible，記 jobId/狀態/進度/檔案, 2026-06-14)
- [x] Status polling. (OpenAIVideoService 每 5 秒輪詢 GET /v1/videos/{id}，上限 ~10 分鐘；狀態映射 queued/generating/completed/failed, 2026-06-14)
- [x] Video artifact preview or link. (完成的 mp4 以可點擊檔案 chip 列出，點擊用系統預設播放器開啟；SetOutputFiles 既有路徑, 2026-06-14)
- [x] Video export. (GeneratedFileWriter.WriteVideo 寫入 _generated/.mp4；Workspace 產出物卡片「開啟檔案」, 2026-06-14)
- [x] Long-running progress UI. (NodeControl.SetLoadingHint：loading 文字動態顯示「影片生成中 N%」+ 已等待秒數, 2026-06-14)
- [x] Failure and cancellation handling. (失敗標記 generate_video failed + 友善訊息、不影響主答案；取消時 OperationCanceledException 上拋 + best-effort 取消遠端 job, 2026-06-14)
- 多工具導演流程（2026-06-14）：Claude 當導演產出結構化影片計畫（劇本/分鏡/旁白/鏡頭/風格，VideoPlanPayload，核心永遠執行、不需影片 API）；影片產生器 Veo 3（VeoVideoService，唯一 provider）；配角 Flux/Midjourney（關鍵畫面）、ElevenLabs（旁白）、Suno（配樂）缺 API 自動略過，分工與狀態記在 ProviderRoles。即使 Veo API 未配置，仍交付完整 Claude 計畫。
- 更新（2026-06-17）：Sora 已停服，移除 fallback。Google 帳單已開通，Veo 3 正式啟用（IsConfigured 只需 GEMINI_API_KEY，已有）。VeoVideoService 端點形狀已校正（predictLongRunning / poll / 下載 uri）。
- 更新（2026-06-18，升 Veo 3.1 + 長片 + 電影風格）：
  - 模型升 veo-3.1-generate-preview；durationSeconds clamp 4–8。
  - **突破 8 秒上限**：VeoVideoService.ExtendAsync 接 Veo 3.1 原生延伸（predictLongRunning + source video base64 inlineData，720p，每段 +7 秒），AgentRuntime 多段續接編排（基底 8 秒 → 逐段延伸，最多 21 段 = 148 秒）。段數由 Claude 分鏡決定（導演被要求每 scene = 一段連續鏡頭、後段從前段最後畫面延續、重述主角/場景特徵以保一致）。延伸中斷 = 部分成功，交付已接好的片段並說明實際長度。VideoPlanBuilder.ParseRequestedSeconds 從文字偵測想要的秒/分。
  - **原廠電影風格**：VideoStyle.DefaultCinematicPrompt（35mm 底片、淺景深、柔和光、輕微顆粒，刻意不過度銳利）注入導演 + 每段 Veo prompt（前置風格避免延伸漂移）。
  - **個人化影片風格**：設定頁新增「影片風格」區，輸入框預填生效風格（讓使用者看得到完整預設 prompt 照格式改寫），可自訂覆寫、可一鍵恢復原廠；存 _preferences.json 的 VideoStyleOverride（空=用預設）。MainWindow.GetEffectiveVideoStylePrompt() 為單一真相。
- 更新（2026-06-18，雙模式影片 + Veo 3.1 API 踩雷修正 + 成本控制）：
  - **不短路主合成**：`GenerateVideoFile` 永遠在 general-agent 主合成完成後才呼叫（`execution.IsSuccess` 守門）；主合成文字以 `treatment` 傳入導演，影片計畫 note append 在主合成之後，不替換。使用者看到雙軌輸出（主合成 + 影片進度）。
  - **主合成當導演底本**：`string treatment = execution.Text` → `BuildVideoPlanAsync(treatment)` → `BuildDirectorPrompt(treatment)` 在導演 prompt 加「【上游已產出的企劃（請以此為改編底本）】」段，避免主合成與實際影片計畫不一致。
  - **FastCut（快剪）模式**：預告片/快剪/混剪/MV/蒙太奇/trailer/montage 等關鍵字 → `VideoPlanBuilder.DetectCutMode` → `VideoCutMode.FastCut`；每鏡頭獨立 Veo base 4s（16:9）生成 → `FfmpegService.TrimAndConcatAsync`（各裁 ~2.5s，H.264/yuv420p/24fps/1280x720，無音避爆）拼接。完全不用延伸 API，最穩。快剪上限 6 鏡頭（成本控制）。
  - **Continuous（連貫）模式**：Veo 原生延伸，base 8s + 逐段 +7s；節奏慢、省、人物連貫。
  - **ffmpeg 整合（2026-06-18）**：`FfmpegService` 偵測順序 `CAT5201_FFMPEG` → `PATH(where)` → 常見路徑 → WinGet Packages（winget 可攜版）。未裝 → `IsAvailable=false` → FastCut 自動降級 Continuous + 提示。ffmpeg 8.1.1 已安裝（winget install Gyan.FFmpeg）。
  - **Veo 模型檔位選擇（個人化）**：`VeoModelTier`（Standard/Fast/Lite）+ `VeoModels`（tier→model id/單價/延伸支援）。UI `VideoModelTierCombo` 在「影片風格」區。前期測試預設 Lite（$0.05/s）。Lite 不支援延伸：Continuous 強制單段 8s + 提示；FastCut 無此限制。計費：Standard=$0.40/s、Fast=$0.15/s、Lite=$0.05/s。
  - **Veo API 踩雷（血淚，已修）**：① `numberOfVideos` → 400（base + extend 都不可送，已移除）② 延伸 source 必須用 `video.uri` 參照（不能 inlineData/gcsUri）③ 延伸來源需 16:9（9:16 直式 → 400；多段/FastCut 一律 16:9 base）④ 延伸 done=true 但無影片內容（API 不穩，錯誤訊息含 raw response 供診斷）。策略：預告片走 FastCut 繞開不穩的延伸 API。
  - **導演 prompt 強化**：禁止 segment_prompt 含文字/字幕/logo；要求每鏡頭聚焦單一主體；最後 scene 強制收尾鏡頭；加負面提示（no text/extra limbs/morphing）。
  - **成本估算**：影片計畫 note 前顯示「預估成本 US$X（≈A$X）」讓使用者生成前知道花費。

### 10. Multi-Model Expansion

- [x] Unified model registry. (AiModelRegistry 既有；2026-06-14 補 Available/WithCapability/CheapestWithCapability/IsAvailable)
- [x] Unified provider interface. (IAiProvider 既有：Kind/Supports/GenerateAsync/GenerateStreamAsync)
- [x] Capability tags: search, vision, image, video, code, cheap, premium. (AiModelCapability 補 ImageGeneration/VideoGeneration/Code；新 AiCostTier Economy/Standard/Premium；AiModelDefinition.CapabilityTags/CapabilitySummary, 2026-06-14)
- [x] Add models without changing orchestration core. (情境 A：registry 加一筆即可，零核心改動；見 docs/MULTI_MODEL_EXTENSION_SEPTEMBER.md, 2026-06-14)
- [x] Prepare extension point for Gemini and other APIs. (AiProviderType/Kind 加 Gemini；registry 加 gemini-2.5-pro 休眠 IsAvailable=false；NormalizeNodeModel 對休眠模型退回預設避免誤路由；擴充步驟成文件, 2026-06-14)
- [x] UI shows model capability and cost tier. (ModelSelector item 加成本層級徽章 + 能力 tooltip；決策窗 Model 步驟顯示成本層級＋能力, 2026-06-14)
- [x] Fallback policy selects by capability and cost. (AiCapabilityGuard 重導改從 Available 依成本由便宜到貴挑具備該能力者；AiFallbackPlanner 最後手段亦依成本排序且只含 Available, 2026-06-14)
- 更新（2026-06-14）：Gemini **已實際啟用**（GeminiChatService + GeminiProvider + AiServiceRouter case 接線完成；registry gemini-2.5-flash[經濟] / gemini-2.5-pro[標準] IsAvailable=true，已出現在模型選單可選）。原因：使用者要 Gemini 做 Google 生態整合（Veo 影片亦走 Gemini API）。v1 能力宣告為文字/長文/程式碼（未宣告 Images/Search，避免導到未實作路徑）。實測：金鑰有效、generateContent 形狀正確；gemini-2.5-pro 免費額度緊（429），故另加額度較寬的 flash。

### 11. Memory v1

- [x] Remember user preferences. (PreferenceExtractor + MemoryStore.UpsertPreference, 2026-06-13)
- [x] Remember common formats. (pref.format, 2026-06-13)
- [x] Remember common workflows. (episodic execution_result 記憶, 2026-06-13)
- [x] Remember model cost preference. (pref.model_cost, 2026-06-13)
- [x] Remember output language. (PreferenceExtractor pref.language, 2026-06-13)
- [x] Manual memory clear. (側邊欄記憶面板：清除偏好 / 清除歷史, 2026-06-13)
- [x] Visualize when memory is used. (decision-viz 新增 Memory 步驟：偏好 N・記憶 M + 召回內容條列；財經即時任務顯示「本次略過記憶」, 2026-06-13)

### 12. Code Agent v1.5

- [x] Keep current code snapshot, diff, and validation. (CodeCapability/FileCapability/CodeDiffArtifactExtractor/CodeDiffDryRunValidator)
- [x] Add large-task cost/risk warning. (CodeTaskAssessor + BuildCodeRiskNoteBlock, 2026-06-13)
- [x] Support "list bugs first, then choose one to fix". (bug_listing/bug_fix_selected request types + BuildBugListingInstructionBlock, 2026-06-13)
- [x] Support targeted context extraction. (CodeContextExtractor anchor windows, 2026-06-13)
- [x] Support chunked analysis. (CodeContextExtractor chunk map + ChunkMap prompt note, 2026-06-13)
- [x] Support patch validation. (CodeDiffDryRunValidator + repair loop)
- [ ] Later: sandbox apply.
- [ ] Later: Codex-like project editing.

### 13. Product UX

- [x] Product-grade error messages. (BuildFriendlyError: 逾時/金鑰/額度/網路/伺服器/拒絕 分類訊息，2026-06-13)
- [x] Complete loading/progress/running states. (running 藍框 + 既有 spinner/秒數計時 + success 綠框短暫提示, 2026-06-13)
- [x] Token and cost display. (真實 API usage：Claude/GPT/Perplexity/Gemini 四家都接，決策窗顯示「實際 N tokens」，未接到才退回字數「估算」，2026-06-16)
- [x] Readable execution log. (決策窗最上方「執行摘要」白話卡：模式/任務/記憶/產出/結果，2026-06-16)
- [x] Workspace should not look like a debug dump. (屬 §5 Workspace v2，已於 2026-06-14 用 CreateWorkspaceProductSurface 處理；本項與 §5 重複，標記為已完成)
- [x] Clear node status. (NodeBorder 顏色：藍=執行中/綠=成功/紅=失敗/黑=閒置, 2026-06-13)
- [x] Rerun after failure. (失敗狀態列「↻ 重新執行」鈕，重跑上一次 prompt, 2026-06-13)
- [x] Clear Manual/Auto mode hints. (執行摘要卡第一行明示手動/自動 + 模型選單 tooltip 說明，2026-06-16)
- [x] Later: 真實 API usage 已串回（取代字數估算），四家 provider 全接。
- [x] App 內 API 金鑰設定（2026-06-20）：設定鈕點開分「個人化 / API」兩分頁。API 分頁可直接輸入
  OpenAI / Claude / Gemini / Perplexity 金鑰，免設環境變數。集中式 ApiKeyStore 解析順序＝
  環境變數 → app 內輸入 → 自動切換（既有 fallback 鏈）→ 全失敗才回報。金鑰以 Windows DPAPI
  加密落地（CurrentUser，不上傳）。儲存後 AiServiceRouter.ResetServices 讓服務以新金鑰重建。
  防禦觸發：未偵測到金鑰 / 額度不足 / 逾時 / 伺服器不穩 / 模型不支援，皆走 ExecuteWithFallbackAsync
  自動換模並顯示於決策窗（SetLiveDecisionExecuting 帶 fallback 原因與 attempt 序號）。

### 14. Demo and Delivery Polish

- [x] Demo 1: stock analysis.（劇本見 docs/DEMO_PLAYBOOK.md；管線 research-first→facts→合成）
- [x] Demo 2: learning plan PDF.（劇本同上；general→PdfReportBuilder）
- [x] Demo 3: research topic -> presentation.（劇本同上；research→outline→PptxBuilder+DeckPdf）
- [x] Demo 4: image generation -> presentation.（劇本同上；gpt-image-2→cover→PptxBuilder 嵌圖）
- [x] Demo 5: attachment summary -> report.（劇本同上；attachment→summarize→DocxReportBuilder）
- [x] Document known limitations.（docs/KNOWN_LIMITATIONS.md, 2026-06-20）
- [ ] Package demo-ready version.（待跑一輪 DEMO_PLAYBOOK 驗證 + 確認四家金鑰）
- [ ] Freeze stable September build.（在 D: repo 打 tag，git 操作於 D: 進行）
- 註：五個 demo 為「可重複展示劇本」（精確輸入 / 管線 / 預期產出 / 講解重點 / 成本）；
  實機截圖／錄影需在備好金鑰的執行環境由使用者現場擷取。另含 Bonus 防禦機制示範。

### 15. Kling Image-to-Video（後期規劃）

背景：頂級 AI 影片創作者的核心工作流是「靜態英雄圖 → I2V 動畫化」，而非直接 T2V。
先用 gpt-image-2 生成超現實風格底稿（構圖/角色/氛圍已定），再交 Kling I2V 讓畫面輕微活起來。
角色一致性比 Veo T2V 高得多，AI 味顯著降低，正好補足目前 Veo 直出的弱點。

- [ ] 接入 Kling REST API（KlingVideoService.cs），支援 Image-to-Video 端點。
- [ ] 在圖片生成任務後提供「動畫化」選項（將生成的 .png 作為 I2V 輸入）。
- [ ] 在 Orchestration 層新增 image_to_video task type，pipeline: image_gen → kling_i2v → export。
- [ ] AiModelRegistry 加入 Kling 模型定義（能力標籤 VideoGeneration + I2V）。
- [ ] 個人化頁面加入 Kling I2V 強度/時長設定。
- [ ] 考慮 Flux（Replicate API）作為 gpt-image-2 的超現實風格替代圖片來源。
- 啟用條件：取得 `KLING_API_KEY`。Veo 保留為 T2V 主力（長片/FastCut），Kling 為 I2V 精製主力。

### 16. Cost and Reliability Hardening（2026-06-26~27）

- [x] 接續迴圈「自然講完即停」：以實際 OutputTokens 是否逼近 8000 上限判斷，模型沒吐 `[[END_OF_RESPONSE]]` 也不再空跑滿 5 輪；簡單問答 5 輪→1 輪，成本與延遲大降。(NodeExecutionCoreService 兩個 continuation 迴圈, 2026-06-27)
- [x] 附件文字化 + 快取：PDF(PdfPig)/HTML(剝標籤)/Office/純文字在本機抽成文字內嵌進 prompt、不再每次重送原檔；圖片維持以圖片傳。快取鍵＝路徑+檔長+修改時間，session 內每附件只抽一次（含下游繼承、節點重跑命中）。大型 PDF 省最多（OpenAI 原本把每頁也當圖片計 token）。(AttachmentTextCache + NodeService.BuildAiRequestAsync；csproj +PdfPig 0.1.15, 2026-06-27)
- [x] 下游讀上游附件 個人化開關：預設關＝下游只讀上游文字輸出（省）；開＝沿上游鏈繼承附件（現走快取文字，便宜）。修正：開關 gate 下在實際執行路徑 `NodeService.CollectAiAttachments`（先前誤接在沒被呼叫的 NodeRequestFactory）。(UserPreferencesState.ReadUpstreamAttachments + 設定頁「上游附件」區, 2026-06-27)
- [x] 第一層輸出判斷擴及影片/圖片：OutputIntentResolver 加 video/image 兩類，閘門 `MentionsVideoOrImage` 放行，OrchestrationPlanner.Build / AgentRuntime 用 LLM 意圖覆寫 taskType。修「給我一個15秒的影片」這種非命令式講法被漏判成純文字。(2026-06-26)
- [x] 純手動模式不自動轉換 + Auto 模式產出前二次確認：手動模式只用選的模型做純文字、不偷加生影片/圖片/檔案；Auto 模式偵測要產出時先彈確認框。收緊影片關鍵字避免聊天誤判。(allowMediaGeneration + MainWindow.ConfirmGenerationAsync, 2026-06-26)
- [x] 「重新生成答案」從輸出區右下角按鈕移到節點右鍵選單（RegenerateMenuItem，依「有輸出且不忙」啟用）。(2026-06-27)

### 17. Mobile Access（後期規劃）

背景：目前是 Windows WPF 桌面程式，畫布／決策窗／節點互動都在本機。希望手機也能**即時看到、甚至操控或使用**此程式，讓使用者離開電腦也能查看執行進度、回應二次確認、或直接下指令。

分三階段（由淺到深，價值遞增、工程量也遞增）：

- [ ] 階段一 — 手機即時「看」（唯讀鏡像）：把畫布／決策窗／節點輸出以唯讀方式鏡像到手機（執行進度、token/成本、產出物清單）。技術選項：本機開輕量 Web 伺服器，手機瀏覽器連同區網即看；或推到雲端唯讀鏡像供外網看。
- [ ] 階段二 — 手機「審核 / 輕操控」：手機上回應二次確認（要不要產影片/圖片/檔案）、停止/重跑節點、加入記憶等輕量操作。需雙向即時通道（WebSocket / SignalR）。
- [ ] 階段三 — 手機「使用」：手機直接下指令、建節點、跑工作流，接近完整客戶端。需把執行/編排/工作區核心邏輯與 WPF UI 解耦成可遠端呼叫的服務層（API），手機端走網頁或原生 App。
- 架構前置（關鍵成本）：目前執行核心與 `MainWindow`／`NodeControl`／Canvas 綁得很緊（全是 WPF）；行動化真正的工程量在「把執行/編排/工作區邏輯抽成不依賴 WPF 的服務層」。建議從階段一（唯讀鏡像）起步，逐步把核心抽離，避免一次重寫。
- 啟用條件：MVP 後的延伸方向，先確認九月桌面 MVP 穩定再投入。

### 18. Google Workspace Integration（Gemini 生態，v1）

背景：當初選 Gemini 的主因就是 **Google 生態整合最好**（見記憶 [[gemini-for-google-integration]]）。LLM 側已啟用 Gemini（§10，gemini-2.5-flash/pro 可選）；本節是**資料／檔案側**——讓產出物直接進 Drive/Docs/Sheets、附件可直接從 Drive 取，使 cat5201 不只是生成工具，而是接進使用者既有 Google 工作流的樞紐。

- [ ] Google OAuth 登入（取得 Drive / Docs / Sheets 授權範圍；token 比照 API 金鑰以 DPAPI 加密落地）。
- [ ] Drive：生成的 PDF / PPTX / DOCX / 圖片 / 影片可一鍵存到使用者 Drive；附件可直接從 Drive 選檔（免先下載到本機）。
- [ ] Docs：書面報告（§6）可一鍵輸出成 Google Docs；可讀 Google Docs 當節點輸入。
- [ ] Sheets：表格（§6 xlsx）可輸出成 Google Sheets；可讀 Sheets 當資料來源（接 §6 表格管線）。
- [ ] 在 Orchestration / 輸出意圖層加「輸出到 Google」選項（OutputIntentResolver 既有架構擴充，不重造分類器）。
- [ ] （延伸）Gmail / Calendar：把產出寄出、或排程節點執行。
- 對接點：§10 已備 Gemini 休眠擴充點；本節是把「模型用 Gemini」延伸成「資料也走 Google」。
- 啟用條件：Google Cloud 專案 + OAuth client id/secret。屬 **v1**（MVP 後第一個正式版的主打差異化）。

## Immediate Next Step

Start with `docs/REGRESSION_TEST_CHECKLIST.md`, then implement Orchestrator v1.
