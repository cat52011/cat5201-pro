using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 匯流節點的對等輸入，以及「各步驟實際收到了什麼」的紀錄。
    /// 兩者都來自 2026-10-29 的模擬面試：Q17（A、B 兩條分支要對等）、Q11（要能證明要求真的傳到了）。
    /// </summary>
    public class UpstreamMergeTests
    {
        [Fact]
        public void EveryUpstreamAppearsInFullAndEqually()
        {
            string longArgument = string.Concat(Enumerable.Repeat("B 方的完整論點。", 400)); // 遠超過 1,200 字

            string merged = UpstreamMerge.Combine(new[]
            {
                new UpstreamMerge.Source("分支 A", "GPT-5.6 Sol", "A 認為應該保守。"),
                new UpstreamMerge.Source("分支 B", "Claude Opus 5.5", longArgument),
            });

            // 兩邊都在，而且第二條沒有被截斷——原本它只會出現在背景脈絡裡、截到 1,200 字。
            Assert.Contains("A 認為應該保守。", merged);
            Assert.Contains(longArgument, merged);

            // 兩邊用同樣的格式標示來源與模型。
            Assert.Contains("【上游 1／2：分支 A（GPT-5.6 Sol）】", merged);
            Assert.Contains("【上游 2／2：分支 B（Claude Opus 5.5）】", merged);

            // 明說地位對等、分歧要並列——整合時漏掉異議是 Q18 的擔心。
            Assert.Contains("地位對等", merged);
            Assert.Contains("並列寫出", merged);
        }

        [Fact]
        public void ASingleUpstreamIsPassedThroughUnchanged()
            => Assert.Equal("只有一條上游的結果",
                UpstreamMerge.Combine(new[] { new UpstreamMerge.Source("A", "", "只有一條上游的結果") }));

        [Fact]
        public void EmptyUpstreamsAreSkipped()
            => Assert.Equal("有內容的那一條",
                UpstreamMerge.Combine(new[]
                {
                    new UpstreamMerge.Source("A", "", "  "),
                    new UpstreamMerge.Source("B", "", "有內容的那一條"),
                }));
    }

    public class StepInputRecordTests
    {
        private const string Request = "研究台積電最近一季財報與近期股價表現，列出關鍵數字與可信來源";

        [Fact]
        public void TheRequestEmbeddedVerbatimCounts()
        {
            string input = "【Parallel Agent Task】\nPurpose: search\n\nOriginal User Request:\n" + Request + "\n\n請只完成你負責的部分";
            Assert.True(StepInputRecord.ContainsVerbatim(input, Request));
        }

        [Fact]
        public void WhitespaceDifferencesDoNotMatter()
            => Assert.True(StepInputRecord.ContainsVerbatim(
                "研究台積電最近一季財報與\n近期股價表現，列出關鍵數字 與可信來源", Request));

        [Theory]
        [InlineData("研究台積電最近一季財報與近期股價表現")]            // 被截斷
        [InlineData("請研究 TSMC 上一季的財務表現與股價，附上來源")]   // 被改寫／摘要
        [InlineData("")]
        public void TruncatedOrRewrittenRequestsDoNotCount(string input)
            => Assert.False(StepInputRecord.ContainsVerbatim(input, Request));

        [Fact]
        public void TheRecordCarriesAHashThatMatchesTheFullText()
        {
            var record = StepInputRecord.From("搜尋研究", AiModels.Claude_Sonnet5, Request, Request);

            Assert.Equal(Request.Length, record.Chars);
            Assert.Equal(StepInputRecord.ShortHash(Request), record.Sha256);
            Assert.Equal(12, record.Sha256.Length);
            Assert.True(record.ContainsOriginalRequest);
        }
    }
}
