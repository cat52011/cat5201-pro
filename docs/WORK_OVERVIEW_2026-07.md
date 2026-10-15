# cat5201-pro 完整工作總表(使用者自用版,2026-07-06)

## 📊 數字面板

| 指標 | 現值 |
|---|---|
| 版本 | v1.8.0(標題列顯示) |
| 專案結構 | 3 專案:WPF 主(7,706 行主檔+6 partial)/ **Core 79 檔(零 WPF)**/ Tests |
| 自動化測試 | **116 個,全綠**(含 AgentRuntime 假 Provider 端到端) |
| 命名空間 | 全案 `Cat5201`(原 `test` 已改名) |
| CI | GitHub Actions(push 自動 build+test,待你 push 生效) |
| 打包 | `publish.ps1` → 自含式單一 exe(~220MB,免裝 .NET) |
| 法務 | LICENSE(專有)+ THIRD-PARTY-NOTICES(含 QuestPDF 營收條款) |

## ✅ 已完成(按主題)

### A. MVP 功能面(§1–§16,畢業版即有+pro 強化)
畫布/多代理編排/工作流/自動下游節點/檔案生成(PDF·DOCX·PPTX·XLSX)/圖片(gpt-image)/影片(Veo 3.1 長片+快剪)/多模型(OpenAI·Claude·Gemini·Perplexity)/記憶/決策視覺化/成本硬化(附件快取·接續自然停止)——全部 ✅

### B. §17 手機鏡像(三階段全完成)
| 功能 | 狀態 |
|---|---|
| 掃 QR 唯讀看進度/token/成本 | ✅(權杖保護、個人化記住自動開、快照去重省流量) |
| 遠端停止/重跑節點 | ✅ |
| 手機回應產檔二次確認(含倒數同步) | ✅ |
| 手機下指令建節點並執行 | ✅(自動接上游·灰色實線;可手動**選連接節點**;可**選模型**) |
| 點卡片看完整輸出(全螢幕) | ✅ |
| 手機建整條工作流/編輯畫布 | ☐ = 未來 web 客戶端範疇(架構已解鎖) |

### C. §18 Google 整合(Drive 全套+讀入)
| 功能 | 狀態 |
|---|---|
| OAuth 連結/解除(App 內憑證輸入框或環境變數) | ✅ 你已實測通過 |
| 狀態顯示連結帳戶信箱 | ✅ |
| 產出自動上傳 Drive | ✅(個人化總開關+**7 種檔案類型個別開關**) |
| Word/Excel 上傳時轉 Google 文件/試算表 | ✅(個人化開關) |
| 從 Drive 選檔當附件 | ✅(附件⊕鈕雙選項:本機/Drive) |
| **讀 Google Docs/Sheets/Slides 當輸入** | ✅ 剛完成(export 轉 txt/csv,走既有附件管線)⭐待你測 |
| Gmail/Calendar | ☐ 延伸項,不做除非你點名 |

