using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-15 產出的「2026 年台灣四大便利商店比較表」：四個品牌 × 四個欄位，
    /// 除了品牌名以外每一格都是「資料不足」或「未取得」。使用者的原話是「在跟我開玩笑嗎」。
    ///
    /// 「查不到就寫未取得、不要編數字」是對的規則，但它只管單一格子。
    /// 整欄都查不到時，那一欄只是把空白排得很整齊。
    /// </summary>
    public class TableQualityTests
    {
        private const string AllMissing = @"| 便利商店 | 門市數 | 2025 年營收 | 年增率 | 平均客單價 |
| --- | --- | --- | --- | --- |
| 統一超商 | 資料不足 | 資料不足 | 資料不足 | 未取得 |
| 全家 | 資料不足 | 資料不足 | 資料不足 | 未取得 |
| 萊爾富 | 資料不足 | 資料不足 | 資料不足 | 未取得 |
| OK 超商 | 資料不足 | 未公開 | 未公開 | 未取得 |";

        private const string PartlyMissing = @"| 便利商店 | 門市數 | 2025 年營收 | 平均客單價 |
| --- | --- | --- | --- |
| 統一超商 | 7248 | 3507 | 未取得 |
| 全家 | 4470 | 1100 | 未取得 |";

        [Fact]
        public void 每一格都查不到的表要被判定為沒有可用資料()
        {
            Assert.False(TableQuality.HasUsableData(AllMissing));
        }

        [Fact]
        public void 有一欄有資料就算撐得起來()
        {
            Assert.True(TableQuality.HasUsableData(PartlyMissing));
        }

        [Fact]
        public void 整欄都沒資料的欄位會被拿掉()
        {
            string cleaned = TableQuality.DropAllMissingColumns(PartlyMissing);

            Assert.DoesNotContain("平均客單價", cleaned);   // 整欄未取得 → 刪掉
            Assert.Contains("門市數", cleaned);              // 有資料 → 留著
            Assert.Contains("2025 年營收", cleaned);
            Assert.Contains("統一超商", cleaned);            // 名稱欄永遠保留
        }

        [Fact]
        public void 名稱欄就算看起來像缺值也不會被刪掉()
        {
            // 第一欄沒了就不知道每一列在講誰。
            string md = @"| 項目 | 數值 |
| --- | --- |
| - | 12 |
| — | 34 |";

            string cleaned = TableQuality.DropAllMissingColumns(md);
            Assert.Contains("項目", cleaned);
            Assert.Contains("數值", cleaned);
        }

        [Fact]
        public void 沒有欄位要刪時原樣回傳()
        {
            string md = @"| 年度 | 營收 |
| --- | --- |
| 2025 | 2.89 |";

            Assert.Equal(md, TableQuality.DropAllMissingColumns(md));
        }

        [Theory]
        [InlineData("未取得")]
        [InlineData("資料不足")]
        [InlineData("未公開")]
        [InlineData("N/A")]
        [InlineData("-")]
        [InlineData("—")]
        [InlineData("")]
        [InlineData("   ")]
        public void 各種寫法都算缺值(string cell) => Assert.True(TableQuality.IsMissing(cell));

        [Theory]
        [InlineData("0")]
        [InlineData("12,345")]
        [InlineData("3.2%")]
        [InlineData("統一超商")]
        public void 真的有值的不要被誤判(string cell) => Assert.False(TableQuality.IsMissing(cell));

        [Fact]
        public void 空輸入不會爆掉()
        {
            Assert.False(TableQuality.HasUsableData(null));
            Assert.False(TableQuality.HasUsableData(""));
            Assert.Equal("", TableQuality.DropAllMissingColumns(null));
        }
    }
}
