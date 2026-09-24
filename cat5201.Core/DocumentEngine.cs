using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 文件（簡報 / 書面報告 / 表格）產出引擎（個人化可切換）。
    /// </summary>
    public enum DocumentEngine
    {
        /// <summary>
        /// Claude 文件技能（預設）：Claude 用官方 pptx/docx/xlsx 技能在沙盒寫程式產檔、轉圖檢查後交付——
        /// 與 Claude App 同一套機制。較慢（數分鐘）、較貴；需 ANTHROPIC_API_KEY，失敗自動退回內建版。
        /// </summary>
        ClaudeSkills = 0,

        /// <summary>內建快速版：AI 寫結構化大綱 → 本機 PptxBuilder / DocxReportBuilder 套版。快、便宜、版面固定。</summary>
        Builtin = 1
    }

    public static class DocumentEngineHelper
    {
        public static DocumentEngine Parse(string? value)
            => (value ?? "").Trim().ToLowerInvariant() is "builtin" or "1" ? DocumentEngine.Builtin : DocumentEngine.ClaudeSkills;

        public static string ToStorageValue(DocumentEngine engine)
            => engine == DocumentEngine.Builtin ? "Builtin" : "ClaudeSkills";

        /// <summary>
        /// 文件技能用的模型：預設 Claude Opus 5（品質優先）；使用者在個人化「封鎖 Opus」時尊重其成本決定，改用 Sonnet 5。
        /// </summary>
        public static string ResolveSkillsModel(bool blockOpus)
            => blockOpus ? AiModels.Claude_Sonnet5 : AiModels.Claude_Opus5;
    }

    /// <summary>文件技能的提示詞（純函式，便於測試與調整）。</summary>
    public static class DocumentSkillsPrompt
    {
        public sealed class Request
        {
            public string UserInput { get; init; } = "";
            public string MainContent { get; init; } = "";
            public bool WantsDeck { get; init; }
            public bool WantsReport { get; init; }
            public bool WantsTable { get; init; }
            public int RequestedSlides { get; init; }
            public bool WantsImages { get; init; }
            public IReadOnlyList<(string Title, string Url)> Sources { get; init; } = new List<(string, string)>();
            public string PreferenceBlock { get; init; } = "";
        }

        /// <summary>依主題挑選的藝術方向（與內建排版共用同一套判斷，兩條路徑風格一致）。</summary>
        public static DeckArtDirection ArtFor(Request r)
            => DeckArtDirection.ForTopic(r.UserInput);

        public static IReadOnlyList<string> SkillIds(Request r)
        {
            var ids = new List<string>();
            if (r.WantsDeck) ids.Add("pptx");
            if (r.WantsReport) ids.Add("docx");
            if (r.WantsTable) ids.Add("xlsx");
            return ids;
        }

        public static IReadOnlyCollection<string> WantedExtensions(Request r)
        {
            var exts = new HashSet<string>();
            if (r.WantsDeck) exts.Add(".pptx");
            if (r.WantsReport) exts.Add(".docx");
            if (r.WantsTable) exts.Add(".xlsx");
            if (r.WantsDeck || r.WantsReport || r.WantsTable) exts.Add(".pdf");
            return exts;
        }

        public const string SystemPrompt =
@"你是頂尖的簡報設計師與商業文件撰稿人，要替使用者產出可以直接交付、拿去開會或寄給客戶的專業檔案。
你有 Anthropic 官方的文件技能（pptx / docx / xlsx）與程式執行環境：開始前先讀對應技能的 SKILL.md，完整遵循它的工作流程與設計準則。

品質標準：與使用者直接在 Claude App 請你做的成品同級，不是把文字貼進範本。
- 先定藝術方向，再談內容排版：下方會給你依主題選定的視覺語言（色盤、字體個性、版面個性），整份必須一致地執行它。
  設計、藝術、展覽、品牌、時尚類主題請做到雜誌編輯或展覽圖錄的水準——超大字級對比、明確網格、大量留白、滿版圖像、細緻分隔線、字距講究的小標。
- 內容：以使用者提供的【主要內容】為依據。事實、數字、日期、引述只能來自提供的內容與來源，不可捏造；缺的資料就明確寫「未取得」。你可以重組結構、提煉洞察、寫出更好的標題與敘事。
- 簡報設計：每頁一個清楚重點與「結論式標題」；依內容選版面（大數字、比較、圖表、表格、時間軸、流程、引言），避免整頁條列文字牆；整份色彩與字級一致，有封面、議程/摘要、結論與下一步、資料來源頁。
- 圖表：有數據就用原生圖表或 matplotlib 畫清楚的圖，數值必須與提供的內容一致並標示單位與來源。
- 報告：有封面、摘要、清楚的章節層次、必要的表格與圖、結論與建議、參考來源；是完整書面文章，不是投影片條列。
- 中文：一律使用使用者的語言（預設繁體中文）。在 pptx/docx 內同時設定東亞字型（East Asian / a:ea）為 Microsoft JhengHei，讓 Windows 上正確顯示。
- 視覺檢查：產出後把每頁轉成圖片檢查文字溢出、重疊、裁切、對比與中文缺字，修正後再交付。檢查 PDF 用的中文字型可用 `fc-list :lang=zh` 確認；沒有中文字型就不要交付會缺字的 PDF。

執行環境紀律（很重要，違反會讓整個任務失敗）：
- 絕不要把二進位檔或大檔的內容印出來（不要 cat/xxd .pptx、.pdf、圖片、字型）。
- 每個指令的輸出控制在 50 行以內，需要看檔案就用 `head`、`tail`、`wc -l`、`ls -la`。
- 縮圖檢查一次最多看 4 張，看完就繼續，不要重複產生整份縮圖。
- 迴圈與批次指令要先確認輸出量；脈絡一旦被塞爆，整份文件就交付不出去。

交付規則：
- 每種檔案只交付一個最終版本，檔名用有意義的主題名稱（不要用 output、test 之類）。
- 每個 pptx / docx / xlsx 都另外轉出一份內容一致的 PDF（例如用 LibreOffice headless 轉檔）。
- 最後一步：用「單一個 bash 指令」把所有最終交付檔複製到輸出資料夾（技能說明有指定就用它，否則用工作目錄下的 outputs/）並列出檔名。
- 完成後用 2～4 句話向使用者說明做了哪些檔案、各自重點（不要貼程式碼、不要描述內部步驟）。";

        public static string BuildUserPrompt(Request r)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(r.PreferenceBlock))
            {
                sb.AppendLine("【使用者個人化偏好（最高優先，若指定語言或格式必須照做）】");
                sb.AppendLine(r.PreferenceBlock.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("【使用者原始需求】");
            sb.AppendLine((r.UserInput ?? "").Trim());
            sb.AppendLine();

            if (r.WantsDeck || r.WantsReport)
            {
                sb.AppendLine(ArtFor(r).BuildModelBrief(r.UserInput ?? ""));
                sb.AppendLine();
            }

            if (r.WantsDeck)
            {
                sb.AppendLine(PresentationDesignBrief.Build(""));
                sb.AppendLine("配圖必須解釋該頁內容，不可用上升箭頭、飛機房屋拼貼或泛用商業象徵圖代替數據。數據頁用可編輯圖表或數字與清楚的單位、時間。圖片和長文使用分離區域，不把主體裁掉。需要真實作品／景點圖片而缺素材時，使用有來源的內容與圖解，明示缺素材，不捏造照片。");
            }

            sb.AppendLine("【需要產出的檔案】");
            if (r.WantsDeck)
            {
                string pages = r.RequestedSlides > 0
                    ? $"正好 {r.RequestedSlides} 張內容頁（封面與來源頁不計）"
                    : "張數依內容決定，通常 8～14 張";
                sb.AppendLine($"- 簡報 .pptx（{pages}）＋ 同內容 .pdf");
            }
            if (r.WantsReport)
                sb.AppendLine("- 書面報告 .docx ＋ 同內容 .pdf");
            if (r.WantsTable)
                sb.AppendLine("- 表格 .xlsx（有標題列、適當欄寬、數字格式、必要時加總或圖表）＋ 同內容 .pdf");
            if (r.WantsDeck && (r.WantsReport || r.WantsTable))
                sb.AppendLine("- 多種檔案的內容與數字必須一致；簡報是精煉版，報告是完整論述版。");
            sb.AppendLine();

            if (r.WantsImages)
            {
                sb.AppendLine("【配圖】");
                sb.AppendLine("使用者希望圖文並茂。執行環境無法連網或生成照片，請用圖表、圖示、幾何圖形與示意圖（程式繪製）增加視覺性。");
                sb.AppendLine();
            }

            sb.AppendLine("【主要內容（已由研究與整合步驟完成，事實與數字以此為準）】");
            sb.AppendLine((r.MainContent ?? "").Trim());
            sb.AppendLine();

            var sources = r.Sources?
                .Where(s => !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Url))
                .Take(20)
                .ToList() ?? new List<(string, string)>();

            if (sources.Count > 0)
            {
                sb.AppendLine("【資料來源（放進來源頁／參考資料）】");
                foreach (var (title, url) in sources)
                    sb.AppendLine($"- {title} {url}".TrimEnd());
                sb.AppendLine();
            }

            sb.AppendLine("請開始製作。");
            return sb.ToString();
        }
    }
}
