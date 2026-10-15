# cat5201-pro 日後完整流程(v2,2026-07-06 重寫)

> 給任何接手的 session(Opus/其他)。使用者已核准,照序執行,**不要重新問方向**。
> 開工必讀:本檔 + `docs/PROJECT_GOALS.md` + `docs/STRATEGY_AND_DECISIONS.md` + `docs/PRODUCT_ROADMAP_2026-09.md`
> + 記憶 `C:\Users\s1065\.claude\projects\C--claudework-cat5201\memory\MEMORY.md`。

---

## 0. 鐵律(違反任何一條 = 停下來)

1. **工作區**:改碼在 `C:\claudework\cat5201-pro`,同步到 `D:\desk\college\cat5201-pro`(永遠 C:→D:;逐檔 cp 或 robocopy 排除 .git/.vs/bin/obj/file/publish)。
2. **驗證**:建整方案 `dotnet build cat5201.sln`(C:)/`cat5201pro.sln`(D:)+ `dotnet test`。**C: 0 錯 0 警 + 116+ 測試綠才算完成**。D: 出現 MSB3027 檔案鎖定=使用者程式開著,屬預期,原始碼同步到位即可。
3. **絕不碰**凍結畢業版(`C:\claudework\cat5201`、`D:\desk\college\final\cat5201`、GitHub `cat52011/cat5201`)。
4. docs/ 本機私密不進 git;publish/ 不進 git;gh CLI 未裝;**絕不主動 git push**。
5. **技術地雷**:
   - `AgentRuntime.ExecuteAsync` 在 UI 執行緒,其內 await 不可 `ConfigureAwait(false)`。
   - 輸出意圖單一真相=`OutputIntentResolver`(每次執行必呼叫,無關鍵字預審)。不重造分類器。
   - 附件/成本真路徑=`NodeService.BuildAiRequestAsync`/`CollectAiAttachments`;NodeRequestFactory 是死路。
   - `MenuConfirmDialog` ~19 處共用;手機指令回 UI 必走 `Dispatcher.InvokeAsync`。
   - 個人化永不被覆蓋;新偏好欄位必向後相容(有測試釘著)。
   - 主 csproj `DefaultItemExcludes` 已排除 `tests\**;cat5201.Core\**`。
   - **命名空間已是 `Cat5201`**(P1 改名完成);MainWindow 已拆 partial——鏡像/Google/Skills/花費/偏好/API 面板的成員放對應的 `MainWindow.*.cs`。
6. **溝通**:繁體中文;誠實回報(含未實測項);runtime 可見變更停下來給實測清單;自主執行不反覆求准,只在真岔路/不可逆/需決策時問;收尾不丟 a/b/c 選單。

## 1. 現況快照(2026-07-06,P1+P2+P3-3 完成後)

- **方案**:cat5201(WPF,主檔 7,706 行+6 個主題 partial)+ **cat5201.Core(79 檔,net8.0 無 WPF)** + cat5201.Tests(116 綠)。
- **命名空間** 全案 `Cat5201`;AssemblyName 不變。
- **功能**:MVP §1–§16 ✅;§17 手機三階段 ✅(看/停止重跑/遠端確認/下指令+選節點+選模型/看全文);§18 Google ✅(OAuth+信箱顯示、自動上傳+各類型開關+轉 Google 格式、Drive 選檔附件、**Docs/Sheets/Slides export 轉 txt/csv 當輸入**);§19 Skills ✅(獨立分頁+就地編輯+GitHub 匯入);Onboarding 精靈 ✅(四家金鑰即時驗證);會話還原 ✅(_session.lock+LastProjectPath)。
- **架構**:執行腦(AgentRuntime)零 WPF 型別依賴(IAgentHost/INodeContext/IExecutionDecisionResolver/IDecisionFinalizer 全在 Core);有假 Provider 端到端整合測試;workspace 群集+全部 payload+執行 DTO 已進 Core。
- **商品級**:花費帳本+每日上限、AtomicFile 原子寫入、全域崩潰處理、AppLog、CI、publish.ps1、LICENSE/第三方聲明、v1.8.0。
- **休眠**:Kling I2V(等 `KLING_API_KEY`)、Gamma(等 `GAMMA_API_KEY`)。
- **方向**:①開公司+③indie、先不收窄、「一個核心×三介面」;紀律=投地基不長橫向新功能。

## 2. 🚧 等使用者的事(隨時可解鎖)

| 事項 | 解鎖後做什麼 |
|---|---|
| **煙霧測試**(P1+P2+P3-3 累積) | 見 §6 清單;過了才開下一個大批 |
| **git commit + tag v1.8.0** | 使用者自己按;可協助訊息;絕不主動 push |
| 進度簡報 | 使用者自理(檔在 file\,截圖規格已給) |
| **簽章憑證決策**(數千台幣/年) | 解鎖 P-簽章:Velopack/Inno + signtool + 自動更新 |
| **KLING_API_KEY** | 解鎖 §15 接線:圖片生成後「動畫化」選項→I2V→輪詢→檔案 chip;個人化加 5/10s 選項 |
| **GAMMA_API_KEY**(九月) | 啟用 Gamma 簡報設計引擎(scaffolding 已在) |

