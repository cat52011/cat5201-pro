using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 協作模式：一個節點指定由哪幾個模型一起回答（2026-10-29 使用者的產品構想）。
    /// 每個模型各自回答 → 讀完彼此的完整回答後交叉檢視 → 固定列出共識與分歧，分歧照原話並列。
    /// </summary>
    public class ModelCollaborationTests
    {
        private const string Gpt = AiModels.OpenAi_Gpt56;
        private const string Claude = AiModels.Claude_Opus5;

        private static AiFallbackExecutionResult Ok(string model, string text)
            => new() { IsSuccess = true, Text = text, ActualModelId = model };

        [Fact]
        public void NormalizeKeepsOnlyUsableDistinctModelsUpToFour()
        {
            var list = ModelCollaboration.Normalize(new[]
            {
                Gpt, Gpt, " ", "no-such-model", "claude-sonnet-5", Claude,
                AiModels.OpenAi_Gpt6, AiModels.Perplexity_Sonar, "gemini-3.5-flash",
            });

            Assert.Equal(4, list.Count);                               // 上限四個
            Assert.Equal(Gpt, list[0]);
            Assert.Contains(AiModels.Claude_Sonnet5, list);            // 舊 id 路由到後繼者
            Assert.DoesNotContain("no-such-model", list);
            Assert.Equal(list.Count, list.Distinct().Count());
        }

        [Fact]
        public void OneModelIsNotCollaboration()
        {
            Assert.False(ModelCollaboration.IsActive(new[] { Gpt }));
            Assert.True(ModelCollaboration.IsActive(new[] { Gpt, Claude }));
        }

        [Fact]
        public async Task BothRoundsRunAndDisagreementsAreListedVerbatim()
        {
            var calls = new ConcurrentBag<(string Model, string Prompt, string Purpose)>();

            Task<AiFallbackExecutionResult> Execute(string model, string prompt, string purpose)
            {
                calls.Add((model, prompt, purpose));
                bool review = purpose.StartsWith("協作交叉檢視");
                if (!review)
                    return Task.FromResult(Ok(model, model == Gpt ? "GPT：應該保守，先觀望。" : "Claude：可以分批進場。"));

                return Task.FromResult(Ok(model, model == Gpt
                    ? "我同意分批能降低風險，但仍認為應先觀望。\n```json\n" +
                      "{\"agree\":[\"分批能降低時點風險\"],\"disagree\":[{\"point\":\"現在要不要進場\"," +
                      "\"mine\":\"先觀望\",\"theirs\":\"分批進場\",\"with\":\"Claude Opus 5.5\",\"reason\":\"財報前波動大\"}]}\n```"
                    : "GPT 的保守有道理，但不影響我的結論。\n```json\n{\"agree\":[\"財報前波動確實大\"],\"disagree\":[]}\n```"));
            }

            var result = await ModelCollaboration.RunAsync(
                new[] { Gpt, Claude }, "【輸入】要不要買？", "要不要買？", Execute, null, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(4, calls.Count);                                   // 兩個模型 × 兩輪

            // 第一輪：兩個模型收到的是同一份輸入（相同背景）。
            Assert.Single(calls.Where(c => c.Purpose.StartsWith("協作第一輪")).Select(c => c.Prompt).Distinct());

            // 第二輪：每個模型都看到對方的完整回答。
            var gptReview = calls.Single(c => c.Model == Gpt && c.Purpose.StartsWith("協作交叉檢視"));
            Assert.Contains("Claude：可以分批進場。", gptReview.Prompt);
            var claudeReview = calls.Single(c => c.Model == Claude && c.Purpose.StartsWith("協作交叉檢視"));
            Assert.Contains("GPT：應該保守，先觀望。", claudeReview.Prompt);

            string text = result.Text;
            Assert.Contains("## 各模型的回答", text);
            Assert.Contains("GPT：應該保守，先觀望。", text);
            Assert.Contains("Claude：可以分批進場。", text);
            Assert.Contains("## 交叉檢視", text);
            Assert.DoesNotContain("```json", text);                         // 結構化區塊不外露

            // 共識與分歧：照原話、標明是誰說的，系統不選邊。
            Assert.Contains("## 共識與分歧", text);
            Assert.Contains("GPT-5.6 Sol：分批能降低時點風險", text);
            Assert.Contains("現在要不要進場", text);
            Assert.Contains("GPT-5.6 Sol：先觀望", text);
            // 「分批進場」在 Claude 第一輪的回答裡找得到，所以可以標成原句。
            Assert.Contains("Claude Opus 5.5（原句）：「分批進場」", text);
            Assert.Contains("理由（GPT-5.6 Sol）：財報前波動大", text);
            Assert.Contains("分歧怎麼取捨由你決定", text);
        }

        /// <summary>
        /// A 對 B 的轉述不能掛成 B 的原話（2026-11-02 第三方審查）。
        /// B 第一輪沒說過這句話 → 只能寫成「A 對 B 立場的理解」，並請讀者對照 B 的回答。
        /// </summary>
        [Fact]
        public void ParaphraseOfOtherModel_IsLabeledAsUnderstanding_NotQuote()
        {
            var round1 = new[]
            {
                new ModelCollaboration.Answer(Gpt, true, "GPT：應該保守，先觀望。", ""),
                new ModelCollaboration.Answer(Claude, true, "Claude：可以分批進場，但要控制比例。", ""),
            };
            var d = new ModelCollaboration.Disagreement(
                "現在要不要進場", "先觀望", "對方主張現在就全部買進", "Claude Opus 5.5", "財報前波動大");

            string line = ModelCollaboration.DescribeOtherSide(Gpt, d, round1);

            Assert.Contains("GPT-5.6 Sol 對 Claude Opus 5.5 立場的理解：對方主張現在就全部買進", line);
            Assert.Contains("請對照上方 Claude Opus 5.5 的回答", line);
            Assert.DoesNotContain("原句", line);
        }

        [Fact]
        public async Task AFailedModelIsMarkedNotReplaced()
        {
            var called = new ConcurrentBag<string>();

            Task<AiFallbackExecutionResult> Execute(string model, string prompt, string purpose)
            {
                called.Add(model);
                return Task.FromResult(model == Claude
                    ? new AiFallbackExecutionResult { IsSuccess = false, ErrorMessage = "429 rate limited" }
                    : Ok(model, "GPT 的回答"));
            }

            var result = await ModelCollaboration.RunAsync(
                new[] { Gpt, Claude }, "輸入", "要求", Execute, null, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Contains("這次沒有回應：429 rate limited", result.Text);
            Assert.Contains("沒有進行交叉檢視", result.Text);              // 只剩一個，沒得比
            Assert.All(called, m => Assert.Contains(m, new[] { Gpt, Claude })); // 沒有找別的模型頂替
        }

        [Fact]
        public async Task AReviewWithoutTheStructuredBlockIsStillShown()
        {
            Task<AiFallbackExecutionResult> Execute(string model, string prompt, string purpose)
                => Task.FromResult(Ok(model, purpose.StartsWith("協作交叉檢視") ? $"{model} 的自由評論" : $"{model} 的回答"));

            var result = await ModelCollaboration.RunAsync(
                new[] { Gpt, Claude }, "輸入", "要求", Execute, null, CancellationToken.None);

            Assert.Contains($"{Gpt} 的自由評論", result.Text);
            Assert.Contains("沒有列出結構化的共識與分歧", result.Text);
        }
    }
}
