using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 答案裡的數字，跟搜尋查到的數字對得上嗎？——專抓「數字一樣、單位差十倍」這一種錯。
    ///
    /// 2026-10-29 第一階段評測：九份產出裡有三份把台積電營收寫小了十倍
    /// （查到的是 NT$1,494.24 billion，寫成「新台幣 1,494.24 億元」）。
    /// 我打開美國證管會的原文核對過：研究階段抓到的數字是對的，錯是在把英文財報寫成中文時，
    /// 把 billion 翻成「億」造成的。系統沒抓到，機器計分也給了滿分。
    /// 模擬面試時被問「系統能不能自動比對來源與最終檔案的數字」，當時的答案是「還做不到」。
    ///
    /// 做法：把查到的每一個數字換算成實際數值，再找答案裡**有效數字完全相同、但相差 10 的整數次方**的數字。
    /// 這種組合幾乎只會是單位寫錯——兩個不同的指標剛好有同樣的五、六位有效數字，機率極低。
    ///
    /// 刻意不做的事：數字本身寫錯（例如 467.2 億寫成 462.72 億）這裡抓不到，
    /// 那需要知道「這個數字在講哪個指標」，是語意比對，不是數字比對。這條界線要講清楚，
    /// 否則「有數字檢查」會被讀成「數字都檢查過了」。
    /// </summary>
    public static class NumberConsistency
    {
        public sealed record Finding(string FactText, string AnswerText, int PowerOfTen);

        /// <summary>有效數字少於這個位數就不比——「5」「10」「2026」到處都是，會誤報。</summary>
        private const int MinSignificantDigits = 4;

        /// <summary>差到 10⁴ 倍以上就不太可能是同一個數字寫錯單位，不報。</summary>
        private const int MaxPowerOfTen = 4;

        public static IReadOnlyList<Finding> FindScaleMismatches(IEnumerable<VerifiedFactItem>? facts, string? answer)
        {
            var answerNumbers = EvalRubric.ExtractNumbers(answer)
                .Where(n => n.SignificantDigits >= MinSignificantDigits && n.Value != 0)
                .ToList();
            if (answerNumbers.Count == 0)
                return Array.Empty<Finding>();

            var findings = new List<Finding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fact in facts ?? Array.Empty<VerifiedFactItem>())
            {
                if (fact == null || !FreshDataAssessment.HasRealValue(fact.Value))
                    continue;

                string factText = $"{fact.Value} {fact.Unit}".Trim();
                foreach (var f in EvalRubric.ExtractNumbers(factText)
                             .Where(n => n.SignificantDigits >= MinSignificantDigits && n.Value != 0))
                {
                    foreach (var a in answerNumbers)
                    {
                        int? power = ScaleOnlyDifference(f.Value, a.Value);
                        if (power == null) continue;

                        // 提醒裡引用查到的整筆資料（含幣別），使用者才對得回來源。
                        string key = a.Index + "|" + factText;
                        if (seen.Add(key))
                            findings.Add(new Finding(factText, a.Text, power.Value));
                    }
                }
            }

            return findings;
        }

        /// <summary>
        /// 兩個數的有效數字相同、只差 10 的整數次方時，回傳那個次方（例如 -1 表示答案小了十倍）；否則 null。
        /// 符號不同不算——那是另一種錯，而且不是單位造成的。
        /// </summary>
        internal static int? ScaleOnlyDifference(double fact, double answer)
        {
            if (fact == 0 || answer == 0 || Math.Sign(fact) != Math.Sign(answer))
                return null;

            double k = Math.Log10(answer / fact);
            int n = (int)Math.Round(k);
            if (n == 0 || Math.Abs(n) > MaxPowerOfTen)
                return null;

            // 有效數字相同：比值去掉 10 的次方之後要非常接近 1（容許四捨五入到小數第二、三位的差異）。
            return Math.Abs(k - n) < 0.0005 ? n : null;
        }

        /// <summary>放在答案最上方的提醒；沒有發現就回空字串。</summary>
        public static string BuildNotice(IReadOnlyList<Finding>? findings)
        {
            if (findings == null || findings.Count == 0)
                return "";

            var lines = findings.Take(5).Select(f =>
                $"答案寫「{f.AnswerText}」，查到的資料是「{f.FactText}」——數字相同，但相差 {Factor(f.PowerOfTen)}");

            return "⚠️【數字可能有單位錯誤】" + string.Join("；", lines) +
                   "。請對照來源確認單位（billion＝十億、trillion＝兆）。";
        }

        private static string Factor(int power)
        {
            string times = Math.Abs(power) switch { 1 => "10", 2 => "100", 3 => "1,000", _ => "10,000" };
            return times + " 倍" + (power < 0 ? "（答案偏小）" : "（答案偏大）");
        }
    }
}
