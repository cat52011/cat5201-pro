using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 答案與查到的數字「數字相同、單位差十倍」的自動檢查。
    ///
    /// 用的是 2026-10-29 第一階段評測的真實產出與美國證管會 6-K 原文的數字——
    /// 那九份裡有三份把營收寫小了十倍，系統沒抓到，機器計分也給了滿分。
    /// </summary>
    public class NumberConsistencyTests
    {
        // 美國證管會 6-K（台積電 2026 第三季）原文的數字，作者 2026-10-29 開原文核對過。
        private static List<VerifiedFactItem> SecFacts() => new()
        {
            new() { FactType = "revenue", Value = "NT$1,494.24 billion", UsageRole = FactOwnership.UsageNumericFactSource },
            new() { FactType = "revenue_usd", Value = "US$46.72 billion", UsageRole = FactOwnership.UsageNumericFactSource },
            new() { FactType = "eps", Value = "NT$29.43", UsageRole = FactOwnership.UsageNumericFactSource },
            new() { FactType = "gross_margin", Value = "66.4%", UsageRole = FactOwnership.UsageNumericFactSource },
        };

        private static string PilotOutput(string name, [System.Runtime.CompilerServices.CallerFilePath] string? here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
            while (dir != null)
            {
                string p = Path.Combine(dir.FullName, "docs", "eval", "runs", "2026-10-29", "outputs", name);
                if (File.Exists(p)) return File.ReadAllText(p);
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到第一階段的產出檔：" + name);
        }

        /// <summary>
        /// 九份真實產出裡，恰好是寫錯單位的那三份被抓到，正確的六份一份都不誤報。
        /// 誤報跟漏報一樣糟——動不動就跳警告，使用者很快就會不看了。
        /// </summary>
        [Theory]
        [InlineData("T01-A-1.txt", false)]
        [InlineData("T01-A-2.txt", false)]
        [InlineData("T01-A-3.txt", false)]
        [InlineData("T01-B-1.txt", true)]    // 新台幣1,494.243億
        [InlineData("T01-B-2.txt", false)]
        [InlineData("T01-B-3.txt", false)]
        [InlineData("T01-D-1.txt", true)]    // 新台幣 1,494.24 億元
        [InlineData("T01-D-2.txt", false)]
        [InlineData("T01-D-3.txt", true)]    // 新台幣 1,494.24 億元／美元 46.72 億元
        public void TheThreePilotUnitErrorsAreCaughtAndNothingElse(string file, bool expectFlag)
        {
            var findings = NumberConsistency.FindScaleMismatches(SecFacts(), PilotOutput(file));

            Assert.Equal(expectFlag, findings.Count > 0);
            if (expectFlag)
                Assert.All(findings, f => Assert.Equal(-1, f.PowerOfTen));   // 都是小了十倍
        }

        [Fact]
        public void TheNoticeSaysWhatWasWrittenAndWhatWasFound()
        {
            var findings = NumberConsistency.FindScaleMismatches(SecFacts(), "第三季營收為新台幣 1,494.24 億元。");
            string notice = NumberConsistency.BuildNotice(findings);

            Assert.Contains("數字可能有單位錯誤", notice);
            Assert.Contains("1,494.24 億", notice);
            Assert.Contains("NT$1,494.24 billion", notice);
            Assert.Contains("10 倍（答案偏小）", notice);
        }

        [Theory]
        [InlineData("營收新台幣 1.49424 兆元")]       // 兆＝10^12，正確
        [InlineData("營收 NT$1,494.24B")]
        [InlineData("營收 467.2 億美元")]             // 46.72 billion＝467.2 億，正確
        public void CorrectRestatementsInOtherUnitsAreNotFlagged(string answer)
            => Assert.Empty(NumberConsistency.FindScaleMismatches(SecFacts(), answer));

        /// <summary>
        /// 界線要講清楚：數字本身寫錯（467.2 億寫成 462.72 億）這裡抓不到——那不是單位問題。
        /// 這條測試記錄的是限制，不是合格行為。
        /// </summary>
        [Fact]
        public void ADigitErrorIsOutOfScope_KnownLimitation()
            => Assert.Empty(NumberConsistency.FindScaleMismatches(SecFacts(), "營收 462.72 億美元"));

        [Theory]
        [InlineData(1494.24e9, 1494.24e8, -1)]
        [InlineData(46.72e9, 46.72e10, 1)]
        [InlineData(46.72e9, 46.72e9, null)]       // 相同
        [InlineData(46.72e9, 46.27e8, null)]       // 有效數字不同
        [InlineData(46.72e9, -46.72e8, null)]      // 符號不同是另一種錯
        [InlineData(46.72e9, 46.72e4, null)]       // 差太多倍，不像是同一個數字寫錯單位
        public void ScaleOnlyDifferenceIsStrict(double fact, double answer, int? expected)
            => Assert.Equal(expected, NumberConsistency.ScaleOnlyDifference(fact, answer));

        [Fact]
        public void ShortRoundNumbersAreIgnored()
        {
            // 「5」「10」「2026」這種到處都有的數字，有效數字太少，比了只會誤報。
            var facts = new List<VerifiedFactItem>
            {
                new() { Value = "5", UsageRole = FactOwnership.UsageNumericFactSource },
            };
            Assert.Empty(NumberConsistency.FindScaleMismatches(facts, "共有 50 家、500 人"));
        }
    }
}