## 3. 主線佇列(照序;每批 = 一批,不混)

### B4:把 AgentRuntime 的依賴群搬進 Core(四小批,各自 build+test)
B3c 調查:AgentRuntime 還引用 ~40 個主專案型別。**方法=逐群 dep-check(grep NodeControl|MainWindow|System.Windows)後整群 mv,build-chase 缺型別**(已驗證的工作流)。順序:
1. **B4-1 檔案 builders 群**(疑似全零 WPF):Markdown/Pdf/Docx/Pptx/Xlsx/DeckPdf builders、GeneratedFileWriter、PresentationAuthor/OutlineBuilder、DocumentAuthor、NotebookLmBundleBuilder、DownstreamNodePlanBuilder、WorkflowPlanBuilder、FinalAnswerSanitizer、FinanceTaskDetector。注意 csproj 套件(QuestPDF/OpenXml/Markdig)要跟著加進 Core.csproj、主專案留 ProjectReference 即可。
2. **B4-2 媒體服務群**:OpenAIImageService、VeoVideoService、FfmpegService、ElevenLabs/Suno、GammaPresentationService、KlingVideoService、GoogleDriveService(HTTP+檔案,應零 WPF;注意 ApiKeyStore 也要搬或抽介面——它用 DPAPI=Windows 專屬,`System.Security.Cryptography.ProtectedData` 套件可進 Core,OK)。
3. **B4-3 Code 工具群**:CodeContextExtractor/CodeDiffArtifactExtractor/CodeDiffDryRunValidator/CodeTaskAssessor(+AttachmentTextCache/ArtifactTextExtractor 順路檢查)。
4. **B4-4 能力層**(最難,最後):IAgentCapability、AgentCapabilityPlanner/Registry/Result、Delegation/Parallel Planner+Runner、AgentRegistry、OrchestrationStateMachine、各 Capability 實作——**先 dep-check;若 Capability 持 NodeService/MainWindow,抽介面而非硬搬**。完成後 AgentRuntime.cs 本體可進 Core。
- 每批 DoD:C: 0/0+測試綠、同步 D:(記得刪 D: 根目錄被搬走的檔)、更新記憶 `service-layer-decoupling`。

### Phase C:核心 API 化(B4 之後)
- 把內嵌 Kestrel 從「鏡像 server」升級為**正式 API 層**:執行節點/查詢工作區/串流輸出的 REST(+SSE)端點,呼叫 Core 的 AgentRuntime(用非 WPF 宿主實作 IAgentHost/INodeContext)。
- 桌面 WPF 照舊直呼(不強迫繞 API);API 供 web/手機/自動化。
- 產出 OpenAPI 描述;手機頁改吃新 API 驗證之。

### Phase D:web 前端(Phase C 之後;§9b 棧決策點)
- 屆時與使用者確認前端棧(React Flow vs Blazor);先做最薄切片(登入→下指令→看串流輸出→看決策資料)。

### P4:商業化資產(可與 B4 並行,偏寫作)
- 3 分鐘 demo 影片腳本(主線=投資建議工作流:下指令→多代理→決策窗→手機→產出→Drive)。
- 「10 個真人試用」發放包:publish 資料夾+一頁上手+回饋表單問題清單。
- 提醒使用者:10 人回饋=收窄垂直的依據(§9a 後半),不替他決定。

## 4. 持續性規範

- 新增偏好欄位:record 加參數(給預設值)→Save/Load→SyncCostControls→**必加向後相容測試**。
- 新增鏡像/Google/Skills/花費成員 → 放對應 partial。
- 空 catch:遇到就近清(資料/網路相關接 AppLog),不獨立成批。
- 每批更新對應記憶檔與 roadmap 勾選;測試數只增不減。

## 5. 每批完成定義(DoD)

1. C: `dotnet build cat5201.sln` 0 警 0 錯;2. 測試全綠(≥116);3. 同步 D:(+處理搬移刪檔);4. 記憶/roadmap 更新;5. 誠實回報+實測清單(runtime 可見時)。

## 6. 目前欠的煙霧測試(P1+P2+P3-3 累積,使用者執行)

1. 關程式→VS 重建→啟動:標題/畫布/設定三分頁正常(P1 改名+拆檔)。
2. 開舊專案檔:載入正常(改名+AttachmentInfo 提出相容性)。
3. 工作管理員強殺→重開:彈「還原上次的工作?」→開啟正確;正常關閉→重開:不彈。
4. (可選)清空金鑰啟動:onboarding 精靈出現、「測試」鈕四家驗證正常、略過無害。
5. 節點附件(+)→Google Drive:清單裡 **Google 文件/試算表可勾選(標「轉為文字/CSV」)**→附加→跑任務 AI 讀得到內容 ⭐(P3-3 新)。
6. 手機掃 QR 快巡:狀態/停止/重跑/下指令照常。
