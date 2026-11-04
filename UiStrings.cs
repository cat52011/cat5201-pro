using System.Collections.Generic;

namespace Cat5201
{
    /// <summary>
    /// 介面共用的中→英對照表（XAML 的 {local:Tr '…'} 與 UiLanguage.T 查這裡）。
    ///
    /// 鍵是畫面上的中文原文，一字不差。XAML 裡改了中文，這裡也要跟著改，
    /// 否則英文模式會退回顯示中文（不會壞，但會露餡）。
    /// 只在程式碼一處用到的字串，直接寫 UiLanguage.P("中文", "English") 就好，不必登記到這裡。
    /// </summary>
    internal static class UiStrings
    {
        private static bool _registered;

        public static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            UiLanguage.Register(En);
        }

        private static readonly Dictionary<string, string> En = new()
        {
            // ===== 主畫面 =====
            ["新增"] = "New",
            ["目前檔案：尚未建立"] = "Current file: not created yet",
            ["個人化設定"] = "Personal",
            ["讓我們有個新的開始！"] = "Let's make a fresh start!",
            ["開始"] = "Start",
            ["決策可視化"] = "Decision view",
            ["匯出紀錄"] = "Export log",
            ["匯出這個節點最近一次執行的 AI 使用紀錄（誰做了哪一段、哪些決策是誰提的、花了多少）"] =
                "Export the AI usage record of this node's latest run (who did which part, who made which decision, how much it cost)",
            ["放大決策窗"] = "Expand decision panel",
            ["模式"] = "Mode",
            ["策略"] = "Strategy",
            ["模型"] = "Model",
            ["這次實際參與"] = "Used this run",
            ["任務"] = "Task",
            ["點一下開啟 API 狀態頁（填儲值金額、重新檢查）"] = "Click to open the API status page (enter balances, re-check)",
            ["API 餘額"] = "API balance",
            ["還沒有花費紀錄。到「個人化 → API」填入儲值金額，這裡就會顯示估計剩餘。"] =
                "No spending recorded yet. Enter your balances under Personal → API and the estimated remaining amount will show here.",
            ["最近刪除"] = "Recently deleted",
            ["刪掉的存檔連同附件與該存檔的記憶都留在這裡，7 天後自動永久刪除。全域記憶不受影響。"] =
                "Deleted projects stay here with their attachments and project memories, and are permanently deleted after 7 days. Global memories are not affected.",
            ["沒有最近刪除的存檔。"] = "No recently deleted projects.",
            ["復原"] = "Restore",
            ["永久刪除"] = "Delete permanently",

            // ===== 設定視窗 =====
            ["設定"] = "Settings",
            ["關閉"] = "Close",
            ["個人化"] = "Personal",
            ["回報問題"] = "Report",   // 設定分頁那一排很窄（440px），太長會把右邊的 ? 擠掉；完整說明在提示文字裡
            ["把最近 7 天的日誌打包到桌面（自動遮蔽 API 金鑰），寄給開發者"] =
                "Pack the last 7 days of logs onto your desktop (API keys are masked) so you can send them to the developer",
            ["使用說明"] = "Help",
            ["語言"] = "Language",
            ["介面顯示的語言。AI 的回答一律跟著你提問的語言。"] =
                "The language of the interface. AI answers always follow the language you ask in.",
            ["記憶與偏好"] = "Memory & preferences",
            ["記住"] = "Remember",
            ["當前記憶"] = "Current memories",
            ["目前沒有任何記憶。"] = "No memories yet.",
            ["刪除這條記憶"] = "Delete this memory",
            ["一鍵清除全部"] = "Clear all",
            ["多階段任務自動展開"] = "Multi-step task expansion",
            ["一鍵展開並自動執行（建議）"] = "Expand and run with one click (recommended)",
            ["完全自動（送出後立即展開並執行）"] = "Fully automatic (expand and run right after sending)",
            ["關閉（只用單一節點作答）"] = "Off (answer with a single node only)",
            ["配圖品質"] = "Illustration quality",
            ["簡報、報告以及影片第一格之配圖，單純圖片不受限制"] =
                "Applies to illustrations in decks, reports and the first frame of videos. Standalone images are not affected.",
            ["簡報與報告品質"] = "Deck & report quality",
            ["最佳品質（Claude 親自設計版面）"] = "Best quality (Claude designs the layout)",
            ["快速版（內建版型，快又便宜）"] = "Fast (built-in templates, quick and cheap)",
            ["簡報生成器（快速版）"] = "Deck generator (fast mode)",
            ["Claude（建議）"] = "Claude (recommended)",
            ["影片設定"] = "Video",
            ["Veo 3.1 標準版（約 US$0.40/秒）"] = "Veo 3.1 Standard (about US$0.40/sec)",
            ["Veo 3.1 Fast（約 US$0.10/秒）"] = "Veo 3.1 Fast (about US$0.10/sec)",
            ["Veo 3.1 Lite（約 US$0.05/秒）"] = "Veo 3.1 Lite (about US$0.05/sec)",
            ["風格"] = "Style",
            ["恢復原廠預設風格"] = "Restore default style",
            ["任務指定模型"] = "Model per task",
            ["產出檔案"] = "Output files",
            ["簡報、報告、表格除了原始檔外再產一份 PDF。關閉可明顯加快產出與單頁編輯；已經有 PDF 的舊檔仍會跟著更新，以免兩份內容對不起來。"] =
                "Also produce a PDF alongside decks, reports and spreadsheets. Turning this off makes generation and single-page edits noticeably faster; older files that already have a PDF still keep it updated so the two never disagree.",
            ["同時產出對照 PDF"] = "Also produce a matching PDF",
            ["產出書面報告後，由沒有參與撰寫的模型核對數字與文字、找出矛盾，另存一份審查清單；不會修改報告。每份約 NT$1（Claude 審查）到 NT$10（GPT 審查）。"] =
                "After a written report is produced, a model that didn't write it checks the numbers and text for contradictions and saves a separate review list. The report itself is not changed. About NT$1 (Claude review) to NT$10 (GPT review) per report.",
            ["報告完成後獨立審查"] = "Independent review after reports",
            ["獨立審查找到「高」或「中」的問題時，把問題清單交回撰寫的模型，修改程式後重跑一次，再審查一次並逐項確認原問題是否修好。修正後的檔案沿用原本的檔名，修正前的檔案移到程式資料夾裡的「修正前」，兩次審查合併在同一份審查清單。目前只支援附資料檔、在沙箱執行的分析報告。每次約多 3～6 分鐘、NT$15～45。需要先開啟獨立審查。"] =
                "When the independent review finds high- or medium-severity issues, the list goes back to the writing model, which fixes its code and reruns once; the result is reviewed again and each original issue is checked off. The fixed files keep the original names, the pre-fix files move to a \"修正前\" (before fix) folder inside the code folder, and both reviews go into the same review list. Currently only for analysis reports run in the sandbox with attached data files. Adds about 3–6 minutes and NT$15–45 each time. Requires independent review to be on.",
            ["審查後自動修正一輪"] = "Auto-fix once after review",
            ["存放位置"] = "Save location",
            ["變更…"] = "Change…",
            ["開啟資料夾"] = "Open folder",
            ["還原預設"] = "Reset to default",
            ["畫面"] = "Display",
            ["每次執行用了哪個模型、為什麼選它、花了多少、根據哪些記憶。關掉只影響顯示，紀錄仍然會留下。"] =
                "Which model each run used, why it was chosen, what it cost and which memories it relied on. Turning this off only hides it; the record is still kept.",
            ["右上角顯示決策視窗"] = "Show decision panel (top right)",
            ["各家都沒有查餘額的 API，這裡用「你填的餘額 − 本機記到的花費」推估；充值後到 API 分頁更新金額。"] =
                "None of the providers offer a balance API, so this estimates \"the balance you entered − spending recorded on this PC\". Update the amount on the API tab after topping up.",
            ["右下角顯示 API 餘額"] = "Show API balance (bottom right)",
            ["成本控制"] = "Cost control",
            ["手動選擇不受影響"] = "Manual selection is not affected",
            ["Auto 不會選用，改用 Claude Sonnet。"] = "Auto won't pick it and uses Claude Sonnet instead.",
            ["關閉 Claude Opus（高成本）"] = "Disable Claude Opus (expensive)",
            ["Auto 不會選用，改用 Perplexity Sonar。"] = "Auto won't pick it and uses Perplexity Sonar instead.",
            ["關閉 Perplexity Deep Research（高成本）"] = "Disable Perplexity Deep Research (expensive)",
            ["上游附件"] = "Upstream attachments",
            ["開啟時讀取完整上游檔案，關閉時只讀取上游節點的文字輸出。"] =
                "When on, read the full upstream files. When off, read only the text output of upstream nodes.",
            ["完整讀取上游附件"] = "Read upstream attachments in full",
            ["逾時上限"] = "Timeout",
            ["設定範圍 30～1800 秒"] = "Range: 30–1800 seconds",
            ["秒"] = "sec",
            ["花費"] = "Spending",
            ["達上限之後不再執行新開始的任務"] = "Once the limit is reached, no new tasks will start",
            ["今日已花：約 NT$0"] = "Spent today: about NT$0",
            ["每日上限"] = "Daily limit",
            ["NT$／天"] = "NT$/day",
            ["（留空＝不限制）"] = "(blank = no limit)",
            ["確認框自動同意"] = "Auto-approve confirmations",
            ["秒後自動執行"] = "sec, then run automatically",
            ["（0＝全手動）"] = "(0 = always ask)",
            ["產出檔案上傳 Google Drive"] = "Upload output files to Google Drive",
            ["需已連結帳戶（「API」分頁的「Google 整合」區）"] = "Requires a linked account (Google integration on the API tab)",
            ["要上傳的檔案類型"] = "File types to upload",
            ["簡報"] = "Decks",
            ["圖片"] = "Images",
            ["影片"] = "Videos",
            ["文字/MD"] = "Text/MD",
            ["Word／Excel 轉成 Google 文件／試算表"] = "Convert Word/Excel to Google Docs/Sheets",
            ["上傳時由 Drive 轉檔，變成可線上編輯的 Google 格式"] = "Drive converts them on upload into editable Google formats",
            ["手機鏡像"] = "Phone mirror",
            ["手機連接同一 Wi-Fi 時，可在手機上查看執行狀態"] = "View run status on your phone when it's on the same Wi-Fi",
            ["鏡像模式"] = "Mirror mode",
            ["唯讀模式"] = "Read-only",
            ["手機端掃描QRcode或輸入網址"] = "Scan the QR code or enter the address on your phone",

            // ===== Skills =====
            ["把常用的指示、流程或格式模板存成「技能」，每次執行任務時自動注入給 AI。全域生效、跨專案、重啟保留；可個別開關。與本節點明確指示衝突時，以節點為準。"] =
                "Save frequently used instructions, workflows or format templates as \"skills\" that are given to the AI on every task. They apply everywhere, across projects and restarts, and can be toggled one by one. If a skill conflicts with a node's own instructions, the node wins.",
            ["新增技能"] = "New skill",
            ["技能名稱（例：週報格式）"] = "Skill name (e.g. Weekly report format)",
            ["技能內容：要注入給 AI 的指示/流程/格式模板"] = "Skill content: instructions / workflow / format template for the AI",
            ["＋ 新增技能"] = "+ Add skill",
            ["從 GitHub 匯入"] = "Import from GitHub",
            ["貼上 GitHub 上技能檔的連結（blob 或 raw 皆可，例如 SKILL.md），自動抓取並匯入成一條技能。frontmatter 的 name/description 會自動解析。"] =
                "Paste a link to a skill file on GitHub (blob or raw, e.g. SKILL.md) and it will be fetched and imported as a skill. The frontmatter name/description are read automatically.",
            ["https://github.com/…/SKILL.md 或 raw.githubusercontent.com 連結"] = "https://github.com/…/SKILL.md or a raw.githubusercontent.com link",
            ["匯入"] = "Import",
            ["我的技能"] = "My skills",

            // ===== API =====
            ["這個服務現在的狀態\n綠＝可用　黃＝接近用完或被限流　紅＝餘額不足／金鑰無效／連不上　灰＝還沒檢查過\n每次真的呼叫成功或失敗都會即時更新，也可以按上面的「重新檢查」"] =
                "Current status of this service\nGreen = available · Yellow = running low or rate-limited · Red = out of credit / invalid key / unreachable · Gray = not checked yet\nUpdates on every real call, or press \"Re-check\" above",
            ["這個服務向你收費的幣別（照官網帳單填）。改了之後現有餘額會自動換算。"] =
                "The currency this service bills you in (as shown on its billing page). Changing it converts the current balance automatically.",
            ["填官網現在顯示的餘額（例 20）。清空後送出＝不再追蹤這個服務。"] =
                "Enter the balance currently shown on the provider's site (e.g. 20). Submit it empty to stop tracking this service.",
            ["送出"] = "Submit",
            ["把餘額改成左邊填的數字，並以此刻為新的對帳起點"] = "Set the balance to the number on the left and start tracking from now",
            ["帳單頁"] = "Billing",
            ["開啟這個服務的官方帳單頁，查目前真正的餘額"] = "Open this service's official billing page to see the actual balance",
            ["API 金鑰"] = "API keys",
            ["解析順序：系統環境變數 → 這裡輸入的金鑰 → 都沒有就自動切換到其他可用模型（顯示於決策窗），全部不可用才回報失敗。"] =
                "Lookup order: system environment variables → keys entered here → otherwise switch to another available model automatically (shown in the decision panel). It only fails when nothing is available.",
            ["（GPT 文字、gpt-image 圖片）"] = "(GPT text, gpt-image images)",
            ["未設定"] = "Not set",
            ["清除"] = "Clear",
            ["（Anthropic 文字 / 導演 / 程式碼）"] = "(Anthropic text / director / code)",
            ["（Google 文字、Veo 影片）"] = "(Google text, Veo video)",
            ["（即時聯網研究 / 來源查證）"] = "(live web research / source checks)",
            ["（Drive 整合，見下方「Google 整合」區）"] = "(Drive integration, see Google integration below)",
            ["儲存 API 金鑰"] = "Save API keys",
            ["Google 整合"] = "Google integration",
            ["連結 Google 帳戶後，產出的檔案（PDF／PPTX／DOCX／圖片／影片）可自動存到你的 Drive。授權採最小權限（只碰本程式建立的檔案），憑證與 token 皆只存本機。"] =
                "After linking a Google account, output files (PDF/PPTX/DOCX/images/videos) can be saved to your Drive automatically. Access is minimal (only files this app creates), and credentials and tokens stay on this PC.",
            ["狀態：檢查中…"] = "Status: checking…",
            ["連結 Google"] = "Link Google",
            ["解除連結"] = "Unlink",
            ["上傳偏好（開關／各檔案類型／轉 Google 格式）在「個人化」分頁的「雲端上傳」區設定。"] =
                "Upload preferences (on/off, file types, Google format conversion) are on the Personal tab.",

            // ===== 預覽 =====
            ["預覽"] = "Preview",
            ["用系統程式開啟"] = "Open with default app",
            ["想改什麼？例如：第 3 頁改成比較表、整體更活潑、多加一頁結論（留空＝用原需求重做一次）"] =
                "What should change? e.g. turn page 3 into a comparison table, make it livelier, add a conclusion page (blank = redo with the original request)",
            ["只重做某一頁時填頁碼；留空＝整份重做"] = "Enter a page number to redo just that page; blank = redo everything",
            ["第"] = "Page",
            ["頁"] = "",
            ["上一版"] = "Previous",
            ["新版不如舊版時，退回上一個版本"] = "Go back to the previous version if the new one is worse",
            ["下一版"] = "Next",
            ["退回之後又想要新版時，前進回去（不用重新生成）"] = "Go forward to the newer version again (no regeneration needed)",
            ["重新生成"] = "Regenerate",
            ["這個檔案無法在這裡預覽。"] = "This file can't be previewed here.",
            ["載入中…"] = "Loading…",

            // ===== 節點 =====
            ["編輯"] = "Edit",
            ["字體大小…"] = "Font size…",
            ["展開並自動執行工作流"] = "Expand and run workflow",
            ["一鍵執行下游"] = "Run downstream",
            ["重新生成答案"] = "Regenerate answer",
            ["略過此步、從下一步續跑"] = "Skip this step and continue",
            ["強制停止生成"] = "Force stop",
            ["停止工作流"] = "Stop workflow",
            ["協作模型…"] = "Collaborating models…",
            ["將此節點加入記憶"] = "Add this node to memory",
            ["變更上游節點"] = "Change upstream node",
            ["更換連接方向"] = "Reverse connection",
            ["刪除"] = "Delete",
            ["上傳檔案/照片（會隨存檔保存）"] = "Upload files/photos (saved with the project)",
            ["送出給 AI 產生下半部"] = "Send to AI",
            ["選擇此對話區塊使用的模型"] = "Choose the model for this block",
            ["AI 正在生成…"] = "AI is generating…",
            ["點擊預覽"] = "Click to preview",
            ["往空白處拖＝新增一個下游節點\n拖到另一個節點上＝把那個節點接成下游（兩條以上接到同一個節點就是匯流，它會等所有上游都跑完才開始）"] =
                "Drag to empty space = add a downstream node\nDrag onto another node = make it downstream (two or more links into one node merge: it waits for every upstream to finish first)",
        };
    }
}
