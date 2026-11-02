using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-29 手動測試回報的幾個問題，各釘一條：
    /// 「模型」欄只顯示主模型、Auto 研究任務誤報「即時資料未取得」、
    /// 匯流節點收到內部結束記號、Claude 簡報的照片放錯頁。
    /// </summary>
    public class ManualTestFixesTests
    {
        // ── 「即時資料未取得」該不該警告 ──

        [Theory]
        [InlineData("做一份 5 頁簡報，介紹澳洲特有美食")]
        [InlineData("幫我整理日本茶道的歷史")]
        [InlineData("I know what you mean")]
        [InlineData("")]
        public void NotTimeSensitive(string text)
            => Assert.False(FinanceTaskDetector.IsTimeSensitive(text));

        [Theory]
        [InlineData("台積電最新一季營收是多少")]
        [InlineData("今天台北的天氣")]
        [InlineData("What is the current price of $NVDA")]
        [InlineData("比較美元兌日圓匯率")]
        public void TimeSensitive(string text)
            => Assert.True(FinanceTaskDetector.IsTimeSensitive(text));

        /// <summary>
        /// 一般研究只有背景摘錄、沒有任何數字型欄位：搜尋有結果就是「搜尋結果」，
        /// 不是「需要的數字一個都沒取得」。實測時答案頂端曾出現後者，搜尋明明有結果。
        /// </summary>
        [Fact]
        public void BackgroundOnlyFacts_WithSearchResults_AreSearchResults()
        {
            var data = new Dictionary<string, object>
            {
                ["verified_facts"] = new VerifiedFactPayload
                {
                    Facts = new List<VerifiedFactItem>
                    {
                        new()
                        {
                            AuthorityLevel = FactOwnership.AuthoritySearchContext,
                            UsageRole = FactOwnership.UsageBackgroundContext,
                            Value = "未取得",
                        },
                    },
                },
                ["search_summary"] = new SearchSummaryPayload
                {
                    Query = "澳洲特有美食",
                    Items = new List<SearchSummaryItem> { new() { Title = "Vegemite", KeyPoint = "酵母抹醬" } },
                },
            };

            var a = FreshDataAssessment.Evaluate(data);

            Assert.Equal(FreshDataAssessment.StatusSearchResults, a.Status);
            Assert.True(a.HasUsableData);
            Assert.DoesNotContain("即時資料未取得", a.BuildUserBanner());
        }

        /// <summary>真的該有數字、卻一個都沒拿到的，仍然要判成沒資料——上一條的修改不能把這條放掉。</summary>
        [Fact]
        public void NumericFactsAllMissing_StillNoResults()
        {
            var data = new Dictionary<string, object>
            {
                ["verified_facts"] = new VerifiedFactPayload
                {
                    Facts = new List<VerifiedFactItem>
                    {
                        new()
                        {
                            AuthorityLevel = FactOwnership.AuthorityMarketQuote,
                            UsageRole = FactOwnership.UsageNumericFactSource,
                            Value = "not_available",
                        },
                    },
                },
                ["search_summary"] = new SearchSummaryPayload
                {
                    Query = "2330 收盤價",
                    Items = new List<SearchSummaryItem> { new() { Title = "t", KeyPoint = "k" } },
                },
            };

            var a = FreshDataAssessment.Evaluate(data);

            Assert.Equal(FreshDataAssessment.StatusNoResults, a.Status);
            Assert.StartsWith("⚠️【即時資料未取得】", a.BuildUserBanner());
        }

        // ── 橫幅不用 **粗體**（串流時原文會露出記號） ──

        [Fact]
        public void Banners_HaveNoMarkdownBold()
        {
            Assert.DoesNotContain("**", FreshDataAssessment.ForStatus(FreshDataAssessment.StatusSearchResults).BuildUserBanner());
            Assert.DoesNotContain("**", FreshDataAssessment.ForStatus(FreshDataAssessment.StatusDomainChecked).BuildUserBanner());
            Assert.DoesNotContain("**", FreshDataAssessment.ForStatus(FreshDataAssessment.StatusServiceFailed).BuildUserBanner());
            Assert.DoesNotContain("**", NumberConsistency.BuildNotice(new[] { new NumberConsistency.Finding("1,494.24 billion", "1,494.24 億", -1) }));
        }

        // ── 「模型」欄：所有實際參與的模型 ──

        [Fact]
        public void ModelField_ListsEveryModelThatWasCalled()
        {
            var log = new AiExecutionLogEntry
            {
                SelectionMode = "Collaboration",
                RequestedModelId = AiModels.OpenAi_Gpt56,
                PlannedModelId = AiModels.OpenAi_Gpt56,
                ActualModelId = AiModels.OpenAi_Gpt56,
                Success = true,
                UsageRecords = new List<UsageRecord>
                {
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.OpenAi_Gpt56, Purpose = "協作第一輪", InputTokens = 900, OutputTokens = 600 },
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "協作第一輪", InputTokens = 900, OutputTokens = 600 },
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "交叉檢視", InputTokens = 900, OutputTokens = 300 },
                    new() { Kind = UsageKinds.Search, ModelId = "brave-search", Purpose = "搜尋" },
                },
            };

            var view = NodeDecisionViewBuilder.BuildFromLog(log);

            Assert.Equal("GPT-5.6 Sol + Claude Sonnet 5.5", view.Model);
        }

        // ── 匯流：上游的內部結束記號不能進下游 ──

        [Fact]
        public void UpstreamOutput_EndMarkerIsStripped()
        {
            Assert.Equal("答案內容", UpstreamMerge.CleanOutput("答案內容\n[[END_OF_RESPONSE]]"));
            Assert.Equal("", UpstreamMerge.CleanOutput(null));
        }

        // ── 匯流節點：要不要查即時資料，只看使用者自己寫的那句話 ──

        [Fact]
        public void OwnInstruction_ExcludesUpstreamContent()
        {
            const string template = "比較以下兩份回答，列出彼此矛盾的地方：{{input}}";
            string injected = template.Replace("{{input}}", "上游說目前市場利率偏高、今天股價大漲。");

            string? own = UpstreamMerge.OwnInstruction(template, injected);

            Assert.Equal("比較以下兩份回答，列出彼此矛盾的地方：", own);
            // 上游內容會觸發「需要即時資料」，使用者自己那句話不會——第二輪手測的誤報就是前者造成的。
            Assert.True(FinanceTaskDetector.IsTimeSensitive(injected));
            Assert.False(FinanceTaskDetector.IsTimeSensitive(own));
        }

        [Theory]
        [InlineData("{{input}}", "上游內容")]                       // 模板只有佔位符：沒有使用者自己的話
        [InlineData("比較：{{input}}", "使用者後來整段改寫的新要求")]  // 文字不是這個模板套出來的
        [InlineData("", "一般輸入")]                                // 沒有模板
        public void OwnInstruction_FallsBackToWholeInput(string template, string topText)
            => Assert.Null(UpstreamMerge.OwnInstruction(template, topText));

        [Fact]
        public void OwnInstruction_KeepsTextOnBothSidesOfPlaceholder()
            => Assert.Equal("請比較 然後列出結論",
                UpstreamMerge.OwnInstruction("請比較{{input}}然後列出結論", "請比較【A】【B】然後列出結論"));

        // ── 頁數：使用者說 N 頁＝封面＋內容 N 頁，資料來源另加 1 頁 ──

        private static PresentationOutlinePayload Outline(int contentSlides, bool withSources)
        {
            var slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "澳洲特有美食" },
            };
            for (int i = 0; i < contentSlides; i++)
                slides.Add(new() { Order = slides.Count + 1, Kind = "content", Heading = $"第 {i + 1} 個主題",
                                   Bullets = new List<string> { "重點一", "重點二" } });
            if (withSources)
                slides.Add(new() { Order = slides.Count + 1, Kind = "sources", Heading = "資料來源",
                                   Bullets = new List<string> { "Tourism Australia", "ABC News" } });
            return new PresentationOutlinePayload { Title = "澳洲特有美食", Slides = slides, SlideCount = slides.Count };
        }

        [Fact]
        public void FivePages_GivesFivePlusSourcesPage()
        {
            var result = PresentationOutlineBuilder.EnforcePageCountPlusSources(Outline(8, withSources: true), 5);

            Assert.Equal(6, result.Slides.Count);
            Assert.Equal(6, DeckScene.CountPages(result.Slides, null));
            Assert.Equal("cover", result.Slides[0].Kind);
            Assert.Equal("sources", result.Slides[^1].Kind);
            Assert.Equal(4, result.Slides.Count(x => x.Kind == "content"));
            Assert.Equal(Enumerable.Range(1, 6), result.Slides.Select(x => x.Order));
        }

        [Fact]
        public void NoSourcesPage_StaysAtRequestedCount()
        {
            var result = PresentationOutlineBuilder.EnforcePageCountPlusSources(Outline(8, withSources: false), 5);

            Assert.Equal(5, result.Slides.Count);
            Assert.DoesNotContain(result.Slides, x => x.Kind == "sources");
        }

        [Fact]
        public void OnePage_StillKeepsTheSourcesPage()
        {
            var result = PresentationOutlineBuilder.EnforcePageCountPlusSources(Outline(3, withSources: true), 1);

            Assert.Equal(2, result.Slides.Count);
            Assert.Equal("content", result.Slides[0].Kind);
            Assert.Equal("sources", result.Slides[1].Kind);
        }

        [Fact]
        public void AuthorPrompt_AsksForContentSlidesPlusSources()
        {
            string prompt = PresentationAuthor.BuildAuthorPrompt("做一份 5 頁簡報，介紹澳洲特有美食", null, null, 5);

            Assert.Contains("封面＋內容正好 5 頁", prompt);
            Assert.Contains("內容投影片正好 4 張", prompt);
        }

        /// <summary>手動 GPT 沒有搜尋、作者沒給來源：仍然要有來源頁，照實寫沒查外部資料（第三輪手測只交出 5 頁）。</summary>
        [Fact]
        public void NoSources_StillGetsHonestSourcesPage_AndFivePlusOne()
        {
            var withPage = PresentationOutlineBuilder.EnsureSourcesSlide(Outline(6, withSources: false));
            Assert.Equal("sources", withPage.Slides[^1].Kind);
            Assert.Contains(PresentationOutlineBuilder.NoExternalSourcesNote, withPage.Slides[^1].Bullets);

            var result = PresentationOutlineBuilder.EnforcePageCountPlusSources(withPage, 5);
            Assert.Equal(6, result.Slides.Count);
            Assert.Equal("sources", result.Slides[^1].Kind);
        }

        [Fact]
        public void ExistingSourcesPage_IsNotDuplicated()
        {
            var outline = Outline(3, withSources: true);
            Assert.Same(outline, PresentationOutlineBuilder.EnsureSourcesSlide(outline));
        }

        /// <summary>沒有重點的內容頁：標題只出現一次（第三輪手測那頁上下各印了一遍）。</summary>
        [Fact]
        public void SlideWithoutBullets_ShowsTitleOnce()
        {
            var outline = new PresentationOutlinePayload
            {
                Title = "澳洲滋味：從原生食材到國民美食",
                Slides = new List<PresentationSlidePayload>
                {
                    new() { Order = 1, Kind = "cover", Heading = "澳洲滋味：從原生食材到國民美食" },
                    new() { Order = 2, Kind = "content", Heading = "澳洲滋味：自然與文化的交會" },
                },
            };

            var page = DeckScene.Build(outline, null)[1];

            Assert.Single(page.Elements, e => e.Text == "澳洲滋味：自然與文化的交會");
        }

        // ── Claude 簡報的照片：說明畫什麼，依內容擺，不依檔名擺 ──

        [Fact]
        public void PhotoBlock_DescribesEachPhoto_AndForbidsPlacingByFileName()
        {
            var photos = new List<ClaudeDocumentSkillsService.ContainerFile>
            {
                new("f0", "cover.png", "封面用；主題：澳洲特有美食"),
                new("f1", "photo-a.png", AgentRuntime.DescribeSlidePhoto("肉類與野味", "grilled kangaroo steak on a wooden board")),
                new("f2", "photo-b.png", AgentRuntime.DescribeSlidePhoto("海鮮", "Moreton Bay bugs on ice")),
            };

            string block = AgentRuntime.PhotoBlock(photos);

            Assert.Contains("photo-a.png：為講「肉類與野味」的內容生成；畫面主體：grilled kangaroo steak", block);
            Assert.Contains("photo-b.png：為講「海鮮」的內容生成；畫面主體：Moreton Bay bugs", block);
            Assert.Contains("不要依檔名順序擺", block);
            Assert.Contains("找不到相符的頁就不要放", block);
        }

        [Fact]
        public void DescribeSlidePhoto_HandlesMissingParts()
        {
            Assert.Equal("為內容頁生成", AgentRuntime.DescribeSlidePhoto(null, null));
            Assert.Equal("為講「海鮮」的內容生成", AgentRuntime.DescribeSlidePhoto("海鮮", "  "));
            Assert.EndsWith("…", AgentRuntime.DescribeSlidePhoto("x", new string('a', 300)));
        }
    }
}
