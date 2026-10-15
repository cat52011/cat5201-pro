using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 簡報的品質門檻。
    ///
    /// 「檔案產出成功」跟「這份簡報可以拿出去」是兩件事：檔案永遠寫得出來，
    /// 但字級縮到看不清楚、內容超出版面、頁碼對不上，成品就是不能用的。
    /// 這裡把可以用程式判斷的部分列成明確規則，產出後檢查一次；
    /// 有問題就寫進日誌並回報給使用者，而不是安靜交付。
    /// </summary>
    public static class DeckQuality
    {
        /// <summary>簡報投影時的內文字級下限。低於這個在會場後排就看不清楚了。</summary>
        public const float MinBodyFontSize = 14f;

        /// <summary>小字（頁碼、小標、圖說）的下限——這些本來就是輔助資訊。</summary>
        public const float MinLabelFontSize = 8f;

        /// <summary>被視為「內文」的字級門檻：比這個大的一律用內文標準檢查。</summary>
        private const float BodyThreshold = 13f;

        public sealed record Issue(int Page, string Kind, string Message);

        public sealed record Report(IReadOnlyList<Issue> Issues, int PageCount)
        {
            public bool Passed => Issues.Count == 0;

            /// <summary>給使用者看的一行摘要；沒問題回空字串。</summary>
            public string Summary()
            {
                if (Passed) return "";

                var byKind = Issues.GroupBy(i => i.Kind)
                    .Select(g => $"{g.Key} {g.Count()} 處")
                    .ToList();

                return $"版面檢查發現 {Issues.Count} 個問題（{string.Join("、", byKind)}）：" +
                       string.Join("；", Issues.Take(3).Select(i => $"第 {i.Page} 頁 {i.Message}"));
            }
        }

        /// <summary>檢查一份簡報的成品版面。</summary>
        internal static Report Check(PresentationOutlinePayload outline, byte[]? cover)
        {
            var issues = new List<Issue>();
            IReadOnlyList<DeckPage> pages;

            try
            {
                pages = DeckScene.Build(outline, cover);
            }
            catch (Exception ex)
            {
                // 排版直接失敗（例如內容多到縮字也放不下）也是品質問題，不是「產出成功」。
                return new Report(new[] { new Issue(0, "排版失敗", ex.Message) }, 0);
            }

            for (int i = 0; i < pages.Count; i++)
            {
                int pageNumber = i + 1;

                foreach (var e in pages[i].Elements)
                {
                    if (e.Kind == "text")
                    {
                        float floor = e.Size >= BodyThreshold ? MinBodyFontSize : MinLabelFontSize;
                        if (e.Size < floor)
                            issues.Add(new Issue(pageNumber, "字太小",
                                $"「{Shorten(e.Text)}」只有 {e.Size:0.#}pt（下限 {floor:0.#}pt）"));
                    }

                    // 元素跑出版面＝印出來會被裁掉。
                    if (e.X < -0.5f || e.Y < -0.5f ||
                        e.X + e.W > DeckScene.Width + 0.5f || e.Y + e.H > DeckScene.Height + 0.5f)
                    {
                        issues.Add(new Issue(pageNumber, "超出版面",
                            $"{(e.Kind == "text" ? $"「{Shorten(e.Text)}」" : e.Kind)} 超出可印範圍"));
                    }

                    // 圖片被壓扁／拉長（來源比例與框不符）——裁切本來就該保持比例。
                    if (e.Kind == "image" && e.Image != null)
                    {
                        var size = PptxBuilder.TryReadPngSize(e.Image);
                        if (size is { Width: > 0, Height: > 0 } && e.W > 0 && e.H > 0)
                        {
                            double source = (double)size.Value.Width / size.Value.Height;
                            double box = e.W / e.H;
                            if (Math.Abs(source - box) / box > 0.02)
                                issues.Add(new Issue(pageNumber, "圖片變形",
                                    $"圖片比例 {source:0.00} 與版面框 {box:0.00} 不符"));
                        }
                    }
                }
            }

            return new Report(issues, pages.Count);
        }

        /// <summary>檢查並寫進日誌；回傳給使用者看的摘要（沒問題回空字串）。</summary>
        internal static string CheckAndLog(PresentationOutlinePayload outline, byte[]? cover, string label)
        {
            var report = Check(outline, cover);
            if (report.Passed)
            {
                AppLog.Info("DeckQuality", $"{label}：{report.PageCount} 頁，版面檢查通過");
                return "";
            }

            AppLog.Warn("DeckQuality", $"{label}：{report.Summary()}");
            foreach (var issue in report.Issues.Take(20))
                AppLog.Warn("DeckQuality", $"　第 {issue.Page} 頁　{issue.Kind}：{issue.Message}");

            return report.Summary();
        }

        private static string Shorten(string? text)
        {
            string t = (text ?? "").Trim();
            return t.Length <= 12 ? t : t[..12] + "…";
        }
    }
}
