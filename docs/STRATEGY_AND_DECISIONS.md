# cat5201-pro 策略與決策紀錄(Strategy & Decisions)

> 這份把散在對話/記憶裡的**策略分析、決策來龍去脈、技術狀態**整合進 repo，讓 pro 專案自足——
> 不管誰、哪個對話、哪台機器打開，都拿得到完整脈絡，不依賴外部記憶。
> 搭配 `PROJECT_GOALS.md`(北極星)與 `PRODUCT_ROADMAP_2026-09.md`(功能規劃 §0–§18)一起看。
> 建立於 2026-06-27。

---

## 0. 現況一句話

畢業版(WPF)已完成並**凍結**；本 repo `cat5201-pro` 為強化版。**產品的「廣度」目前遠超過程式的「結構成熟度」與「商業定位清晰度」**——下一步該定商業方向、補地基，而不是再加功能。

## 1. 雙軌策略 + 凍結決策 + 工作區

**雙軌(避免用同一 codebase 同時當畢業專題又當商用產品——那是最常見的失敗陷阱):**
- **軌道一 = 畢業版 WPF**：收尾凍結，是「我能造整個棧」的證明/獎盃。tag `v1.0-graduation`。
- **橋樑 = 可攜資產**：prompt library + 流程/設計。跨語言可攜，是軌道二的起點。真正可攜的是**設計**，不是 40k 行 C# 管線。
- **軌道二 = 商用版**：在對的地基上重建護城河。

**凍結範圍(絕不再動):**
- `C:\claudework\cat5201`、`D:\desk\college\final\cat5201`、GitHub `cat52011/cat5201`。

**pro 工作區對應(沿用 C: 編輯 → D: 跑測 模式):**
- `C:\claudework\cat5201-pro` = 編輯區(SavesDir 已改成獨立路徑)。
- `D:\desk\college\cat5201-pro` = 跑/測 + git repo(main，remote `cat52011/cat5201-pro`)。
- 輸出/存檔/附件 = `D:\desk\college\cat5201-pro\file`(獨立，不碰舊資料)。

## 2. 這個產品是什麼 + 真正的差異點

- 是「**AI 工作台**」——節點畫布 + 多代理編排，**串接各家模型的編排層**(功能大多是連接來的，不是自己造)。
- ⚠️ 「連接各模型」是 AI 棧**最商品化、最可抄**的一層(LiteLLM/OpenRouter/LangChain 就是幹這個)。站那層 = 最大暴露，不是護城河。
- **真正難抄的差異點 = 「終端使用者 × 每一步可稽查」的交集**：
  - Langflow/Dify 是給**開發者**建 app 的，不服務非工程師。
  - Poe/Monica 是終端使用者多模型，但**沒有畫布、沒有可稽查**。
  - 沒人把「非工程師也能用、且每個 AI 決策/成本/來源都攤開」做成工作台。**這才是該死守深耕的**(尤其對金融/合規場景)。

## 3. 商業化銳評(六個硬傷 — 別走回頭路)

1. **血海賽道**：視覺化 AI agent/工作流是擁擠又有錢的品類(Dify、n8n ~6000萬鎂、LangChain 高估值…)，多數開源免費。單人用 WPF 從零打同品類 = 必輸。
2. **「什麼都做」是最大商業致命傷**：贏家全是極窄垂直(Cursor=code、Perplexity=搜尋、Gamma=簡報)。廣度 = 沒有單一買家說得出「我為 X 付錢」。
3. **WPF = 沒有通路 = 商業化≈重寫**：上不了網頁/手機/app store，做不了 PLG。§17 手機不是 nice-to-have，是通路前提。
4. **單位經濟天生燒錢**：每輪多次 LLM 呼叫 + 重 context + 媒體生成。BYO-key 可解毛利(見 §4)但把市場縮到技術人。
5. **買家錯配**：真護城河(可稽查)服務的是金融/合規買家，但沒機構會跟「學生 + 桌面 App + 沒公司/SOC2」買。護城河真，包裝錯。
6. **贏不了 feature war**：跟生態拼功能數量必敗；只能贏「他們不做的那個窄點」。

## 4. 商業模式分析(BYO-key 的致命矛盾)

