using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// FinalAnswerSanitizer 負責把「給使用者看的最終答案」中的系統內部標記清掉
    /// （稽查資訊留在決策窗，不進人看的答案）。這裡釘住 Codex P1-10 修正：
    /// 內部脈絡標籤 [Container:…] / [Capability Data] 不得洩漏到輸出。
    /// </summary>
    public class FinalAnswerSanitizerTests
    {
        [Theory]
        [InlineData("估值可能修正 [Container:Perplexity Research]。", "估值可能修正。")]
        [InlineData("盤前價未取得[Capability Data]。", "盤前價未取得。")]
        [InlineData("A [Container: Perplexity Research] B [Capability Data] C", "A B C")]
        public void Sanitize_RemovesInternalContextLabels(string input, string expected)
        {
            var actual = FinalAnswerSanitizer.Sanitize(input, enforceSynthesisFormat: false);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Sanitize_RemovesInternalLabels_CaseInsensitive()
        {
            var actual = FinalAnswerSanitizer.Sanitize("x [container:foo] y", enforceSynthesisFormat: false);
            Assert.DoesNotContain("container", actual, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x", actual);
            Assert.Contains("y", actual);
        }

        [Fact]
        public void Sanitize_KeepsOrdinaryBracketsThatAreNotInternalLabels()
        {
            // 一般的括號內容（非內部標籤）不應被這條規則清掉。
            var actual = FinalAnswerSanitizer.Sanitize("風險（詳見附註）仍在。", enforceSynthesisFormat: false);
            Assert.Contains("詳見附註", actual);
        }
    }
}
