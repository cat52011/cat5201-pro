using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 顯示層 Markdown 清洗（MVP P1-10 後半）：節點輸出框是 TextBox、無法渲染粗體，
    /// 直接顯示原文會出現一堆 ** 與 # 符號，看起來像半成品。
    ///
    /// 原則：只動「顯示」、不動資料——GetBottomText / 存檔 / 下游 prompt / 檔案 builder
    /// 全部仍拿原始 Markdown（PDF/Docx 的粗體排版靠它），只有畫面上把符號拿掉。
    /// 刻意保守：只清 **粗體**、__粗體__ 與行首標題 #，其餘（單*斜體、表格、清單、程式碼）不碰，避免誤傷。
    /// </summary>
    public static class MarkdownDisplayText
    {
        // **bold**：兩側緊貼非空白才視為成對（"2**3" 這種數學不動）；不跨行。
        private static readonly Regex BoldRegex =
            new(@"\*\*(?=\S)([^\r\n]+?)(?<=\S)\*\*", RegexOptions.Compiled);

        private static readonly Regex UnderscoreBoldRegex =
            new(@"__(?=\S)([^\r\n]+?)(?<=\S)__", RegexOptions.Compiled);

        // 行首（允許 0-3 空白縮排）的 # ~ ###### 標題記號。
        private static readonly Regex HeadingRegex =
            new(@"(?m)^(\s{0,3})#{1,6}\s+", RegexOptions.Compiled);

        public static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            // 快路徑：完全沒有記號字元就原樣返回（顯示路徑會被頻繁呼叫）。
            if (text.IndexOf('*') < 0 && text.IndexOf('#') < 0 && text.IndexOf('_') < 0)
                return text;

            text = BoldRegex.Replace(text, "$1");
            text = UnderscoreBoldRegex.Replace(text, "$1");
            text = HeadingRegex.Replace(text, "$1");
            return text;
        }
    }
}
