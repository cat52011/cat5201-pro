using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Markdig;

namespace Cat5201
{
    /// <summary>
    /// 把抽出的文件 / 簡報內容渲染成漂亮的 HTML，給預覽面板的 WebView2 顯示。
    /// 報告：內容本身是 markdown（表格 / 粗體 / 標題），用 Markdig 還原成正確排版。
    /// 簡報：每張投影片渲染成一張卡片。
    /// </summary>
    public static class ArtifactHtmlRenderer
    {
        private static readonly MarkdownPipeline _pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        private const string BaseCss = @"
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                padding: 40px 48px 64px;
                font-family: 'Microsoft JhengHei','Segoe UI',-apple-system,'Helvetica Neue',sans-serif;
                font-size: 15.5px;
                line-height: 1.7;
                color: #24262b;
                background: #fafafa;
            }
            .sheet {
                max-width: 820px;
                margin: 0 auto;
                background: #fff;
                border: 1px solid #ececef;
                border-radius: 14px;
                padding: 44px 52px 52px;
                box-shadow: 0 1px 3px rgba(0,0,0,.04);
            }
            h1,h2,h3,h4 { color: #1b1d22; font-weight: 700; line-height: 1.35; }
            h1 { font-size: 26px; margin: 0 0 18px; }
            h2 { font-size: 20px; margin: 30px 0 12px; padding-bottom: 6px; border-bottom: 2px solid #f0f0f2; }
            h3 { font-size: 17px; margin: 22px 0 8px; }
            p { margin: 10px 0; }
            ul,ol { margin: 10px 0; padding-left: 24px; }
            li { margin: 4px 0; }
            strong { color: #14161a; }
            a { color: #2e6fe0; text-decoration: none; }
            hr { border: none; border-top: 1px solid #ececef; margin: 26px 0; }
            code {
                background: #f3f3f5; border-radius: 5px; padding: 1px 6px;
                font-family: Consolas,'Courier New',monospace; font-size: 0.92em;
            }
            pre { background: #f6f7f9; border-radius: 10px; padding: 14px 16px; overflow-x: auto; }
            pre code { background: none; padding: 0; }
            table {
                border-collapse: collapse; width: 100%; margin: 16px 0; font-size: 14.5px;
            }
            th,td { border: 1px solid #e6e6ea; padding: 9px 13px; text-align: left; vertical-align: top; }
            th { background: #f5f6f8; font-weight: 700; color: #2a2c31; }
            tr:nth-child(even) td { background: #fbfbfc; }
            blockquote {
                margin: 14px 0; padding: 6px 16px; border-left: 3px solid #d6d9e0;
                color: #555; background: #fafafb;
            }
            img {
                max-width: 100%; height: auto; display: block;
                margin: 18px auto; border-radius: 8px;
            }";

        private const string SlidesCss = @"
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                padding: 34px 40px 60px;
                font-family: 'Microsoft JhengHei','Segoe UI',-apple-system,'Helvetica Neue',sans-serif;
                color: #24262b;
                background: #f0f1f4;
            }
            .deck { max-width: 880px; margin: 0 auto; }
            .slide {
                position: relative;
                background: #fff;
                border: 1px solid #e6e6ea;
                border-radius: 14px;
                padding: 30px 36px 48px;
                margin: 0 0 22px;
                box-shadow: 0 2px 10px rgba(0,0,0,.05);
                aspect-ratio: 16 / 9;
                display: flex;
                flex-direction: column;
            }
            .slide-no {
                position: absolute; top: 16px; right: 20px;
                font-size: 12px; color: #b3b6bd; font-weight: 600;
            }
            .slide-title {
                font-size: 23px; font-weight: 700; color: #1b1d22;
                line-height: 1.3; margin: 4px 0 16px; padding-right: 48px;
            }
            .slide-body { font-size: 16px; line-height: 1.6; }
            .slide-body .line { margin: 7px 0; padding-left: 18px; position: relative; color: #3a3d44; }
            .slide-body .line::before {
                content: ''; position: absolute; left: 2px; top: 11px;
                width: 5px; height: 5px; border-radius: 50%; background: #9aa0ab;
            }
            .empty { color: #aeb2ba; font-size: 14px; }
            .cover-img {
                display: block; max-width: 100%; max-height: 52%;
                margin: 6px auto 0; border-radius: 8px; object-fit: contain;
            }
            .regen-btn {
                position: absolute; bottom: 14px; right: 14px;
                font-size: 12px; font-family: inherit; color: #1565c0;
                background: #fff; border: 1px solid #1565c0; border-radius: 6px;
                padding: 3px 10px; cursor: pointer;
            }
            .regen-btn:hover { background: #e8f1fd; }
            .regen-btn:disabled { color: #9aa0ab; border-color: #cfd3da; cursor: default; background: #f3f3f5; }";

        /// <summary>報告（markdown 原文）→ 完整 HTML 頁面。</summary>
        public static string BuildDocxHtml(string markdown)
        {
            string body = string.IsNullOrWhiteSpace(markdown)
                ? "<p class='empty'>（沒有可顯示的內容）</p>"
                : Markdown.ToHtml(markdown, _pipeline);

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>").Append(BaseCss).Append("</style></head><body>");
            sb.Append("<div class='sheet'>").Append(body).Append("</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>簡報（逐張文字行）→ 投影片卡片 HTML。每張 slide 第一行當標題，其餘為內容。</summary>
        public static string BuildSlidesHtml(IReadOnlyList<IReadOnlyList<string>> slides)
            => BuildSlidesHtml(slides, allowRegen: false);

        public static string BuildSlidesHtml(IReadOnlyList<IReadOnlyList<string>> slides, bool allowRegen)
            => BuildSlidesHtml(slides, allowRegen, (IReadOnlyList<byte[]?>?)null);

        // 向後相容：只帶封面圖的舊呼叫 → 包成「只有第一張有圖」的清單。
        public static string BuildSlidesHtml(
            IReadOnlyList<IReadOnlyList<string>> slides, bool allowRegen, byte[]? coverImagePng)
            => BuildSlidesHtml(slides, allowRegen,
                coverImagePng == null ? null : new List<byte[]?> { coverImagePng });

        /// <summary>
        /// allowRegen=true 時每張投影片右下角加「重生這張」鈕，點擊 postMessage 回宿主重生並覆蓋 .pptx。
        /// slideImages：每張投影片的圖（與 slides 順序對齊，封面 + 內容頁配圖），該張無圖則為 null，
        /// 讓預覽看得到簡報裡所有嵌入的圖（不只封面）。
        /// </summary>
        public static string BuildSlidesHtml(
            IReadOnlyList<IReadOnlyList<string>> slides, bool allowRegen, IReadOnlyList<byte[]?>? slideImages)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>").Append(SlidesCss).Append("</style></head><body>");
            sb.Append("<div class='deck'>");

            if (slides == null || slides.Count == 0)
            {
                sb.Append("<p class='empty'>（沒有可顯示的投影片）</p>");
            }
            else
            {
                int n = 0;
                foreach (var slide in slides)
                {
                    n++;
                    sb.Append("<div class='slide'>");
                    sb.Append("<div class='slide-no'>").Append(n).Append("</div>");

                    // 封面（第一張）不提供重生鈕，內容頁才有。
                    if (allowRegen && n > 1)
                    {
                        sb.Append("<button class='regen-btn' data-order='").Append(n)
                          .Append("' onclick='regen(").Append(n).Append(")'>重生這張</button>");
                    }

                    string title = slide != null && slide.Count > 0 ? slide[0] : "";
                    sb.Append("<div class='slide-title'>")
                      .Append(WebUtility.HtmlEncode(title))
                      .Append("</div>");

                    // 該張投影片的圖（封面或內容頁配圖）：有圖就顯示。
                    byte[]? imgBytes = (slideImages != null && (n - 1) < slideImages.Count)
                        ? slideImages[n - 1] : null;
                    if (imgBytes != null && imgBytes.Length > 0)
                        sb.Append("<img class='cover-img' src='data:image/png;base64,")
                          .Append(Convert.ToBase64String(imgBytes))
                          .Append("' alt='圖' />");

                    sb.Append("<div class='slide-body'>");
                    if (slide != null)
                    {
                        for (int i = 1; i < slide.Count; i++)
                        {
                            if (string.IsNullOrWhiteSpace(slide[i]))
                                continue;
                            sb.Append("<div class='line'>")
                              .Append(WebUtility.HtmlEncode(slide[i]))
                              .Append("</div>");
                        }
                    }
                    sb.Append("</div></div>");
                }
            }

            sb.Append("</div>");

            if (allowRegen)
            {
                sb.Append("<script>function regen(o){")
                  .Append("try{window.chrome.webview.postMessage(JSON.stringify({action:'regen',order:o}));}catch(e){}")
                  .Append("document.querySelectorAll('.regen-btn').forEach(function(b){b.disabled=true;});")
                  .Append("var t=document.querySelector('[data-order=\"'+o+'\"]');if(t){t.textContent='重生中…';}")
                  .Append("}</script>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>試算表（逐列儲存格）→ HTML 表格頁（首列當表頭）。</summary>
        public static string BuildXlsxHtml(IReadOnlyList<IReadOnlyList<string>> rows)
            => BuildXlsxHtml(new ArtifactTextExtractor.XlsxGrid(
                (rows ?? new List<IReadOnlyList<string>>())
                    .Select(r => r.Select(c => new ArtifactTextExtractor.XlsxCell(c ?? "", false)).ToList())
                    .ToList(),
                null));

        /// <summary>
        /// 試算表 → HTML 表格頁，照活頁簿本身的樣子渲染：標題列套檔案裡的強調色、
        /// 數字右對齊並用表格數字（等寬對位）、列間只有髮絲線。
        ///
        /// 為什麼要這麼講究：預覽的用途就是「不開 Excel 也知道交出去的東西長怎樣」。
        /// 用一張通用的灰底 HTML 表格代替，使用者就得自己開檔案再確認一次。
        /// </summary>
        public static string BuildXlsxHtml(ArtifactTextExtractor.XlsxGrid grid)
        {
            var rows = grid?.Rows;
            string accent = string.IsNullOrWhiteSpace(grid?.HeaderColorHex) ? "#2a2c31" : grid!.HeaderColorHex!;

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>").Append(BaseCss).Append(SheetCss(accent)).Append("</style></head><body>");
            sb.Append("<div class='sheet'>");

            if (rows == null || rows.Count == 0)
            {
                sb.Append("<p class='empty'>（空白試算表）</p>");
            }
            else
            {
                int headerRow = grid!.HeaderRowIndex;

                // 標題與來源註記在欄位列之前，是文件的抬頭，不是表格的一列。
                for (int r = 0; r < headerRow && r < rows.Count; r++)
                {
                    string text = string.Join(" ", rows[r].Select(c => c?.Text ?? "")).Trim();
                    if (text.Length == 0)
                        continue;
                    sb.Append(r == 0 ? "<h1 class='sheet-title'>" : "<p class='sheet-note'>")
                      .Append(WebUtility.HtmlEncode(text))
                      .Append(r == 0 ? "</h1>" : "</p>");
                }

                sb.Append("<div class='grid-wrap'><table class='grid'>");
                for (int r = headerRow; r < rows.Count; r++)
                {
                    bool isHeader = r == headerRow;
                    bool isTotal = r == grid.TotalRowIndex;
                    string tag = isHeader ? "th" : "td";

                    sb.Append(isTotal ? "<tr class='total'>" : "<tr>");
                    foreach (var cell in rows[r])
                    {
                        sb.Append('<').Append(tag);
                        if (!isHeader && cell != null && cell.IsNumber)
                            sb.Append(" class='num'");
                        sb.Append('>')
                          .Append(WebUtility.HtmlEncode(cell?.Text ?? ""))
                          .Append("</").Append(tag).Append('>');
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</table></div>");
                AppendChart(sb, grid, accent);
            }

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>表格預覽的專用樣式：強調色取自活頁簿，所以要在執行期組出來。</summary>
        private static string SheetCss(string accent) => $@"
            /* 預覽面板可能很窄。讓表格保持自然欄寬、必要時橫向捲動，
               不要硬擠成每格一個字——那不是使用者在 Excel 裡看到的樣子。 */
            .grid-wrap {{ overflow-x: auto; margin: 16px 0; }}
            table.grid {{ border: none; width: auto; min-width: 100%; margin: 0; }}
            table.grid th {{ white-space: nowrap; }}
            table.grid td {{ vertical-align: middle; }}
            @media (max-width: 640px) {{
                body {{ padding: 20px 16px 40px; }}
                .sheet {{ padding: 24px 18px 28px; border-radius: 10px; }}
            }}
            table.grid th, table.grid td {{ border: none; border-bottom: 1px solid #edeef1; }}
            table.grid th {{
                background: {accent}; color: #fff; font-weight: 700;
                border-bottom: none; vertical-align: middle; padding: 11px 13px;
            }}
            table.grid tr:nth-child(even) td {{ background: none; }}
            table.grid td.num {{
                text-align: right; font-variant-numeric: tabular-nums;
                white-space: nowrap;
            }}
            table.grid tr.total td {{
                font-weight: 700; border-top: 1.5px solid #24262b; border-bottom: none;
            }}
            .sheet-title {{ margin: 0 0 4px; font-size: 22px; }}
            .sheet-note {{ margin: 0 0 20px; font-size: 12.5px; color: #8b8f98; }}
            .chart-wrap {{ overflow-x: auto; margin: 26px 0 4px; }}
            .chart-title {{
                font-size: 12.5px; color: #8b8f98; letter-spacing: .04em; margin: 0 0 6px;
            }}";

        /// <summary>
        /// 把活頁簿裡那張原生圖表也畫進預覽。畫哪兩欄是檔案自己說的（見 XlsxChart），
        /// 所以預覽跟 Excel 指的是同一份資料；只有渲染方式不同，那是必然的。
        /// </summary>
        private static void AppendChart(StringBuilder sb, ArtifactTextExtractor.XlsxGrid grid, string accent)
        {
            var chart = grid.Chart;
            if (chart == null)
                return;

            var points = new List<(string Label, double Value)>();
            for (int r = grid.HeaderRowIndex + 1; r < grid.Rows.Count; r++)
            {
                if (r == grid.TotalRowIndex)
                    continue;

                var row = grid.Rows[r];
                if (chart.CategoryColumn >= row.Count || chart.ValueColumn >= row.Count)
                    continue;

                if (TryNumber(row[chart.ValueColumn]?.Text, out double value))
                    points.Add((row[chart.CategoryColumn]?.Text ?? "", value));
            }

            if (points.Count == 0)
                return;

            const int width = 640, height = 300, top = 16, bottom = 96, left = 8, right = 8;
            double max = Math.Max(points.Max(p => p.Value), 0.0001);
            double slot = (width - left - right) / (double)points.Count;
            double barWidth = Math.Min(64, slot * 0.6);
            int baseline = height - bottom;

            sb.Append("<div class='chart-wrap'>");
            if (!string.IsNullOrWhiteSpace(chart.Title))
                sb.Append("<p class='chart-title'>").Append(WebUtility.HtmlEncode(chart.Title)).Append("</p>");

            sb.Append("<svg width='").Append(width).Append("' height='").Append(height)
              .Append("' viewBox='0 0 ").Append(width).Append(' ').Append(height)
              .Append("' xmlns='http://www.w3.org/2000/svg'>");

            sb.Append("<line x1='").Append(left).Append("' y1='").Append(baseline)
              .Append("' x2='").Append(width - right).Append("' y2='").Append(baseline)
              .Append("' stroke='#d8dade' stroke-width='1'/>");

            for (int i = 0; i < points.Count; i++)
            {
                double centre = left + slot * (i + 0.5);
                double barHeight = (baseline - top) * (points[i].Value / max);
                double x = centre - barWidth / 2;

                sb.Append("<rect x='").Append(F(x)).Append("' y='").Append(F(baseline - barHeight))
                  .Append("' width='").Append(F(barWidth)).Append("' height='").Append(F(barHeight))
                  .Append("' fill='").Append(accent).Append("'/>");

                sb.Append("<text x='").Append(F(centre)).Append("' y='").Append(F(baseline - barHeight - 6))
                  .Append("' text-anchor='middle' font-size='11' fill='#6c7079'>")
                  .Append(WebUtility.HtmlEncode(Trim(points[i].Value))).Append("</text>");

                // 中文類別名很長，跟 Excel 一樣斜放，直排會變成一個字一行。
                sb.Append("<text x='").Append(F(centre)).Append("' y='").Append(baseline + 14)
                  .Append("' font-size='11' fill='#6c7079' text-anchor='end' transform='rotate(-35 ")
                  .Append(F(centre)).Append(' ').Append(baseline + 14).Append(")'>")
                  .Append(WebUtility.HtmlEncode(Clip(points[i].Label, 14))).Append("</text>");
            }

            sb.Append("</svg></div>");
        }

        private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Trim(double value)
            => value.ToString(Math.Abs(value % 1) < 0.0001 ? "#,##0" : "#,##0.##", CultureInfo.InvariantCulture);

        private static string Clip(string text, int max)
        {
            string t = (text ?? "").Trim();
            return t.Length <= max ? t : t[..max] + "…";
        }

        /// <summary>把顯示格式化過的文字（1,234 / 28.5%）讀回數值。</summary>
        private static bool TryNumber(string? text, out double value)
        {
            string t = (text ?? "").Trim().Replace(",", "");
            bool percent = t.EndsWith("%", StringComparison.Ordinal);
            if (percent) t = t[..^1];

            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;

            if (percent) value /= 100.0;
            return true;
        }
    }
}