- **矛盾**：使用者想要的「**BYO-key 免費/買斷/超低訂閱**」⟂「**面向非工程師**」。BYO-key 對工程師低門檻、對非工程師是高牆(要開 4 個平台帳號綁卡)。
- **市場證據**：
  - BYO-key 工具(**TypingMind / Msty / BoltAI / LibreChat**)= 全是技術小眾 indie 或開源零營收。
  - 非工程師大眾要**代管**(**Poe** $20/月吃到飽，平台幫你付模型錢)——正好跟 BYO-key 相反。
  - funded 畫布工具(**Dify / Langflow / n8n**)賺錢在「託管 + 團隊 + 企業」，**不在 BYO-key**。
- **廣告/買斷打臉**：廣告要百萬級曝光(到不了)+ 傷 UX；買斷對「依賴一直變的外部 API」的 AI 工具特別危險(等於永遠免費維護)。
- **唯一能同時要的結構 = 免費階梯 → 付費階梯**：
  ```
  免費層(BYO-key)          → 衝採用/驗證/履歷/社群
  付費層(你代管 key + 加值) → 真營收:非工程師「免設定開箱即用」+ 團隊/SSO/合規稽查匯出
  ```
  「幫你搞定一切 + 可稽查」本身就是能賣的價值。
- 該拆的競品定價頁:**TypingMind**(BYO-key 買斷範本)、**Msty**(桌面 BYO-key freemium)、**Dify**(畫布 + 正確變現階梯)。

## 5. 高手怎麼思考(心智模型 — 這是最該內化的)

1. **Buy commodity, build moat**：預設用現成基礎設施，只親手做獨特價值。判準:「別人是不是已做得更好且在維護?」是 → 用它。
2. **Distribution-first**：先問「使用者怎麼拿到」再選技術。你的答案(商用+手機)→ 幾乎必然 web-first。你是先選 WPF 才發現上不了手機 = 反了。
3. **標準介面 / thin waist**：押注標準(OpenAI-compatible、**MCP**)。Google 整合(§18)其實該走 MCP 連接器而非自刻 OAuth+SDK。
4. **AI 產品先有 Evals 再有功能**：核心價值是「AI 做對事」，就要早建評測資料集 + 自動評分(Langfuse/Braintrust)，別憑感覺改 prompt。
5. **Observability from day one**：第一天接 tracing，不是出事才 printf(這次成本 bug 就是沒可觀測性才難追)。
6. **研究既有品類**：別重新發想。拆 Langflow / Dify / Flowise / n8n / Rivet 的架構，一週勝過自己想一個月。

## 6. Buy-vs-Build 對照(track2 該用現成的)

