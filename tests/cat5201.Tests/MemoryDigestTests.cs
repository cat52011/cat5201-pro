using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 標記為記憶時存的應該是「值得長期記住的事實」，不是把整段對話截斷塞進去。
    /// 整段存的話，召回時那些跟記憶無關的字會一起被送進提示詞，佔位置又干擾判斷。
    /// </summary>
    public class MemoryDigestTests
    {
        [Fact]
        public void KeepsTheDurableFactAndDropsTheRest()
        {
            var facts = MemoryDigest.FromText(
                "我是美國人，現在住在台北。幫我查一下這週台積電的股價走勢，然後整理成表格。",
                "好的，以下是本週台積電的股價整理……（略）");

            Assert.Contains(facts, f => f.Contains("美國人"));
            // 這一次的任務細節不該變成長期記憶。
            Assert.DoesNotContain(facts, f => f.Contains("股價"));
        }

        [Fact]
        public void QuestionsAreNotDurableFacts()
        {
            var facts = MemoryDigest.FromText("我是哪裡人？", "你還沒告訴我。");
            Assert.DoesNotContain(facts, f => f.EndsWith("？"));
        }

        [Fact]
        public void NeverReturnsNothingWhenThereIsSomethingToRemember()
        {
            // 使用者按了「加入記憶」，清單裡卻是空白的話，他會以為沒存成功。
            var facts = MemoryDigest.FromText("把這段翻成日文", "承知しました");
            Assert.NotEmpty(facts);
        }

        [Fact]
        public void ParsesTheModelsAnswerAndCapsIt()
        {
            var facts = MemoryDigest.TryParse(
                "{\"facts\":[\"使用者是美國人\",\"使用者住在台北\",\"使用者偏好繁體中文\",\"第四條應該被截掉\"]}");

            Assert.NotNull(facts);
            Assert.Equal(MemoryDigest.MaxFacts, facts!.Count);
            Assert.Equal("使用者是美國人", facts[0]);
        }

        [Fact]
        public void AnEmptyAnswerIsRespectedButUnparseableOutputIsNot()
        {
            // 模型說「沒有值得長期記住的事」是有效答案。
            Assert.Empty(MemoryDigest.TryParse("{\"facts\":[]}")!);
            // 解析不出來才算失敗（呼叫端據此退回規則版）。
            Assert.Null(MemoryDigest.TryParse("抱歉，我無法處理。"));
        }

        [Fact]
        public void ContentIsWrittenAsShortBullets()
        {
            string content = MemoryDigest.ToContent(new[] { "使用者是美國人", "使用者偏好繁體中文" });
            Assert.Equal("- 使用者是美國人\n- 使用者偏好繁體中文", content.Replace("\r\n", "\n"));
        }
    }
}
