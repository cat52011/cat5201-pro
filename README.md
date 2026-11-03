# cat5201-pro

[中文](#中文) | [English](#english)

---

## 中文

cat5201-pro 是一個 Windows 桌面版的 AI 整合工作台：以心智圖樣式呈現，畫布上的每個節點是一個 AI 互動，可以指定由哪個或哪些模型獨立或協作回答，並且可以把節點串聯成上下游模式，提高記憶上的關聯性，同時可選擇讓上游的結果自動交給下游。支援 OpenAI、Anthropic（Claude）、Google Gemini 與 Perplexity，可產出 PowerPoint、Word、Excel、PDF、程式碼、圖片與影片。

每次執行都會留下完整紀錄：判斷成什麼任務、為什麼選用該模型、查了哪些來源、實際花費多少，都顯示於決策窗，也可以匯出成文件。這是我的畢業專題，想解決的問題是：使用 AI 的人通常不知道它背後做了什麼，也不知道一次花了多少。

### 下載與安裝

至 [Releases](https://github.com/cat52011/cat5201-pro/releases) 下載最新版 zip 並完整解壓縮，執行 `cat5201.exe` 即可，無須安裝 .NET。

- 系統需求：Windows 10 或 11（64 位元）。
- `cat5201.exe` 須與 `Assets`、`LatoFont` 資料夾放在同一層。
- 程式未經數位簽章，首次開啟若出現「Windows 已保護您的電腦」，點選「其他資訊」→「仍要執行」即可。
- Windows 10 若畫面有區塊持續空白，請安裝 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。
- 選用：安裝 [LibreOffice](https://www.libreoffice.org/) 後，Word 預覽會呈現實際版面。

### 設定 API 金鑰

程式不附金鑰，呼叫費用由各自帳號支付。首次開啟會出現設定畫面，之後可由左下角齒輪 →「API」分頁修改，至少一把即可使用。

| 供應商 | 用途 | 申請金鑰 |
|---|---|---|
| OpenAI | GPT 模型、圖片生成 | https://platform.openai.com/api-keys |
| Anthropic | Claude 模型、「最佳品質」模式、資料分析 | https://console.anthropic.com/settings/keys |
| Perplexity | 即時搜尋（選填） | https://www.perplexity.ai/settings/api |
| Google Gemini | Gemini 模型、Veo 影片（選填） | https://aistudio.google.com/apikey |

建議至少備有 OpenAI 或 Anthropic 其中一把。金鑰以 Windows DPAPI 加密存放於本機，僅限該 Windows 帳號解密，不會上傳。亦可改用系統環境變數 `OPENAI_API_KEY`、`ANTHROPIC_API_KEY`、`GEMINI_API_KEY`、`PERPLEXITY_API_KEY`，環境變數的優先順序高於程式內填寫的金鑰。

### 基本操作

**提問**

1. 在畫布空白處點兩下，新增節點。
2. 於節點上半部輸入問題並送出，回答會顯示在下半部。
3. 需要附件時，以節點上的上傳按鈕加入檔案或照片，附件會隨存檔保存。

**選擇模型**

- 畫布右下角為 Auto 開關。開啟時，依任務內容自動選用模型。
- 關閉 Auto 後，以節點上的模型選單指定。手動指定時只使用選定的模型；僅在該模型執行失敗時改由其他模型完成，並於答案最上方註明替換的模型。

**產生檔案**

直接在要求裡寫明需要的產出，例如：

```
做一份 5 頁簡報，介紹澳洲特有美食
把這份資料整理成 Excel 表格
寫成一份書面報告
寫一個 Python 程式計算 BMI
```

送出後會先出現確認框，列出即將產生的檔案與約略時間。手動模式下，確認框可另外選擇撰寫的模型與是否配圖；Auto 模式依「個人化」的設定處理。

- 簡報頁數：要求 5 頁，即封面加內容共 5 頁，最後另附 1 頁資料來源；未查詢外部資料時，來源頁會如實註明。
- 製作方式：要求裡寫明「快速版」「最佳品質」「不配圖」時，依這一次的要求處理，不受「個人化」原本設定影響。判斷交由語意模型，換個說法或以英文表達皆可。
- 程式碼：會存成檔案置於節點下方，可直接預覽（含行號與語法上色）；同時產出報告與程式時，會分類列出。
- 檔名：與存檔相同，由模型取短標題，不帶時間戳記；同名時加 _2。

**資料分析**

附上資料檔（CSV、Excel）並要求產出報告或表格時，會在 Claude 的沙箱內實際執行程式：讀取完整資料、分析、繪圖，報告中的數字皆來自執行結果，另附可自行重現的 `.py` 與結果表。需要 Anthropic 金鑰，約 5 分鐘。沙箱沒有中文字型，圖表文字為英文。

**預覽與修改**

完成的檔案會顯示在節點下方，點選即可預覽。預覽視窗下方可以：

- 輸入修改要求，例如「第 3 頁改成比較表」。填寫頁碼只重做該頁，留空則整份重做。
- 以「上一版」「下一版」切換版本，無須重新生成。
- 以「用系統程式開啟」，用 PowerPoint、Word 等開啟原檔。

簡報與報告有兩種製作方式，於設定 →「個人化」→「簡報與報告品質」選擇：

- 最佳品質：由 Claude 設計每一頁，約 5 分鐘，費用較高。程式內預覽為近似版面，單頁僅能修改文字。
- 快速版：使用內建版型，速度快且費用低，預覽與檔案完全一致，單頁可連同版面重做。

**把節點串聯成流程**

1. 拖曳節點左上或右上的小圓點。拖到空白處會新增下游節點；拖到另一個節點上，則將其接為下游。
2. 在連接線上按右鍵，切換為藍色虛線。藍線代表「上游完成後自動執行下游」。
3. 在下游節點的輸入框寫 `{{input}}`，執行時會替換為上游的回答。例如：

```
比較以下兩份回答，列出彼此矛盾的地方，兩邊的說法都要寫出來：{{input}}
```

兩條以上的藍線接到同一個節點時，會等所有上游完成才開始，每個上游的回答皆完整放入輸入，地位相同。

**協作模式（多個模型一起回答）**

1. 在節點上按右鍵 →「協作模型…」，勾選 2 到 4 個模型後確定。
2. 送出後，各模型先獨立回答，再閱讀其他模型的回答進行交叉檢視。
3. 答案分為三段：各模型的回答、交叉檢視、共識與分歧。分歧中若為某模型轉述他方立場，會標明為轉述，並提示對照原始回答。

呼叫次數為模型數的兩倍，兩個模型即四次呼叫。關閉方式：右鍵 →「協作模型…」→「關閉協作」。

**決策窗**

點選節點後，畫面右上角的決策窗會顯示該次執行的細節：任務類型、選用的模型與原因、實際參與的每個模型、搜尋來源、token 數與費用，以及每一步收到的輸入。

詢問股價、匯率、新聞等會隨時間變動的資料時，會依查證程度在答案最上方提示，例如「僅有搜尋摘錄」「來源僅核對網域」「即時資料未取得」。

### 控制費用

- 設定 →「個人化」→「每日上限」，預設 NT$100。該次執行將超過上限時會先行詢問。
- 「API」分頁可填寫各服務的儲值餘額，之後每筆花費會即時扣除並顯示。

實測約略花費：

| 操作 | 約略花費 |
|---|---|
| 一般問答 | NT$1 以下 |
| 協作模式（兩個模型） | NT$3～5 |
| 3～5 頁簡報，快速版、不配圖 | 約 NT$2，十餘秒 |
| 5 頁簡報，最佳品質＋配圖 | NT$40～50，約 5 分鐘 |
| 附資料檔的分析報告（沙箱實際執行） | 約 NT$15，約 5 分鐘 |
| 報告完成後的獨立審查 | 每份 NT$1～10（可關閉） |

### 其他功能

- **獨立審查**：產出書面報告後，由獨立呼叫狀態的模型對照報告裡的數字、表格與程式算出的結果，找出前後矛盾，另存一份「審查清單」。列出問題與修改建議，不改動實際報告。Auto 模式由不同廠商的模型審查；手動模式只使用選定的模型，另開一次獨立呼叫。審查前會先以程式檢查 nan、#DIV/0!、未填完的模板字，不另計費。Claude 審查每份約 NT$1，GPT 審查約 NT$10，可於設定 →「個人化」→「報告完成後獨立審查」關閉。
- **AI 使用紀錄**：決策窗右上角「匯出紀錄」，把該次執行整理成一份文件：哪些決策是你做的、哪些是系統做的、每一步由哪個模型完成、花了多少、產出哪些檔案。由程式依執行帳目產生，不經 AI 改寫，可以直接附在需要揭露 AI 使用的作業或報告上。
- **記憶與偏好**：在節點上按右鍵 →「將此節點加入記憶」，保存長期事實。回答的語言與風格於設定 →「個人化」→「記憶與偏好」調整。
- **技能（Skills）**：設定 →「Skills」，將常用的指示或格式模板存為技能，每次執行自動帶入；亦可貼上 GitHub 上 SKILL.md 的連結直接匯入。
- **手機鏡像**：設定 →「個人化」→「手機鏡像」，開啟「鏡像模式」後，手機連上同一個 Wi-Fi 並掃描 QR code，即可查看執行進度。預設為「唯讀模式」；關閉後，手機才能停止或重跑節點、下指令與回答確認框。
- **Google Drive**：於「API」分頁連結 Google 帳戶後，產出的檔案可自動上傳至 Drive。
- **存檔與最近刪除**：左側列表為存檔。刪除的存檔連同附件保留 7 天，可於「最近刪除」復原。

### 回報問題

設定 →「回報問題」會將最近 7 天的日誌打包成 zip 置於桌面，API 金鑰會自動遮蔽，將該檔案傳給我即可。亦可直接在本 repo 開 Issue，並附上截圖與當次的輸入。

程式資料（存檔、附件、日誌）皆位於 `%LOCALAPPDATA%\cat5201-pro`，不需保留時刪除該資料夾即可。

### 已知限制

- 「最佳品質」模式約需 5 分鐘。
- 程式內的簡報預覽為近似版面，陰影、漸層等效果以 PowerPoint 開啟為準。
- 未安裝 LibreOffice 時，Word 預覽僅顯示文字內容，實際版面請以「用系統程式開啟」查看。
- Gemini 無法進行即時搜尋，選用 Gemini 時改由其他模型查詢資料，並於答案最上方註明。
- 數字單位檢查（例如 billion 被寫成「億」）只比對聊天中的回答，不檢查產出的簡報或報告檔案。
- 只支援 Windows。介面為 WPF；核心 `cat5201.Core`（模型路由、成本、任務編排、審查、使用紀錄）以 net8.0 撰寫，不引用 WPF，因此移植到 macOS、Linux 或網頁版主要是重寫介面層。核心目前仍有兩處依賴 Windows，移植時須替換：金鑰加密使用的 DPAPI，以及簡報圖片處理使用的 System.Drawing。
- 手機鏡像使用區網內未加密的 HTTP，存取權杖位於網址中。預設為唯讀模式；僅在信任的 Wi-Fi 上關閉唯讀，使用完畢即關閉鏡像模式。

### 給開發者

需要 .NET 8 SDK。

```powershell
# 執行
dotnet run --project cat5201.csproj

# 測試（xUnit，1015 項）
dotnet test tests\cat5201.Tests\cat5201.Tests.csproj

# 打包成單一 exe，輸出到 publish\win-x64\
.\publish.ps1
```

專案結構：

```
cat5201.Core/    與介面無關的核心（net8.0）：模型註冊與路由、成本估算、任務編排、
                 決策紀錄、輸出判斷。執行核心只依賴 IAgentHost / INodeContext 介面，
                 不引用任何 WPF 型別。執行核心 AgentRuntime 依職責分成多個檔案：
                 主流程、文件、簡報、配圖、圖片與影片、資料分析、獨立審查、最終整合、程式修改。
cat5201.csproj   WPF 桌面前端（net8.0-windows）：畫布、節點、決策窗、手機鏡像伺服器
tests/           xUnit 測試；評測題目等測試資料在 tests/cat5201.Tests/TestData/
```

版本號只在 `cat5201.csproj` 的 `<Version>` 設定一次，標題列與檔案內容頁會自動讀取。日誌位於 `%LOCALAPPDATA%\cat5201-pro\logs\`，保留 30 天。

### 授權

本軟體不是開源軟體，著作權人保留一切權利。允許為評估、學術審查與面試展示目的檢視原始碼與執行程式；其他用途請先取得書面同意。詳見 [LICENSE](LICENSE)，第三方元件的授權見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## English

cat5201-pro is a Windows desktop AI workbench laid out like a mind map. Each node on the canvas is one AI interaction, answered by one model or by several models working independently or together. Nodes can be chained upstream to downstream, which keeps related context connected, and an upstream result can optionally be passed to the downstream node automatically. It works with OpenAI, Anthropic (Claude), Google Gemini and Perplexity, and can produce PowerPoint, Word, Excel and PDF files, code, images and video.

Every run leaves a full record: what kind of task it was judged to be, why a given model was chosen, which sources were consulted, and what the run actually cost. All of it is shown in the decision panel and can be exported as a document. This is my graduation project. The problem it addresses is that people using AI usually can't see what happened behind an answer or how much it cost.

### Download and install

Download the latest zip from [Releases](https://github.com/cat52011/cat5201-pro/releases), extract all of it, and run `cat5201.exe`. No .NET installation is needed.

- Requirements: Windows 10 or 11 (64-bit).
- `cat5201.exe` must stay in the same folder as `Assets` and `LatoFont`.
- The program is not code-signed. If "Windows protected your PC" appears on first launch, choose "More info" → "Run anyway".
- On Windows 10, if parts of the window stay blank, install the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- Optional: with [LibreOffice](https://www.libreoffice.org/) installed, the Word preview shows the actual layout.

### API keys

No keys are included; calls are billed to your own accounts. A setup screen appears on first launch, and keys can be changed later under the gear icon (bottom left), "API" tab. One key is enough.

| Provider | Used for | Get a key |
|---|---|---|
| OpenAI | GPT models, image generation | https://platform.openai.com/api-keys |
| Anthropic | Claude models, "best quality" mode, data analysis | https://console.anthropic.com/settings/keys |
| Perplexity | Live search (optional) | https://www.perplexity.ai/settings/api |
| Google Gemini | Gemini models, Veo video (optional) | https://aistudio.google.com/apikey |

At least one of OpenAI or Anthropic is recommended. Keys are encrypted with Windows DPAPI and stored locally; only that Windows account can decrypt them, and they are never uploaded. The environment variables `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY` and `PERPLEXITY_API_KEY` can be used instead and take priority over keys entered in the app.

### Basic use

**Asking a question**

1. Double-click an empty spot on the canvas to add a node.
2. Type the question in the top half of the node and send it; the answer appears in the bottom half.
3. To attach files or photos, use the upload button on the node. Attachments are saved with the project.

**Choosing a model**

- The Auto switch is at the bottom right of the canvas. When it is on, a model is chosen based on the task.
- With Auto off, choose a model from the node's model menu. Only that model is used; if it fails, another model completes the task and the replacement is noted at the top of the answer.

**Producing files**

State the output you need in the request, for example:

```
做一份 5 頁簡報，介紹澳洲特有美食
(Make a 5-page deck about foods unique to Australia)
寫一個 Python 程式計算 BMI
(Write a Python program that calculates BMI)
```

A confirmation dialog appears first, listing the files to be made and the approximate time. In manual mode the dialog also lets you choose which model writes the file and whether to add images; in Auto mode these follow the Personalization settings.

- Deck length: "5 pages" means the cover plus content pages total 5, with one extra sources page at the end. If no outside sources were consulted, the sources page says so.
- How it is made: if a request says "quick version", "best quality" or "no images", that request is followed regardless of the Personalization defaults. The meaning is judged by a semantic model, so other wordings and English work as well.
- Code: saved as files under the node and previewable with line numbers and syntax highlighting. When a report and code are produced together, they are listed by category.
- File names: like projects, files get a short title chosen by the model, without a timestamp; a duplicate name gets _2.

**Data analysis**

When a data file (CSV, Excel) is attached and a report or table is requested, the work runs as real code in Claude's sandbox: it reads the full data, analyses it and draws the charts, so every number in the report comes from an actual run. A reproducible `.py` and a results spreadsheet are delivered as well. Requires an Anthropic key and takes about 5 minutes. The sandbox has no Chinese fonts, so chart text is in English.

**Preview and revise**

Finished files appear under the node; click one to preview it. At the bottom of the preview you can:

- Type a change, such as "turn page 3 into a comparison table". Enter a page number to redo only that page, or leave it blank to redo the whole file.
- Use "上一版 / 下一版" (previous / next version) to switch versions without regenerating.
- Use "用系統程式開啟" (open with the system app) to open the original file in PowerPoint, Word and so on.

Decks and reports can be made in two ways, chosen under the gear icon, "個人化" (Personalization), "簡報與報告品質" (deck and report quality):

- Best quality: Claude designs every page. About 5 minutes and costs more. The in-app preview is an approximation, and single pages can only be edited as text.
- Quick version: built-in templates. Fast and cheap, the preview matches the file exactly, and a single page can be redone including its layout.

**Chaining nodes into a flow**

1. Drag the small dot at the top left or top right of a node. Dropping it on empty space creates a downstream node; dropping it on another node connects that node downstream.
2. Right-click the connecting line to turn it into a blue dashed line. A blue line means "run the downstream node automatically when the upstream one finishes".
3. Put `{{input}}` in the downstream node's prompt; it is replaced with the upstream answer when the node runs.

When two or more blue lines lead into one node, it waits until every upstream node has finished. All upstream answers are included in full and treated equally.

**Collaboration mode (several models answer together)**

1. Right-click a node, choose "協作模型…" (collaboration models), tick 2 to 4 models, and confirm.
2. After sending, each model answers independently, then reads the others' answers and cross-reviews them.
3. The result has three parts: each model's answer, the cross-review, and agreements and disagreements. When a model restates another's position, it is labelled as a paraphrase and you are pointed to the original answer.

The number of calls is twice the number of models, so two models means four calls. To turn it off: right-click → "協作模型…" → "關閉協作" (turn off).

**The decision panel**

Click a node and the decision panel at the top right shows the details of that run: task type, the chosen model and why, every model that took part, search sources, tokens and cost, and the input each step received.

For data that changes over time, such as stock prices, exchange rates or news, a notice at the top of the answer states how well the data was verified, for example "search snippets only", "source domain checked only" or "live data not retrieved".

### Controlling cost

- Gear icon, "個人化" (Personalization), "每日上限" (daily limit). The default is NT$100; if a run would go over it, you are asked first.
- In the "API" tab you can enter each provider's account balance; spending is deducted and shown as you go.

Approximate costs measured in testing:

| Task | Approximate cost |
|---|---|
| Ordinary question | under NT$1 |
| Collaboration mode (two models) | NT$3–5 |
| 3–5 slide deck, quick version, no images | about NT$2, under half a minute |
| 5-slide deck, best quality with images | NT$40–50, about 5 minutes |
| Analysis report from a data file (real code in the sandbox) | about NT$15, about 5 minutes |
| Independent review after a report | NT$1–10 per report (can be turned off) |

### Other features

- **Independent review**: after a written report is produced, a model in a separate, independent call compares the numbers in the report against its tables and the computed results, finds contradictions, and saves a separate "審查清單" (review checklist). It lists problems and suggested fixes without changing the report itself. In Auto mode the reviewer comes from a different provider; in manual mode only the chosen model is used, in a separate call. A program check for nan, #DIV/0! and unfilled template text runs first at no cost. A review costs about NT$1 per report with Claude and about NT$10 with GPT, and can be turned off under Personalization, "報告完成後獨立審查" (review after the report is done).
- **AI usage record**: "匯出紀錄" (export record) at the top right of the decision panel turns a run into a document: which decisions were yours and which were the system's, which model completed each step, what it cost, and which files it produced. It is generated by the program from the run's ledger, not rewritten by AI, and can be attached directly to coursework or reports that require disclosure of AI use.
- **Memory and preferences**: right-click a node and choose "將此節點加入記憶" (add to memory) to keep a long-term fact. Answer language and style are set under Personalization, "記憶與偏好" (memory and preferences).
- **Skills**: gear icon, "Skills" tab. Save instructions or format templates you use often; they are added to every run. A link to a SKILL.md file on GitHub can also be pasted to import it.
- **Phone mirror**: Personalization, "手機鏡像" (phone mirror). Turn on "鏡像模式" (mirror mode), connect the phone to the same Wi-Fi and scan the QR code to follow progress. "唯讀模式" (read-only mode) is on by default; with it off, the phone can stop or rerun nodes, issue commands and answer confirmation dialogs.
- **Google Drive**: after linking a Google account in the "API" tab, generated files can be uploaded to Drive automatically.
- **Projects and recently deleted**: the list on the left holds your projects. Deleted projects, with their attachments, are kept for 7 days and can be restored from "最近刪除" (recently deleted).

### Reporting problems

Gear icon, "回報問題" (Report a problem): this packs the last 7 days of logs into a zip on the desktop, with API keys masked; send me that file. Issues can also be opened in this repo, with a screenshot and the input used.

All app data (projects, attachments, logs) is in `%LOCALAPPDATA%\cat5201-pro`; delete that folder if nothing needs to be kept.

### Known limitations

- "Best quality" mode takes about 5 minutes.
- The in-app deck preview is an approximation; shadows, gradients and similar effects are accurate only in PowerPoint.
- Without LibreOffice, the Word preview shows text only; view the actual layout with "用系統程式開啟" (open with the system app).
- Gemini cannot do live search; when Gemini is selected, another model looks up the data and this is noted at the top of the answer.
- The number-unit check (for example "billion" written as the wrong Chinese unit) only looks at the chat answer, not at generated deck or report files.
- Windows only. The UI is WPF; the core, `cat5201.Core` (model routing, costs, task orchestration, review, usage records), targets net8.0 and references no WPF, so porting to macOS, Linux or the web mainly means rewriting the UI layer. Two parts of the core still depend on Windows and would need replacing: DPAPI for key encryption, and System.Drawing for deck image processing.
- The phone mirror uses unencrypted HTTP on the local network, with the access token in the URL. Read-only mode is on by default; only turn it off on a Wi-Fi network you trust, and turn mirror mode off when you are done.

### For developers

Requires the .NET 8 SDK.

```powershell
# Run
dotnet run --project cat5201.csproj

# Test (xUnit, 1015 tests)
dotnet test tests\cat5201.Tests\cat5201.Tests.csproj

# Build a single-file exe into publish\win-x64\
.\publish.ps1
```

Project layout:

```
cat5201.Core/    UI-independent core (net8.0): model registry and routing, cost estimates,
                 task orchestration, decision records, output detection. The runtime depends
                 only on the IAgentHost / INodeContext interfaces and references no WPF types.
                 The runtime, AgentRuntime, is split into files by responsibility: main flow,
                 documents, decks, illustrations, images and video, data analysis, independent
                 review, final synthesis, and code edits.
cat5201.csproj   WPF desktop front end (net8.0-windows): canvas, nodes, decision panel,
                 phone mirror server
tests/           xUnit tests; fixtures such as evaluation tasks are in tests/cat5201.Tests/TestData/
```

The version number is set once, in `<Version>` in `cat5201.csproj`; the title bar and file properties read it from there. Logs are in `%LOCALAPPDATA%\cat5201-pro\logs\` and kept for 30 days.

### License

This is not open-source software; all rights are reserved. You may view the source and run the program for evaluation, academic review and interview demonstration. Any other use needs written permission. See [LICENSE](LICENSE), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party components.
