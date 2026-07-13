using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// MarkdownDisplayText.Clean 是「顯示層」清洗（節點輸出框/手機鏡像）：
    /// 把 **粗體**、__粗體__ 與行首 # 標題記號拿掉，但刻意保守——單*斜體、清單、表格、
    /// 程式碼與不成對記號一律不動。資料層（存檔/下游/builder）永遠拿原文，這裡只釘顯示行為。
    /// </summary>
    public class MarkdownDisplayTextTests
    {
        [Theory]
        [InlineData("**投資觀察 1**：基本面強", "投資觀察 1：基本面強")]
        [InlineData("毛利率 **66.2%**，優於指引", "毛利率 66.2%，優於指引")]
        [InlineData("__重點__結論", "重點結論")]
        [InlineData("## 短期走勢判斷", "短期走勢判斷")]
        [InlineData("  ### 縮排標題", "  縮排標題")]
        public void Clean_StripsBoldAndHeadingMarkers(string input, string expected)
            => Assert.Equal(expected, MarkdownDisplayText.Clean(input));

        [Fact]
        public void Clean_MultilineOutput_CleansEachLine()
        {
            string input = "## 關鍵資料\n- 營收：**NT$1,134.10B**\n- 毛利率：66.2%";
            string expected = "關鍵資料\n- 營收：NT$1,134.10B\n- 毛利率：66.2%";
            Assert.Equal(expected, MarkdownDisplayText.Clean(input));
        }

        [Theory]
        [InlineData("2**3 是次方寫法")]      // 不成對，兩側非緊貼非空白
        [InlineData("純文字沒有任何記號。")]  // 快路徑恆等
        [InlineData("- 清單項目保留")]        // 清單記號不清
        [InlineData("行中 # 不是標題")]       // 非行首 # 不動
        public void Clean_LeavesNonMarkupAlone(string input)
            => Assert.Equal(input, MarkdownDisplayText.Clean(input));

        [Fact]
        public void Clean_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal("", MarkdownDisplayText.Clean(null));
            Assert.Equal("", MarkdownDisplayText.Clean(""));
        }
    }
}