| 你手刻的 | 換成(現成) |
|---------|-----------|
| IAiProvider + 4 ChatService + Router + Fallback | **Microsoft.Extensions.AI `IChatClient`**(C# 原生) / LiteLLM / OpenRouter |
| Orchestration 狀態機 + Agent/Capability 框架 | **Microsoft Semantic Kernel**(官方 C#) / LangGraph / OpenAI Agents SDK |
| 決策窗 token/成本追蹤 | **Langfuse / Helicone** |
| NodePromptBuilder 手動塞 context | tool-calling + 結構化 retrieval(讓模型自己要 context) |
| AttachmentTextCache(PdfPig/剝HTML) | LlamaIndex / Unstructured |
| WPF 手刻節點畫布 | **React Flow (xyflow)** — node-UI 事實標準 |

> 留在 C# 的中間路 = Semantic Kernel + Microsoft.Extensions.AI(等於你手刻了一個較差版本的它)；但畫布上 web/手機仍需 React Flow。

## 7. 技術債(要面對，但**凍結版別動**，留 track2)

- **上帝物件**：`MainWindow.xaml.cs` 8000 行 / 330 方法 / 0 region；`AgentRuntime.cs` 4258 行、單一 `ExecuteAsync` **1160 行**；`NodeControl.xaml.cs` 2536 行。
- **零自動化測試**(沒有測試專案)。商用級硬傷；重構沒測試網 = 走鋼索。
- **核心與 WPF 焊死**：service 幾乎都持有 `MainWindow _main` 回呼。**這是 §17 手機化的真正工程量**——要把執行/編排/工作區抽成不依賴 WPF 的服務層。
- 29 處空 `catch {}`(吞例外，除錯盲區)；54 處 ConfigureAwait(false) 與「UI 執行緒不可用」規則混用(cross-thread 溫床)；`namespace test`(152 檔，門面差);181 檔全平鋪根目錄。

## 8. 這幾天(2026-06-26~27)的成本/可靠性/UX 修正史

- **接續自然停止**：以 OutputTokens 是否逼近 8000 上限判斷，模型沒吐結束標記也不空跑滿 5 輪。簡單問答 5輪→1輪。(NodeExecutionCoreService)
- **附件文字化 + 快取**：PDF(PdfPig)/HTML(剝標籤)/Office/純文字本機抽成文字內嵌、不再重送原檔;圖片維持圖片傳。快取鍵=路徑+檔長+修改時間。大型 PDF 省最多(OpenAI 原本把每頁當圖片計 token)。(AttachmentTextCache + `NodeService.BuildAiRequestAsync`；csproj +PdfPig 0.1.15)
- **上游附件個人化開關**：預設關=下游只讀上游文字輸出。⚠️ gate 要下在真正被呼叫的 `NodeService.CollectAiAttachments`，**不是** NodeRequestFactory(那是死路)。
- **第一層輸出判斷擴及影片/圖片**：OutputIntentResolver 加 video/image；修「給我一個15秒的影片」被漏判成純文字。
- **純手動不自動轉換 + Auto 產出前二次確認**：手動只用選的模型做純文字；Auto 偵測要產出先彈確認框。
- **重新生成移到右鍵選單** + 修「無快取時空回應」(改走完整重跑)。
- **所有原生 MessageBox → 自製對話框**(MenuConfirmDialog 通用化，19 處)。
- **右鍵選單去圖示 + 「執行此節點與下游」→「一鍵執行下游」**(只跑下游、不重跑本節點;自動流僅限 FlowMode 連線)。
- **成本結論**:輔助判斷器(OutputIntent/AutoModel)都只送使用者那句話、輸出小、有閘門——**無異常消耗**。剩下只有深鏈才有感的「鏈指示摘錄重複」(可選優化)。

## 9. ⏸ 待決定(pro 動大強化前要先定)

### 9a. 商業化三選一(決定一切下一步)
1. **開一間能活的公司** → 壯士斷腕收窄成一個垂直 wedge(如金融可稽查研究)+ 換 web。
2. **作品 → 進 AI 業界**(對學生常是最高 EV) → WPF 全棧加分，重點改成「把故事/架構講到讓人想雇你」。
3. **indie 小工具賺點真錢** → 收窄成一個窄功能 + web + 訂閱。

### 9b. 軌道二後端棧(選了才好起垂直切片)
| 選項 | 重用 | 代價 |
|------|------|------|
| **C# 後端**(ASP.NET + Semantic Kernel + Microsoft.Extensions.AI)+ React/React Flow 前端 | C# 技能 + 邏輯直接移植;修好 web+框架兩病 | .NET AI 生態較弱 |
| **TS 全棧**(Next.js + Vercel AI SDK + React Flow) | 生態最全、部署最易、學習值最高 | C# 邏輯重寫(設計可攜) |
| **Python 後端**(FastAPI + LangGraph) | AI 生態最強 | 又一新語言 |

> 建議路徑:先定 9a → 選 9b → 做最薄垂直切片(一節點/一模型 + React Flow + Langfuse 第一天就接)驗證地基 → 再移植護城河 → §18 Google → §17 手機。

## 10. 技術地雷備忘(改 pro 程式前必看)

- `AgentRuntime.ExecuteAsync` 跑在 **UI 執行緒**，裡面 await **不可** ConfigureAwait(false)。
- 「要產哪些輸出」的第一層判斷 = **OutputIntentResolver(LLM)**，不要重造分類器；影片/圖片也已納入它。
- 動附件/成本相關：真正路徑是 `NodeExecutionCoreService → BuildExecutionRequestAsync → NodeService.BuildAiRequestAsync → CollectAiAttachments`。**同名/相似 factory 有多個，先確認改的是真被呼叫的那條**(上次踩過:誤改 NodeRequestFactory 死路)。
- 記憶偏好只來自手動輸入;衝突=節點優先、偏好間最新優先。
- PowerShell/Bash 用 Big5 讀 UTF-8 會假性亂碼;debug 檔案內容用 UTF8.GetString(ReadAllBytes)。
