using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-15：記憶裡存了「我是西班牙人」，用中文問「我是哪國人」，
    /// 回答變成 "Eres español."。
    ///
    /// 問題不在那條記憶，而在**從頭到尾沒有人告訴模型要用什麼語言**——
    /// 沒有明確指示時它從上下文猜，而一條國籍事實比提問語言更像指示。
    /// 第一次的修法是在記憶區塊加一句「這些事實不要影響語言」，那是拜託不是規格。
    /// </summary>
    public class AnswerLanguageTests
    {
        [Theory]
        [InlineData("我是哪國人", "繁體中文")]
        [InlineData("幫我做一份簡報", "繁體中文")]
        [InlineData("What nationality am I?", "English")]
        [InlineData("私の国籍は何ですか", "日本語")]
        [InlineData("제 국적이 무엇인가요", "한국어")]
        public void 依提問的字形判斷語言(string input, string expected)
            => Assert.Equal(expected, AnswerLanguage.Detect(input));

        [Fact]
        public void 中文句子提到外國不會被當成那個語言()
        {
            // 「我是西班牙人」是中文句子，不是西班牙文——看字形，不看內容。
            Assert.Equal("繁體中文", AnswerLanguage.Detect("我是西班牙人"));
            Assert.Equal("繁體中文", AnswerLanguage.Detect("我住在東京，幫我安排行程"));
            Assert.Equal("繁體中文", AnswerLanguage.Detect("介紹一下 France 的高檔餐飲"));
        }

        [Fact]
        public void 空的輸入退回繁體中文()
        {
            Assert.Equal("繁體中文", AnswerLanguage.Detect(null));
            Assert.Equal("繁體中文", AnswerLanguage.Detect("   "));
        }

        [Fact]
        public void 指示裡會寫明語言並擋掉身分事實的誤用()
        {
            string block = AnswerLanguage.BuildBlock("我是哪國人", null);

            Assert.Contains("繁體中文", block);
            Assert.Contains("依使用者提問的語言判斷", block);
            // 身分事實不是語言指示，這句要在提示裡，不能只寫在註解裡。
            Assert.Contains("不代表要用西班牙文回答", block);
        }

        [Fact]
        public void 使用者指定的語言優先於提問的字形()
        {
            string block = AnswerLanguage.BuildBlock("我是哪國人", "English");

            Assert.Contains("English", block);
            Assert.Contains("使用者指定", block);
            Assert.DoesNotContain("依使用者提問的語言判斷", block);
        }
    }
}