### D. §19 Skills(獨立分頁)
新增/就地編輯(點名稱)/啟停/刪除/**GitHub 匯入**(SKILL.md frontmatter 解析)/全域注入 prompt/存偏好跨專案——✅(你說用法之後再想)

### E. 商品級硬化
| 項 | 狀態 |
|---|---|
| 花費帳本+每日預算上限(達標攔新任務) | ✅ |
| 產檔確認框:新版卡片設計+成本徽章(與實際計費同價目表)+**自動同意秒數個人化**(0=關) | ✅ |
| 全域崩潰處理(UI/背景/Task 例外全攔+友善對話框) | ✅ |
| 診斷日誌 `%LOCALAPPDATA%\cat5201-pro\logs`(30 天滾動) | ✅ |
| 原子寫入(偏好/專案檔/帳本;.bak 備份;鎖檔 IOException 已修) | ✅ |
| **Onboarding 精靈**(無金鑰首啟引導+四家「測試」即時驗證) | ✅ ⭐待你測 |
| **會話還原**(崩潰後提示開回上次專案) | ✅ ⭐待你測 |
| 執行時長人性化(2 分 50 秒)/失敗重跑鈕移除/空畫布提示移除 | ✅ |

### F. 架構工程(「一個核心×三介面」地基)
| 里程碑 | 狀態 |
|---|---|
| cat5201.Core 拆分(模型註冊/編排規劃腦/成本/決策記錄/workspace/payload/執行 DTO) | ✅ 79 檔 |
| 四個服務介面(IAgentHost/INodeContext/IExecutionDecisionResolver/IDecisionFinalizer)全在 Core | ✅ |
| **AgentRuntime 執行腦零 WPF 依賴,可無頭執行**(整合測試證明) | ✅ |
| AttachmentInfo 提出 MainWindow(舊存檔相容) | ✅ |
| namespace 改名 + MainWindow 拆 6 個主題 partial | ✅ |
| 手機鏡像 DTO/指令模型 = 未來 API 契約雛形 | ✅ |

### G. 這幾週抓到並修掉的重大 bug(實測回饋)
「照片」不生圖(關鍵字漏+手動模式硬封殺→改一律問第一層 AI)/確認框 NT$55 錯 10 倍(兩處真相)/File.Replace 鎖檔炸例外/VS 舊 cat5201pro.csproj 吸入子專案炸幾百錯(退役+方案檔重建)/Drive 403(=Cloud 專案沒啟用 Drive API,已記錄)/SpendLedger 初始化殘留(測試網抓到)

## 🌙 休眠中(給金鑰即活)
| 項 | 等什麼 | 啟用後 |
|---|---|---|
| Kling I2V | `KLING_API_KEY`(AK:SK) | 我接「圖片生成後→動畫化」管線 |
| Gamma 簡報引擎 | `GAMMA_API_KEY`(九月) | scaffolding 已在,接上即用 |

## 🚧 等你的閘門
| # | 事項 | 說明 |
|---|---|---|
| 1 | **煙霧測試**(P1+P2+P3-3 累積 6 項) | 見下方「你的待辦」;過了才開 B4 |
| 2 | git commit(+tag v1.8.0) | 你自己按;訊息我可給;我絕不主動 push |
| 3 | 進度簡報 | 你自理(檔在 file\,截圖規格已給過) |
| 4 | 簽章憑證決策(數千台幣/年) | 決定買→我做安裝器+自動更新+簽章 |
| 5 | 找 10 個真人試用 | 銳評結論:下一個里程碑在別人電腦上 |

## 📋 未來佇列(照序)
| 批 | 內容 | 規模 |
|---|---|---|
| **B4**(下一批) | AgentRuntime 的 ~40 個依賴分四小批搬 Core:①檔案 builders ②媒體服務 ③Code 工具 ④能力層(最難最後)→ 完成後 AgentRuntime 本體進 Core | 大,分四次 |
| **Phase C** | 內嵌 Kestrel 升級為正式 API 層(REST+SSE 呼叫 Core 執行腦),手機頁改吃新 API | 大 |
| **Phase D** | web 前端最薄切片(屆時定 React Flow vs Blazor) | 大 |
| **P4**(可並行) | demo 影片腳本、10 人試用發放包 | 小,偏寫作 |
| 持續 | 空 catch 就近清、偏好欄位必加相容測試、測試數只增不減 | — |

## ✍️ 你的待辦(現在)
1. **煙霧測試 6 項**:①重建啟動正常 ②開舊專案檔正常 ③強殺重開→彈還原提示/正常關→不彈 ④(可選)清空金鑰看精靈 ⑤**Drive 選擇器勾 Google 文件/試算表→附加→AI 讀得到**⑥手機快巡
2. 測過 → **git add -A + commit**(要訊息喊我)
3. 簡報截圖自理;想好 Skills 用法後告訴我
4. 兩個決策放心裡:簽章憑證買不買;10 人試用何時發

## 🗂️ 位置速查
| 東西 | 位置 |
|---|---|
| 執行計劃(給 AI 接手) | `docs/EXECUTION_PLAN_FOR_OPUS_2026-07.md` |
| 本總表 | `docs/WORK_OVERVIEW_2026-07.md` |
| 進度簡報 | `file\cat5201-pro_進度分享_2026-07.pptx` |
| 診斷日誌 | `%LOCALAPPDATA%\cat5201-pro\logs\` |
| 打包 | 根目錄 `.\publish.ps1` → `publish\win-x64\` |
| 花費帳本/偏好/Google token | `file\_config\` |
