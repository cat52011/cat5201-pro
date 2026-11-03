# cat5201-pro

[中文](#中文) | [English](#english)

---

## 中文

cat5201-pro 是一個 Windows 桌面程式，用「節點」把多個 AI 模型串起來做事。每個節點是一次提問和回答，可以指定由哪個模型回答，也可以把節點接起來，讓上一步的結果自動交給下一步。支援 OpenAI、Anthropic（Claude）、Google Gemini 和 Perplexity，可以產出 PowerPoint、Word、Excel、PDF、圖片與影片。

每次執行都會留下完整紀錄：程式把這次判斷成什麼任務、為什麼選這個模型、查了哪些來源、實際花了多少錢。這是我的畢業專題，想解決的問題是：用 AI 的人通常不知道它背後做了什麼，也不知道一次花了多少。

### 下載與安裝

到 [Releases](https://github.com/cat52011/cat5201-pro/releases) 下載最新版的 zip，整個解壓縮後，點兩下 `cat5201.exe`。不需要安裝 .NET。

- 需要 Windows 10 或 11（64 位元）。
- `cat5201.exe` 旁邊的 `Assets`、`LatoFont` 資料夾要一起留著。
- 程式沒有數位簽章，第一次開啟時 Windows 可能顯示「Windows 已保護您的電腦」，按「其他資訊」→「仍要執行」。
- Windows 10 如果有區塊一直空白，請安裝 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

### 設定 API 金鑰

程式不附金鑰，呼叫費用由你自己的帳號支付。第一次開啟會出現設定畫面，之後可以按左下角的齒輪，到「API」分頁修改。至少填一把就能使用。

| 供應商 | 用途 | 申請金鑰 |
|---|---|---|
| OpenAI | GPT 模型、圖片生成 | https://platform.openai.com/api-keys |
| Anthropic | Claude 模型、簡報「最佳品質」模式 | https://console.anthropic.com/settings/keys |
| Perplexity | 即時搜尋（選填） | https://www.perplexity.ai/settings/api |
| Google Gemini | Gemini 模型、Veo 影片（選填） | https://aistudio.google.com/apikey |

建議至少有 OpenAI 或 Anthropic 其中一把。金鑰用 Windows DPAPI 加密後存在本機，只有你的 Windows 帳號解得開，不會上傳到任何地方。也可以改用系統環境變數 `OPENAI_API_KEY`、`ANTHROPIC_API_KEY`、`GEMINI_API_KEY`、`PERPLEXITY_API_KEY`，環境變數的優先順序比程式裡填的高。

### 基本操作

**提問**

1. 在畫布空白處點兩下，新增一個節點。
2. 在節點上半部輸入問題，按送出。回答會出現在下半部。
3. 需要附件時，用節點上的上傳按鈕加入檔案或照片，附件會跟著存檔保存。

**選擇模型**

- 畫布右下角有 Auto 開關。開啟時，程式依任務內容自動挑模型。
- 關閉 Auto 後，用節點上的模型選單指定。手動指定時只會用你選的模型；只有在那個模型執行失敗時，程式才會改用其他模型完成，並在答案最上方寫明換成了誰。

**產生檔案**

直接在要求裡寫你要什麼，例如：

```
做一份 5 頁簡報，介紹澳洲特有美食
把這份資料整理成 Excel 表格
寫成一份書面報告
```

送出後會先跳出確認框，列出要產生的檔案和大約需要的時間。手動模式下，確認框裡還可以選由哪個模型撰寫、要不要配圖；Auto 模式依「個人化」的設定處理。

簡報頁數的算法：你說 5 頁，就是封面加內容共 5 頁，最後另外附 1 頁資料來源。沒有查外部資料時，來源頁會照實寫明。

**預覽與修改**

完成的檔案會出現在節點下方，點一下就能預覽。預覽視窗下方可以：

- 輸入修改要求，例如「第 3 頁改成比較表」。填了頁碼只重做那一頁，留空則整份重做。
- 按「上一版」「下一版」切換版本，不用重新生成。
- 按「用系統程式開啟」，用 PowerPoint、Word 等開啟原檔。

簡報有兩種製作方式，在設定 →「個人化」選擇：

- 最佳品質：由 Claude 設計每一頁，約 5 分鐘，費用較高。程式內預覽是近似版面，單頁只能改文字。
- 快速版：用程式內建的版型，快也便宜，預覽和檔案完全一致，單頁可以連版面一起重做。

**把節點接成流程**

1. 拖節點左上或右上的小圓點。拖到空白處會新增一個下游節點；拖到另一個節點上，會把它接成下游。
2. 在連接線上按右鍵，切換成藍色虛線。藍線代表「上游跑完後自動執行下游」。
3. 在下游節點的輸入框寫 `{{input}}`，執行時會換成上游的回答。例如：

```
比較以下兩份回答，列出彼此矛盾的地方，兩邊的說法都要寫出來：{{input}}
```

兩條以上的藍線接到同一個節點時，它會等所有上游都完成才開始，每個上游的回答都完整放進輸入，地位相同。

**協作模式（多個模型一起回答）**

1. 在節點上按右鍵 →「協作模型…」，勾選 2 到 4 個模型，按確定。
2. 送出問題後，每個模型先各自回答，接著讀過其他模型的回答再做一次檢視。
3. 答案分三段：各模型的回答、交叉檢視、共識與分歧。分歧裡如果是某個模型轉述別人的立場，會標明是轉述，並請你對照原本的回答。

呼叫次數是模型數的兩倍，兩個模型就是四次呼叫。要關閉時，右鍵 →「協作模型…」→「關閉協作」。

**看決策窗**

點一下節點，畫面右上角的決策窗會顯示那次執行的細節：任務類型、選用的模型和原因、實際參與的每個模型、搜尋來源、token 數與費用，以及每一步收到的輸入。

詢問股價、匯率、新聞這類會隨時間改變的資料時，程式會依查證程度在答案最上方提醒，例如「僅有搜尋摘錄」「來源僅核對網域」「即時資料未取得」。

### 控制費用

- 設定 →「個人化」→「每日上限」，預設 NT$100。這次執行會超過上限時，程式會先問你。
- 「API」分頁可以填各服務的儲值餘額，之後每一筆花費都會即時扣除顯示。

實際測試時的大約花費：

| 操作 | 約略花費 |
|---|---|
| 一般問答 | NT$1 以下 |
| 協作模式（兩個模型） | NT$3～5 |
| 5 頁簡報，快速版、不配圖 | 約 NT$2 |
| 5 頁簡報，最佳品質＋配圖 | NT$40～50，約 5 分鐘 |

### 其他功能

- **記憶與偏好**：在節點上按右鍵 →「將此節點加入記憶」，把長期事實記下來。回答的語言和風格在設定 →「個人化」→「記憶與偏好」調整。
- **技能（Skills）**：設定 →「Skills」，把常用的指示或格式模板存成技能，每次執行時自動帶入；也可以貼上 GitHub 上 SKILL.md 的連結直接匯入。
- **手機鏡像**：設定 →「個人化」→「手機鏡像」。手機連上同一個 Wi-Fi 後掃 QR code，就能在手機上看執行進度、停止或重跑節點、回答確認框。
- **Google Drive**：在「API」分頁連結 Google 帳戶後，產出的檔案可以自動上傳到你的 Drive。
- **存檔與最近刪除**：左側列表是你的存檔。刪掉的存檔連同附件會保留 7 天，可以在「最近刪除」救回。

### 回報問題

設定 →「回報問題」會把最近 7 天的日誌打包成 zip 放在桌面，API 金鑰會自動遮蔽，把這個檔案傳給我即可。也歡迎直接在這個 repo 開 Issue，附上截圖和那次的輸入。

程式的資料（存檔、附件、日誌）都在 `%LOCALAPPDATA%\cat5201-pro`。不想保留時，刪除這個資料夾即可。

### 已知限制

- 簡報「最佳品質」模式約需 5 分鐘。
- 程式內的簡報預覽是近似版面，陰影、漸層等效果以 PowerPoint 開啟為準。
- Gemini 不能做即時搜尋。選 Gemini 時，程式會改用其他模型查資料，並在答案最上方註明。
- 數字單位檢查（例如 billion 被寫成「億」）只比對聊天中的回答，不檢查產出的簡報或報告檔案。

### 給開發者

需要 .NET 8 SDK。

```powershell
# 執行
dotnet run --project cat5201.csproj

# 測試（xUnit，973 項）
dotnet test tests\cat5201.Tests\cat5201.Tests.csproj

# 打包成單一 exe，輸出到 publish\win-x64\
.\publish.ps1
```

專案結構：

```
cat5201.Core/    與介面無關的核心（net8.0）：模型註冊與路由、成本估算、任務編排、
                 決策紀錄、輸出判斷。執行核心只依賴 IAgentHost / INodeContext 介面，
                 不引用任何 WPF 型別。
cat5201.csproj   WPF 桌面前端（net8.0-windows）：畫布、節點、決策窗、手機鏡像伺服器
tests/           xUnit 測試；評測題目等測試資料在 tests/cat5201.Tests/TestData/
```

版本號只在 `cat5201.csproj` 的 `<Version>` 設定一次，標題列和檔案內容頁會自動讀取。日誌在 `%LOCALAPPDATA%\cat5201-pro\logs\`，保留 30 天。

### 授權

本軟體不是開源軟體，著作權人保留一切權利。允許為評估、學術審查與面試展示目的檢視原始碼與執行程式；其他用途請先取得書面同意。詳見 [LICENSE](LICENSE)，第三方元件的授權見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## English

cat5201-pro is a Windows desktop app that chains AI models together on a canvas of "nodes". Each node is one prompt and its answer. You can pick which model answers each node, and you can connect nodes so one step's output feeds the next. It works with OpenAI, Anthropic (Claude), Google Gemini and Perplexity, and can produce PowerPoint, Word, Excel and PDF files, images and video.

Every run leaves a full record: what kind of task the app decided it was, why it picked a given model, which sources it searched, and what the run actually cost. This is my graduation project. The problem it addresses is that people using AI tools usually can't see what happened behind an answer or how much it cost.

### Download and install

Download the latest zip from [Releases](https://github.com/cat52011/cat5201-pro/releases), extract the whole folder, and double-click `cat5201.exe`. No .NET installation is needed.

- Requires Windows 10 or 11 (64-bit).
- Keep the `Assets` and `LatoFont` folders next to `cat5201.exe`.
- The app is not code-signed. If Windows shows "Windows protected your PC", click "More info" and then "Run anyway".
- On Windows 10, if parts of the window stay blank, install the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

### API keys

The app ships without keys; calls are billed to your own accounts. A setup screen appears on first launch. Later you can change keys under the gear icon (bottom left), "API" tab. One key is enough to start.

| Provider | Used for | Get a key |
|---|---|---|
| OpenAI | GPT models, image generation | https://platform.openai.com/api-keys |
| Anthropic | Claude models, "best quality" slide decks | https://console.anthropic.com/settings/keys |
| Perplexity | Live web search (optional) | https://www.perplexity.ai/settings/api |
| Google Gemini | Gemini models, Veo video (optional) | https://aistudio.google.com/apikey |

At least one of OpenAI or Anthropic is recommended. Keys are encrypted with Windows DPAPI and stored locally; only your Windows account can decrypt them, and they are never uploaded. You can also use the environment variables `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY` and `PERPLEXITY_API_KEY`, which take priority over keys entered in the app.

### Basic use

**Asking a question**

1. Double-click an empty spot on the canvas to add a node.
2. Type your question in the top half of the node and send it. The answer appears in the bottom half.
3. To attach files or photos, use the upload button on the node. Attachments are saved with the project.

**Choosing a model**

- The Auto switch is at the bottom right of the canvas. When it is on, the app picks a model based on the task.
- With Auto off, choose a model from the node's model menu. The app then uses only that model. If it fails, the app finishes with another model and says so at the top of the answer.

**Producing files**

Say what you want in the request, for example:

```
做一份 5 頁簡報，介紹澳洲特有美食
(Make a 5-page deck about foods unique to Australia)
```

A confirmation dialog appears first, listing the files to be made and roughly how long it will take. In manual mode the dialog also lets you choose which model writes the file and whether to add illustrations. In Auto mode these follow your personalization settings.

How deck length works: "5 pages" means the cover plus content pages total 5, with one extra sources page at the end. If no outside sources were used, the sources page says so.

**Preview and revise**

Finished files appear under the node; click one to preview it. At the bottom of the preview you can:

- Type a change, such as "turn page 3 into a comparison table". Enter a page number to redo only that page, or leave it blank to redo the whole file.
- Use "上一版 / 下一版" (previous / next version) to switch versions without regenerating.
- Use "用系統程式開啟" (open with system app) to open the file in PowerPoint, Word and so on.

There are two ways to build decks, chosen under the gear icon, "個人化" (Personalization) tab:

- Best quality: Claude designs every page. About 5 minutes and costs more. The in-app preview is an approximation, and single pages can only be edited as text.
- Fast: built-in templates. Quick and cheap, the preview matches the file exactly, and a single page can be redone including its layout.

**Connecting nodes into a flow**

1. Drag the small dot at the top left or top right of a node. Dropping it on empty space creates a downstream node; dropping it on another node connects that node downstream.
2. Right-click the connecting line to turn it into a blue dashed line. A blue line means "run the downstream node automatically when the upstream one finishes".
3. Put `{{input}}` in the downstream node's prompt. It is replaced with the upstream answer when the node runs.

When two or more blue lines lead into one node, that node waits until every upstream node has finished. All upstream answers are included in full and treated equally.

**Collaboration mode (several models answer together)**

1. Right-click a node, choose "協作模型…" (collaboration models), tick 2 to 4 models, and confirm.
2. After you send a question, each model answers on its own, then reads the others' answers and reviews them.
3. The result has three parts: each model's answer, the cross-review, and agreements and disagreements. When a model restates another model's position, it is labelled as a paraphrase and you are pointed to the original answer.

The number of calls is twice the number of models, so two models means four calls. To turn it off, right-click, "協作模型…", then "關閉協作" (turn off).

**The decision panel**

Click a node and the decision panel at the top right shows the details of that run: task type, the chosen model and why, every model that actually took part, search sources, tokens and cost, and the input each step received.

For questions about data that changes over time, such as stock prices, exchange rates or news, the app adds a notice at the top of the answer saying how well the data was verified, for example "search snippets only", "source domain checked only" or "live data not retrieved".

### Controlling cost

- Gear icon, "個人化" (Personalization), "每日上限" (daily limit). The default is NT$100. If a run would go over it, the app asks first.
- In the "API" tab you can enter each provider's account balance, and the app deducts spending from it as you go.

Approximate costs from my own testing:

| Task | Approximate cost |
|---|---|
| Ordinary question | under NT$1 |
| Collaboration mode (two models) | NT$3–5 |
| 5-page deck, fast mode, no illustrations | about NT$2 |
| 5-page deck, best quality with illustrations | NT$40–50, about 5 minutes |

### Other features

- **Memory and preferences**: right-click a node and choose "將此節點加入記憶" (add to memory) to keep a long-term fact. Answer language and style are set under Personalization, "記憶與偏好" (memory and preferences).
- **Skills**: gear icon, "Skills" tab. Save instructions or format templates you use often; they are added to every run. You can also paste a link to a SKILL.md file on GitHub to import it.
- **Phone mirror**: Personalization, "手機鏡像" (phone mirror). With your phone on the same Wi-Fi, scan the QR code to watch progress, stop or rerun nodes, and answer confirmation dialogs from the phone.
- **Google Drive**: after linking a Google account in the "API" tab, generated files can be uploaded to your Drive automatically.
- **Projects and recently deleted**: the list on the left holds your projects. Deleted projects, with their attachments, are kept for 7 days under "最近刪除" (recently deleted).

### Reporting problems

Gear icon, "回報問題" (report a problem): this packs the last 7 days of logs into a zip on your desktop, with API keys masked. Send me that file, or open an Issue in this repo with a screenshot and the input you used.

All app data (projects, attachments, logs) lives in `%LOCALAPPDATA%\cat5201-pro`. Delete that folder to remove everything.

### Known limitations

- Best-quality decks take about 5 minutes.
- The in-app deck preview is an approximation; shadows and gradients look right only in PowerPoint.
- Gemini cannot do live search. When Gemini is selected, another model does the searching and the answer says so at the top.
- The number-unit check (for example "billion" written as the wrong Chinese unit) only looks at the chat answer, not at generated deck or report files.

### For developers

Requires the .NET 8 SDK.

```powershell
# Run
dotnet run --project cat5201.csproj

# Test (xUnit, 973 tests)
dotnet test tests\cat5201.Tests\cat5201.Tests.csproj

# Build a single-file exe into publish\win-x64\
.\publish.ps1
```

Project layout:

```
cat5201.Core/    UI-independent core (net8.0): model registry and routing, cost estimates,
                 task orchestration, decision records, output detection. The runtime depends
                 only on the IAgentHost / INodeContext interfaces and references no WPF types.
cat5201.csproj   WPF desktop front end (net8.0-windows): canvas, nodes, decision panel,
                 phone mirror server
tests/           xUnit tests; fixtures such as evaluation tasks are in tests/cat5201.Tests/TestData/
```

The version number is set once, in `<Version>` in `cat5201.csproj`; the title bar and file properties read it from there. Logs are in `%LOCALAPPDATA%\cat5201-pro\logs\` and kept for 30 days.

### License

This is not open-source software; all rights are reserved. You may view the source and run the program for evaluation, academic review and interview demonstration. Any other use needs written permission. See [LICENSE](LICENSE), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party components.
