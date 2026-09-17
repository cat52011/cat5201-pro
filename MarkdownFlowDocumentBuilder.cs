using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace Cat5201
{
    /// <summary>
    /// #6 輸出渲染：把模型輸出的 Markdown 轉成 FlowDocument（真粗體/標題/清單），
    /// 給節點輸出區的 RichTextBox 顯示。純顯示層——資料真相永遠是原始 Markdown
    /// （NodeControl._bottomRawText），存檔/下游/檔案 builder 不經過這裡。
    ///
    /// 刻意保守（與 MarkdownDisplayText.Clean 同哲學）：只處理 **粗體**、__粗體__、
    /// 行首 #~### 標題、行首 -/* 清單；表格、程式碼、連結一律原樣呈現，避免誤傷。
    /// </summary>
    public static class MarkdownFlowDocumentBuilder
    {
        // 與 MarkdownDisplayText 同語義：兩側緊貼非空白才視為成對，不跨行。
        private static readonly Regex BoldRegex =
            new(@"(\*\*|__)(?=\S)([^\r\n]+?)(?<=\S)\1", RegexOptions.Compiled);

        private static readonly Regex HeadingRegex =
            new(@"^(\s{0,3})(#{1,6})\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex BulletRegex =
            new(@"^(\s*)[-*]\s+(.*)$", RegexOptions.Compiled);

        public static FlowDocument Build(string? markdown, double baseFontSize)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = baseFontSize,
                // 讓文件用滿可用寬度換行（預設 PageWidth 行為在窄容器會出現水平捲動）。
                PageWidth = double.NaN,
            };

            string text = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd();

                if (line.Length == 0)
                {
                    // 空行＝段落間距，用小空段維持原有的視覺分隔。
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 0), FontSize = baseFontSize * 0.4 });
                    continue;
                }

                var heading = HeadingRegex.Match(line);
                if (heading.Success)
                {
                    int level = heading.Groups[2].Value.Length;
                    double size = level switch
                    {
                        1 => baseFontSize * 1.30,
                        2 => baseFontSize * 1.18,
                        _ => baseFontSize * 1.08,
                    };
                    var hp = new Paragraph
                    {
                        FontSize = size,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, level <= 2 ? 6 : 4, 0, 2),
                    };
                    AddInlines(hp, heading.Groups[3].Value);
                    doc.Blocks.Add(hp);
                    continue;
                }

                var bullet = BulletRegex.Match(line);
                if (bullet.Success)
                {
                    var bp = new Paragraph { Margin = new Thickness(12, 0, 0, 2) };
                    bp.Inlines.Add(new Run("•  "));
                    AddInlines(bp, bullet.Groups[2].Value);
                    doc.Blocks.Add(bp);
                    continue;
                }

                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                AddInlines(p, line);
                doc.Blocks.Add(p);
            }

            return doc;
        }

        // 行內 **粗體**/__粗體__ → Bold Run；其餘原樣。
        private static void AddInlines(Paragraph p, string text)
        {
            int last = 0;
            foreach (Match m in BoldRegex.Matches(text))
            {
                if (m.Index > last)
                    p.Inlines.Add(new Run(text.Substring(last, m.Index - last)));

                p.Inlines.Add(new Bold(new Run(m.Groups[2].Value)));
                last = m.Index + m.Length;
            }

            if (last < text.Length)
                p.Inlines.Add(new Run(text.Substring(last)));
        }
    }
}
