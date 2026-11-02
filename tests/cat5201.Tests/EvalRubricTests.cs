using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 必要內容清單與比對器。
    ///
    /// 為什麼要有這組測試：本專題過去兩個最難回答的問題——「壓頁數刪掉的是不是重要的東西」
    /// 與「這份產出有沒有把該講的講到」——都是因為「必要」沒有被寫下來，所以量不出來。
    /// 下面每一條都在釘住「清單寫錯時要看得出來」「分數不能靠寬鬆比對撐高」。
    /// </summary>
    public class EvalRubricTests
    {
        // ───────────────────────── 規格本身要擋得住寫壞 ─────────────────────────

        [Fact]
        public void EmptyChecklistIsRejected()
        {
            // 空清單會讓涵蓋率永遠是 100%——比沒有清單更危險，所以直接丟例外。
            var ex = Assert.Throws<FormatException>(() =>
                EvalRubricSpec.Parse("""{"task_id":"X","required":[]}"""));
            Assert.Contains("空的", ex.Message);
        }

        [Fact]
        public void MissingTaskIdIsRejected()
            => Assert.Throws<FormatException>(() => EvalRubricSpec.Parse(
                """{"required":[{"id":"R1","kind":"any_of","patterns":["a"]}]}"""));

        [Fact]
        public void DuplicateItemIdIsRejected()
        {
            var ex = Assert.Throws<FormatException>(() => EvalRubricSpec.Parse("""
                {"task_id":"X","required":[
                  {"id":"R1","kind":"any_of","patterns":["a"]},
                  {"id":"R1","kind":"any_of","patterns":["b"]}]}
                """));
            Assert.Contains("重複", ex.Message);
        }

        [Theory]
        // kind 與必填欄位不相符的四種寫壞方式，每一種都會讓該項「永遠通過」或「永遠不通過」。
        [InlineData("""{"task_id":"X","required":[{"id":"R1","kind":"number","aliases":["營收"]}]}""")]
        [InlineData("""{"task_id":"X","required":[{"id":"R1","kind":"source"}]}""")]
        [InlineData("""{"task_id":"X","required":[{"id":"R1","kind":"any_of","patterns":[]}]}""")]
        [InlineData("""{"task_id":"X","required":[{"id":"R1","kind":"length"}]}""")]
        public void MalformedItemIsRejected(string json)
            => Assert.Throws<FormatException>(() => EvalRubricSpec.Parse(json));

        [Fact]
        public void MinCountLargerThanPatternsIsRejected()
        {
            // 要求「至少命中 4 個」卻只給 2 個樣式：這一項永遠不可能通過，分數會被無聲拉低。
            var ex = Assert.Throws<FormatException>(() => EvalRubricSpec.Parse("""
                {"task_id":"X","required":[
                  {"id":"R1","kind":"at_least","min_count":4,"patterns":["a","b"]}]}
                """));
            Assert.Contains("min_count", ex.Message);
        }

        // ───────────────────────── 比對的鬆緊要剛好 ─────────────────────────

        [Theory]
        [InlineData("2026 年第二季營收", "2026年第二季", true)]   // 空白不該造成差異
        [InlineData("ＲＡＧ 與 ＧＰＴ", "rag", true)]              // 全形要能對上半形
        [InlineData("Retrieval-Augmented", "retrieval-augmented", true)] // 大小寫不該造成差異
        [InlineData("這份文件談的是毛利", "毛利率", false)]        // 不該把「毛利」當成「毛利率」
        public void NormalizationIgnoresOnlyTheThingsThatDoNotMatter(string text, string pattern, bool expected)
            => Assert.Equal(expected, EvalRubric.ContainsPattern(EvalRubric.Normalize(text), pattern));

        [Fact]
        public void NumberMustSitNearItsAlias()
        {
            var aliases = new[] { "營收" };

            // 數字就在別名旁邊：算命中。
            Assert.True(EvalRubric.HasNumberNearAlias("本季營收為 9,890 億元", 989_000_000_000, 0.5, aliases));

            // 同樣的數字出現在別名前面也算（±120 字元的窗）。
            Assert.True(EvalRubric.HasNumberNearAlias("9,890 億元，這是營收", 989_000_000_000, 0.5, aliases));

            // 數字離別名太遠：不算——否則「文章裡剛好有這個數字」就會被當成講對了。
            string far = "本季營收表現亮眼。" + new string('餘', 400) + "另有一筆 9,890 億元的無關金額。";
            Assert.False(EvalRubric.HasNumberNearAlias(far, 989_000_000_000, 0.5, aliases));

            // 別名完全沒出現：不算。
            Assert.False(EvalRubric.HasNumberNearAlias("毛利率 9,890 億元", 989_000_000_000, 0.5, aliases));
        }

        /// <summary>
        /// 符號相反的數字不得算成命中。
        ///
        /// 2026-10-28 第四輪 AI 輔助審查：要求「EPS 必須是 5」，輸入「EPS: -5」，
        /// 原本的數字樣式沒有涵蓋正負號，只抽出 5，於是判成 100% 命中。
        /// 一個會接受符號相反數字的計分器，算出來的分數不能用來支持任何品質主張。
        /// </summary>
        [Theory]
        [InlineData("EPS: 5", 5, true)]
        [InlineData("EPS: -5", 5, false)]
        [InlineData("EPS: −5", 5, false)]       // U+2212 數學減號
        [InlineData("EPS: 負 5", 5, false)]      // 中文的「負」
        [InlineData("EPS: -5.0", 5, false)]
        [InlineData("EPS: -5", -5, true)]       // 要求負數時，負數才算命中
        [InlineData("EPS: 5", -5, false)]
        // 只認符號（- − 負），不認「虧損」「下跌」這類語意上的負——
        // 那需要語意判讀，比對器不做；要求負值的題目請在清單裡明確寫負號。
        [InlineData("虧損 34%", -34, false)]
        public void SignedNumbersAreNotConfused(string text, double expected, bool hit)
            => Assert.Equal(hit, EvalRubric.HasNumberNearAlias(text, expected, 0, new[] { "EPS", "虧損" }));

        [Theory]
        [InlineData("成長 34%", 34, 0, true)]
        [InlineData("成長 34.2%", 34, 0, false)]    // 容許誤差 0 就是要完全相等
        [InlineData("成長 34.2%", 34, 1, true)]     // 1% 誤差內
        [InlineData("延遲增加 1.8 倍", 1.8, 0, true)]
        [InlineData("樣本 1,248 筆", 1248, 0, true)]
        [InlineData("約 3.5 億", 350_000_000, 0, true)]
        [InlineData("約 23.86B", 23_860_000_000, 0, true)]
        public void NumberUnitsAndToleranceWork(string text, double expected, double tolerancePct, bool hit)
            => Assert.Equal(hit, EvalRubric.HasNumberNearAlias(text, expected, tolerancePct, Array.Empty<string>()));

        [Theory]
        [InlineData("來源：https://www.sec.gov/edgar/data/1/x.htm", true)]
        [InlineData("來源：https://sec.gov/x", true)]
        [InlineData("來源：https://www.sec.gov.evil.example/x", false)] // 網域邊界，不是字串包含
        [InlineData("來源：https://notsec.gov/x", false)]
        [InlineData("來源：公司財報（沒有連結）", false)]
        public void SourceMatchingUsesDomainBoundaries(string text, bool expected)
            => Assert.Equal(expected, EvalRubric.HasSourceUnder(text, new[] { "sec.gov" }));

        [Fact]
        public void SourceUrlFollowedByChinesePunctuationStillParses()
        {
            // 中文行文常寫成「（見 https://sec.gov/x）。」——尾巴的全形標點不該吃掉整個判定。
            Assert.True(EvalRubric.HasSourceUnder("（見 https://sec.gov/x）。", new[] { "sec.gov" }));
        }

        // ───────────────────────── 各種 kind 的行為 ─────────────────────────

        private static EvalRubricSpec SpecWith(string itemJson)
            => EvalRubricSpec.Parse($"{{\"task_id\":\"X\",\"required\":[{itemJson}]}}");

        [Fact]
        public void AtLeastCountsDistinctPatterns()
        {
            var spec = SpecWith("""
                {"id":"R1","kind":"at_least","min_count":3,"critical":true,
                 "patterns":["個人化學習","自動批改","語言學習","教材生成"]}
                """);

            Assert.False(EvalRubric.Score(spec, "談了個人化學習與自動批改").CriticalItemsSatisfied);     // 只有兩個
            Assert.True(EvalRubric.Score(spec, "個人化學習、自動批改、教材生成").CriticalItemsSatisfied); // 三個

            // 同一個樣式重複出現不該算成兩個。
            Assert.False(EvalRubric.Score(spec, "個人化學習、個人化學習、個人化學習").CriticalItemsSatisfied);
        }

        [Fact]
        public void LengthWindowIsEnforced()
        {
            var spec = SpecWith("""{"id":"R1","kind":"length","critical":true,"min_chars":10,"max_chars":20}""");

            Assert.False(EvalRubric.Score(spec, "太短").CriticalItemsSatisfied);
            Assert.True(EvalRubric.Score(spec, new string('字', 15)).CriticalItemsSatisfied);
            Assert.False(EvalRubric.Score(spec, new string('字', 40)).CriticalItemsSatisfied);

            // 計字不含空白：排版用的空白不該把字數撐進區間。
            Assert.False(EvalRubric.Score(spec, "字 " + new string(' ', 60)).CriticalItemsSatisfied);
        }

        [Fact]
        public void DeclinesAcceptsTheCommonWaysOfSayingIDoNotKnow()
        {
            var spec = SpecWith("""{"id":"R1","kind":"declines","critical":true,"patterns":["屆時"]}""");

            Assert.True(EvalRubric.Score(spec, "這個獎尚未公布，無從介紹。").CriticalItemsSatisfied);
            Assert.True(EvalRubric.Score(spec, "I am unable to verify this.").CriticalItemsSatisfied);
            Assert.True(EvalRubric.Score(spec, "屆時會公布").CriticalItemsSatisfied);          // 規格自己補的字樣
            Assert.False(EvalRubric.Score(spec, "得主是 A 與 B，貢獻為 C。").CriticalItemsSatisfied);
        }

        [Fact]
        public void ForbiddenContentFailsEvenAtFullCoverage()
        {
            var spec = EvalRubricSpec.Parse("""
                {"task_id":"X",
                 "required":[{"id":"R1","kind":"any_of","critical":true,"patterns":["結論"]}],
                 "forbidden":[{"id":"F1","label":"保證報酬","patterns":["保證獲利"]}]}
                """);

            var score = EvalRubric.Score(spec, "結論：本策略保證獲利。");

            Assert.Equal(1.0, score.Coverage);       // 涵蓋率滿分
            Assert.False(score.CriticalItemsSatisfied);              // 但不及格——這兩件事必須分開
            Assert.Contains("F1", score.ForbiddenHitIds);
        }

        [Fact]
        public void CoverageAndPassAreDifferentQuestions()
        {
            var spec = EvalRubricSpec.Parse("""
                {"task_id":"X","required":[
                  {"id":"R1","kind":"any_of","patterns":["背景"]},
                  {"id":"R2","kind":"any_of","patterns":["方法"]},
                  {"id":"R3","kind":"any_of","patterns":["資料"]},
                  {"id":"R4","kind":"any_of","critical":true,"patterns":["結論"]}]}
                """);

            var score = EvalRubric.Score(spec, "背景、方法、資料都談了，就是沒有收尾。");

            Assert.Equal(0.75, score.Coverage, 3);   // 四分之三
            Assert.False(score.CriticalItemsSatisfied);              // 但缺的正好是必備的那一項
            Assert.Equal(new[] { "R4" }, score.CriticalMissingIds);
        }

        [Fact]
        public void ExpiredSpecIsFlagged()
        {
            var spec = EvalRubricSpec.Parse("""
                {"task_id":"X","expires_on":"2027-10-01",
                 "required":[{"id":"R1","kind":"declines","patterns":["尚未"]}]}
                """);

            Assert.False(spec.IsExpired(new DateTime(2027, 9, 30)));
            Assert.True(spec.IsExpired(new DateTime(2027, 10, 2)));

            // 沒寫期限就永遠不過期。
            Assert.False(EvalRubricSpec.Parse(
                """{"task_id":"Y","required":[{"id":"R1","kind":"any_of","patterns":["a"]}]}""")
                .IsExpired(new DateTime(2099, 1, 1)));
        }

        // ───────────────────────── 內容保留率：本專題真正要量的東西 ─────────────────────────

        /// <param name="limitsAt">
        /// 把「風險與限制」放在第幾節（1 起算）。預設第 8 節；
        /// 傳 4 可重現第四輪審查的例子：重要限制排在中段時還救不救得回來。
        /// </param>
        private static PresentationOutlinePayload NineSectionDeck(int limitsAt = 8)
        {
            string filler = string.Concat(Enumerable.Repeat(
                "帶電粒子沿磁力線進入極區大氣，與氧原子碰撞後放出綠色光，高度約一百至二百五十公里之間，" +
                "實際亮度受太陽風速度與當地天候共同影響。", 4));

            var slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "冰島極光觀賞指南", Bullets = new List<string>() },
            };

            // 九節內容：必要項目刻意分散在頭、中、尾——尾段放結論與限制。
            string[] topics =
            {
                "什麼是極光", "觀測季節", "地點選擇", "天候判讀", "裝備與拍攝",
                "交通與住宿", "費用估算", "風險與限制", "結論與行動建議",
            };

            // 把「風險與限制」搬到指定位置，其餘順序不變。
            var ordered = topics.Where(t => t != "風險與限制").ToList();
            ordered.Insert(Math.Clamp(limitsAt - 1, 0, ordered.Count), "風險與限制");

            var headings = ordered.Select((t, i) => $"第 {i + 1} 節：{t}").ToArray();
            foreach (string h in headings)
                slides.Add(new PresentationSlidePayload
                {
                    Order = slides.Count + 1,
                    Kind = "content",
                    Heading = h,
                    Bullets = Enumerable.Range(0, 6).Select(j => $"重點 {j + 1}：{h}。{filler}").ToList(),
                });

            slides.Add(new PresentationSlidePayload
            {
                Order = slides.Count + 1, Kind = "sources", Heading = "資料來源",
                Bullets = new List<string> { "Visit North Iceland", "Iceland.org" },
            });

            return new PresentationOutlinePayload
            { Title = "冰島極光觀賞指南", Slides = slides, SlideCount = slides.Count };
        }

        private static EvalRubricSpec DeckSpec() => EvalRubricSpec.Parse("""
            {"task_id":"DECK","required":[
              {"id":"D1","kind":"any_of","patterns":["什麼是極光"]},
              {"id":"D2","kind":"any_of","patterns":["觀測季節"]},
              {"id":"D3","kind":"any_of","patterns":["地點選擇"]},
              {"id":"D4","kind":"any_of","patterns":["天候判讀"]},
              {"id":"D5","kind":"any_of","patterns":["裝備與拍攝"]},
              {"id":"D6","kind":"any_of","patterns":["交通與住宿"]},
              {"id":"D7","kind":"any_of","patterns":["費用估算"]},
              {"id":"D8","kind":"any_of","patterns":["風險與限制"]},
              {"id":"D9","kind":"any_of","critical":true,"patterns":["結論與行動建議"]}]}
            """);

        /// <summary>
        /// 壓到使用者要的頁數之後，結論必須還在。
        ///
        /// 2026-10-28 第三輪 AI 輔助審查實測：舊的壓縮邏輯是「留前 N 張、其餘併進尾巴再被字數上限截掉」，
        /// 九節壓成五頁只剩第 1～3 節——等於優先刪除結論。這條測試就是那個回歸的守門員。
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(4)]
        [InlineData(3)]
        public void CompressingToAPageBudgetKeepsTheConclusion(int requested)
        {
            var spec = DeckSpec();
            var before = NineSectionDeck();
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(before, requested);

            var (b, a, lost) = EvalRubric.Retention(spec, before, after);

            Assert.Equal(1.0, b.Coverage);   // 壓縮前九節都在
            Assert.True(a.Coverage < 1.0, "壓到更少頁卻什麼都沒少，這個樣本沒驗到壓縮");

            // 現在守得住的是「最末節不會被犧牲」。
            Assert.DoesNotContain("D9", lost);
            Assert.Empty(a.CriticalMissingIds);
        }

        /// <summary>
        /// 標題語意上不能刪的章節，不論排在第幾節都要留住。
        ///
        /// 這條原本是「已知限制」的反向記錄：均勻取樣只是另一條位置規則，
        /// 2026-10-28 第四輪 AI 輔助審查指出「重要限制若在第四節，仍可能被刪除」。
        /// 現在的取捨順序改成「標題語意優先、位置次之」，所以它變成正向的驗收條件。
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(4)]
        public void ProtectedSectionsSurviveRegardlessOfPosition(int requested)
        {
            var spec = DeckSpec();
            var before = NineSectionDeck();
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(before, requested);

            var (_, _, lost) = EvalRubric.Retention(spec, before, after);

            Assert.DoesNotContain("D9", lost);   // 結論與行動建議
            Assert.DoesNotContain("D8", lost);   // 風險與限制：第 8 節，靠位置救不到
        }

        /// <summary>
        /// 頁數少到只容得下一張內容頁時，受保護的章節也只能留一個——這是版面的硬限制，不是取捨錯誤。
        /// 此時留下的必須是結論，而不是「什麼是極光」。
        /// </summary>
        [Fact]
        public void WhenOnlyOneContentPageFitsTheConclusionWins()
        {
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(NineSectionDeck(), 3);
            string text = EvalRubric.Flatten(after);

            Assert.Contains("結論與行動建議", text);
            Assert.DoesNotContain("第 1 節：什麼是極光", text);
        }

        /// <summary>
        /// 把「風險與限制」搬到第 4 節，它仍然要活下來——這正是第四輪審查舉的例子。
        /// </summary>
        [Fact]
        public void AProtectedSectionInTheMiddleIsKept()
        {
            var deck = NineSectionDeck(limitsAt: 4);
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(deck, 4);
            string text = EvalRubric.Flatten(after);

            Assert.Contains("風險與限制", text);
            Assert.Contains("結論與行動建議", text);
        }

        /// <summary>
        /// 這條記錄的是**還沒解決**的部分，不是合格行為。
        ///
        /// 受保護的判準是「標題看起來像結論／建議／限制／風險／來源」——
        /// 系統認得自己產生的章節名字，但認不出使用者心裡真正必要的中性標題。
        /// 若使用者要的重點掛在「天候判讀」這種標題下，壓縮時依然可能被犧牲。
        /// 要真正解決，得先能從需求文字抽出必要項目清單，那是本專題未完成的部分。
        ///
        /// 哪天壓縮器會讀清單了，這條測試就會失敗——那時請改寫成正向的驗收條件。
        /// </summary>
        [Fact]
        public void NeutralHeadingsAreStillSacrificed_KnownLimitation()
        {
            var spec = DeckSpec();
            var before = NineSectionDeck();
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(before, 4);

            var (_, _, lost) = EvalRubric.Retention(spec, before, after);

            // 中性標題（天候判讀／交通與住宿／費用估算…）至少還是有東西被捨去。
            Assert.NotEmpty(lost);
            Assert.All(lost, id => Assert.DoesNotContain(id, new[] { "D8", "D9" }));
        }

        /// <summary>
        /// 壓到一頁時，留下的必須是結論，而不是「什麼是極光」。
        /// 這一頁放不下整份簡報，但至少要放對那一頁。
        /// </summary>
        [Fact]
        public void CompressingToASinglePageKeepsTheLastSectionNotTheFirst()
        {
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(NineSectionDeck(), 1);
            string text = EvalRubric.Flatten(after);

            Assert.Contains("結論與行動建議", text);
            Assert.DoesNotContain("第 1 節：什麼是極光", text);
        }

        /// <summary>保留率要算得出「失去了哪幾項」，而不只是一個比例。</summary>
        [Fact]
        public void RetentionNamesWhatWasLost()
        {
            var spec = DeckSpec();
            var before = NineSectionDeck();
            var after = PresentationOutlineBuilder.EnforceTotalPageCount(before, 3);

            var (_, _, lost) = EvalRubric.Retention(spec, before, after);

            Assert.NotEmpty(lost);                                  // 壓到三頁一定有東西沒了
            Assert.All(lost, id => Assert.StartsWith("D", id));     // 而且說得出是哪幾項
        }

        // ───────────────────────── 真正要跑的那十題也得過關 ─────────────────────────

        /// <summary>
        /// 評測題目：測試專案自己的 TestData/eval/tasks（複製自本機的 docs/eval，docs/ 不上傳 GitHub）。
        /// 從編譯時記下的原始碼路徑找，而不是從執行目錄——建置輸出被導到專案外的暫存資料夾。
        /// </summary>
        private static string TasksDir([System.Runtime.CompilerServices.CallerFilePath] string? here = null)
        {
            string candidate = Path.Combine(Path.GetDirectoryName(here) ?? "", "TestData", "eval", "tasks");
            if (Directory.Exists(candidate)) return candidate;
            throw new DirectoryNotFoundException("找不到 TestData/eval/tasks：" + candidate);
        }

        /// <summary>
        /// repo 裡那十份清單必須解析得過、而且每一題都要有 critical 項。
        /// 沒有 critical 項的題目永遠不會不及格，等於沒有驗收條件；
        /// 清單是跟程式一起版本控管的，所以寫壞要在測試階段就爆掉。
        /// </summary>
        [Fact]
        public void ShippedTaskChecklistsAreValid()
        {
            var files = Directory.GetFiles(TasksDir(), "*.json");
            Assert.True(files.Length >= 10, $"只找到 {files.Length} 題，計畫是 10 題");

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                var spec = EvalRubricSpec.Parse(File.ReadAllText(file));

                Assert.True(ids.Add(spec.TaskId), "task_id 重複：" + spec.TaskId);
                Assert.False(string.IsNullOrWhiteSpace(spec.Prompt), spec.TaskId + " 沒有題目原文");
                Assert.Contains(spec.Required, x => x.Critical);

                // 有 source 項就必須列人工抽查：機器只驗網域，不驗那一頁是否支持數字。
                if (spec.Required.Any(x => x.Kind == EvalRequiredItem.KindSource))
                    Assert.NotEmpty(spec.HumanChecks);
            }
        }

        /// <summary>
        /// 陷阱題要真的還是陷阱。
        /// 原本的題目是「2026 年諾貝爾物理獎」——該獎在 2026 年十月已經公布，
        /// 那一題從那天起就不再測幻覺，而是測即時資料。所以題目帶有效期限，並在這裡釘住。
        /// </summary>
        [Fact]
        public void TheHallucinationTrapHasNotExpired()
        {
            var spec = EvalRubricSpec.Parse(File.ReadAllText(Path.Combine(TasksDir(), "T10.json")));

            Assert.False(string.IsNullOrWhiteSpace(spec.ExpiresOn), "陷阱題必須寫有效期限");
            Assert.False(spec.IsExpired(DateTime.Now),
                $"T10 已於 {spec.ExpiresOn} 失效——答案可能已經公開，換題再跑");
            Assert.Contains(spec.Required, x => x.Kind == EvalRequiredItem.KindDeclines);
            Assert.NotEmpty(spec.Forbidden);
        }

        /// <summary>T06 / T09 的固定素材必須存在：三組貼的是同一份，否則比較不成立。</summary>
        [Theory]
        [InlineData("T06_abstract_en.txt", 1500)]
        [InlineData("T09_article_zh.txt", 1200)]
        public void FixedMaterialsExist(string name, int minLength)
        {
            string path = Path.Combine(Path.GetDirectoryName(TasksDir())!, "materials", name);
            Assert.True(File.Exists(path), "缺少固定素材：" + path);
            Assert.True(File.ReadAllText(path).Length >= minLength, name + " 太短，不足以當評測素材");
        }

        /// <summary>
        /// 用自己寫的那篇文章當「完美答案」去驗 T09 的清單：
        /// 原文含有全部五個重點，所以五個重點項都該命中。
        /// 這是在驗清單寫得對不對——清單漏抓真正命中的內容，分數就會系統性偏低。
        /// </summary>
        [Fact]
        public void T09ChecklistMatchesItsOwnSourceArticle()
        {
            string tasks = TasksDir();
            var spec = EvalRubricSpec.Parse(File.ReadAllText(Path.Combine(tasks, "T09.json")));
            string article = File.ReadAllText(
                Path.Combine(Path.GetDirectoryName(tasks)!, "materials", "T09_article_zh.txt"));

            var score = EvalRubric.Score(spec, article);

            // R1～R5 是五個重點；原文一定全含。
            foreach (string id in new[] { "R1", "R2", "R3", "R4", "R5" })
                Assert.Contains(id, score.CoveredIds);

            Assert.Empty(score.ForbiddenHitIds);
        }

        /// <summary>
        /// 列詞不是摘要。
        ///
        /// 2026-10-28 第四輪 AI 輔助審查：只送出「排隊、變異、批量、瓶頸、量測。」這一行，
        /// 原本的 T09 清單判成 5/6、83.3%、及格——因為五個重點各只要求一個關鍵詞。
        /// 現在每個重點改為「至少兩個相關字樣共同出現」（概念＋後果才算在陳述主張），
        /// 並把字數下限列為必備項。字樣比對永遠不等於內容判讀，但這條差距要縮到列詞無法過關。
        /// </summary>
        [Fact]
        public void ABareKeywordListDoesNotSatisfyTheSummaryTask()
        {
            var spec = EvalRubricSpec.Parse(File.ReadAllText(Path.Combine(TasksDir(), "T09.json")));

            var score = EvalRubric.Score(spec, "排隊、變異、批量、瓶頸、量測。");

            Assert.False(score.CriticalItemsSatisfied);
            Assert.True(score.Coverage < 0.5,
                $"列詞仍命中 {score.RequiredCovered}/{score.RequiredTotal}，清單還是太鬆");
        }
    }
}
