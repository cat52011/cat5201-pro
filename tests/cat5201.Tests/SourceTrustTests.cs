using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 信任判定的三個分界，每一個都是 2026-10-28 AI 輔助審查用合成反例打穿過的：
    ///   ① 網址存在 ≠ 來源身分確認　② 標籤降級 ≠ 承認沒資料　③ 名單比對要用網域不是字串包含
    /// </summary>
    public class SourceTrustTests
    {
        // ───────── ① 只有身分查得出來的才算官方 ─────────

        /// <summary>
        /// 這四個網址都「長得像」官方，但沒有一個能證明身分。
        /// 尤其 sec.gov.untrusted.example：上一版用 host.Contains(".gov.") 比對，直接被它穿過去。
        /// </summary>
        [Theory]
        [InlineData("https://untrusted.example/blog")]
        [InlineData("https://untrusted.example/investor/fake")]
        [InlineData("https://investor.untrusted.example/fake")]
        [InlineData("https://sec.gov.untrusted.example/fake")]
        [InlineData("https://www.sec.gov.evil.example/fake")]
        [InlineData("https://notsec.gov/fake")]
        public void LookalikeUrlsNeverBecomeOfficial(string url)
        {
            Assert.False(SearchCapability.IsOfficialSourceUrl(url));

            // 連帶：來源等級與信心都不得被網址本身撐上去。
            Assert.Equal(FactOwnership.AuthoritySearchContext,
                SearchCapability.ResolveFinanceAuthority("revenue", "Source: " + url));
            Assert.Equal("medium", SearchCapability.ResolveConfidence("Source: " + url));
        }

        [Theory]
        [InlineData("https://www.sec.gov/edgar/data/1/x.htm")]
        [InlineData("https://sec.gov/x")]
        [InlineData("https://mops.twse.com.tw/mops/web/t05st01")]
        public void AllowlistedRegulatorsAreOfficial(string url)
        {
            Assert.True(SearchCapability.IsOfficialSourceUrl(url));
            Assert.Equal(FactOwnership.AuthorityOfficial,
                SearchCapability.ResolveFinanceAuthority("revenue", "Source: " + url));

            // 證據只走到「網域在白名單上」——沒有人把那一頁抓下來讀過，
            // 所以信心封頂在 medium。https://www.sec.gov/not-a-real-document 也會走到這裡。
            Assert.Equal(FactOwnership.SourceDomainAllowlisted,
                SearchCapability.ResolveSourceVerification("Source: " + url));
            Assert.Equal("medium", SearchCapability.ResolveConfidence("Source: " + url));
        }

        /// <summary>
        /// 第三輪審查的核心反問：模型捏造一個數字、附上真正的 SEC 網域，哪一步擋得住？
        /// 擋不住「網域比對」，但擋得住「high 信心」——證據階段沒走到核對原文就不給。
        /// </summary>
        [Fact]
        public void FabricatedPathOnARealRegulatorDomainIsNeverHighConfidence()
        {
            const string fake = "https://www.sec.gov/not-a-real-document";

            Assert.True(SearchCapability.IsOfficialSourceUrl(fake));          // 網域確實是 SEC
            Assert.Equal("medium", SearchCapability.ResolveConfidence(fake)); // 但沒核對過原文
            Assert.Equal(FactOwnership.SourceDomainAllowlisted,
                SearchCapability.ResolveSourceVerification(fake));

            // 只有走到「原文支持此主張」才可能是 high——目前系統做不到，所以永遠不會出現。
            Assert.Equal("high",
                FactOwnership.CapConfidence("high", FactOwnership.SourceClaimSupported));
            Assert.Equal("medium",
                FactOwnership.CapConfidence("high", FactOwnership.SourceDocumentFetched));
        }

        /// <summary>網域比對是「是這個網域或它的子網域」，不是「字串裡有這段」。</summary>
        [Theory]
        [InlineData("sec.gov", "sec.gov", true)]
        [InlineData("www.sec.gov", "sec.gov", true)]
        [InlineData("sec.gov.untrusted.example", "sec.gov", false)]
        [InlineData("notsec.gov", "sec.gov", false)]
        [InlineData("xsec.gov", "sec.gov", false)]
        public void HostMatchingUsesDomainBoundaries(string host, string domain, bool expected)
            => Assert.Equal(expected, SearchCapability.HostIsUnder(host, domain));

        /// <summary>沒有網址時，連 medium 都不給。</summary>
        [Fact]
        public void NoUrlMeansLowOrMediumNeverHigh()
        {
            Assert.Equal("low", SearchCapability.ResolveConfidence(""));
            Assert.Equal("medium", SearchCapability.ResolveConfidence("公司財報"));
        }

        // ───────── ② 全部「未取得」就是沒資料 ─────────

        /// <summary>
        /// 降級標籤不等於承認沒資料：上一版把 verified 降成 search_results，
        /// 但 HasUsableData 仍是 true、警告橫幅仍是空的，使用者看到的還是「查過了」。
        /// </summary>
        [Fact]
        public void AllFieldsMissingMeansNoUsableData()
        {
            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    new()
                    {
                        FactType = "revenue", Value = "未取得",
                        AuthorityLevel = FactOwnership.AuthorityOfficial,
                        UsageRole = FactOwnership.UsageNumericFactSource,
                    },
                    new()
                    {
                        FactType = "quote_availability",
                        Value = "regular_close_price=not_available; after_hours_price=not_available",
                        AuthorityLevel = FactOwnership.AuthorityMarketQuote,
                        UsageRole = FactOwnership.UsageBackgroundContext,
                    },
                },
            };

            var a = FreshDataAssessment.Evaluate(
                new Dictionary<string, object> { ["verified_facts"] = facts });

            Assert.False(a.HasUsableData);                       // 不可用
            Assert.NotEqual(FreshDataAssessment.StatusVerified, a.Status);
            Assert.False(string.IsNullOrWhiteSpace(a.BuildUserBanner()));  // 而且要講出來
        }

        /// <summary>
        /// 證據階段必須貫穿到整體判定，而不只是出現在單筆事實上。
        ///
        /// 2026-10-28 第四輪 AI 輔助審查：以真實主管機關網域配編造路徑送進來，
        /// 單筆正確地標成 domain_allowlisted / medium（第三輪已修好），
        /// 但 FreshDataAssessment 只看來源等級與有無數值，整體仍判為 verified、警告橫幅仍是空白。
        /// 新增一個正確的欄位、卻讓舊決策繼續忽略它，使用者看到的結論完全沒變。
        /// </summary>
        [Fact]
        public void DomainOnlyEvidenceIsNotWholeQueryVerified()
        {
            const string fake = "Source: https://www.sec.gov/not-a-real-document";

            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    new()
                    {
                        FactType = "revenue", Value = "US$23.86B",
                        SourceUrl = "https://www.sec.gov/not-a-real-document",
                        AuthorityLevel = SearchCapability.ResolveFinanceAuthority("revenue", fake),
                        Confidence = SearchCapability.ResolveConfidence(fake),
                        SourceVerification = SearchCapability.ResolveSourceVerification(fake),
                        UsageRole = FactOwnership.UsageNumericFactSource,
                    },
                },
            };

            var a = FreshDataAssessment.Evaluate(
                new Dictionary<string, object> { ["verified_facts"] = facts });

            Assert.Equal(FreshDataAssessment.StatusDomainChecked, a.Status);
            Assert.NotEqual(FreshDataAssessment.StatusVerified, a.Status);

            Assert.True(a.HasUsableData);                                   // 數字是拿到了
            Assert.True(a.NeedsSourceCaution);                              // 但查證沒走完
            Assert.Contains("僅核對網域", a.BuildUserBanner());               // 使用者看得到
            Assert.Contains("不得聲稱「已查證」", a.BuildSynthesisNotice());    // 模型也被約束

            // 呼叫端只看這一個屬性決定要不要講話。
            // 2026-10-29 實測九次執行後發現：上面那三個斷言全過，橫幅卻一次都沒出現過，
            // 因為 AgentRuntime 當時是用 !HasUsableData 決定要不要取橫幅，
            // 而這個狀態的 HasUsableData 是 true，整段被無聲跳過。
            // 方法對、呼叫端沒接上，使用者看到的結果就跟沒修一樣。
            Assert.True(a.NeedsUserNotice);
        }

        /// <summary>
        /// 只有走到第三階段（原文支持此主張）才配稱「已查證」。
        /// 本系統做不到第三階段，所以 verified 實際上不會出現——
        /// 跟 high 信心一樣，達不到的標準就不該發放。
        /// </summary>
        [Fact]
        public void OnlyClaimSupportedEvidenceEarnsVerified()
        {
            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    new()
                    {
                        FactType = "revenue", Value = "US$23.86B",
                        AuthorityLevel = FactOwnership.AuthorityOfficial,
                        SourceVerification = FactOwnership.SourceClaimSupported,
                        UsageRole = FactOwnership.UsageNumericFactSource,
                    },
                },
            };

            var a = FreshDataAssessment.Evaluate(
                new Dictionary<string, object> { ["verified_facts"] = facts });

            Assert.Equal(FreshDataAssessment.StatusVerified, a.Status);
            Assert.False(a.NeedsSourceCaution);
            Assert.False(a.NeedsUserNotice);        // 只有這個狀態可以什麼都不說
            Assert.Equal("", a.BuildUserBanner());

            // 而這個階段目前沒有任何程式路徑產生得出來——它是標準，不是現況。
            Assert.Equal(FactOwnership.SourceDomainAllowlisted,
                SearchCapability.ResolveSourceVerification("Source: https://www.sec.gov/x"));
        }

        /// <summary>
        /// 每一種狀態都要明確回答「要不要對使用者講話」。
        /// 這張表是呼叫端唯一該看的東西；漏掉任何一列，就會有一種狀態被無聲跳過。
        /// </summary>
        [Theory]
        [InlineData(FreshDataAssessment.StatusVerified, false)]       // 原文核對過，可以不說
        [InlineData(FreshDataAssessment.StatusDomainChecked, true)]   // 拿到了但沒核對原文
        [InlineData(FreshDataAssessment.StatusSearchResults, true)]   // 只有摘錄
        [InlineData(FreshDataAssessment.StatusNoResults, true)]
        [InlineData(FreshDataAssessment.StatusServiceFailed, true)]
        [InlineData(FreshDataAssessment.StatusNotRun, true)]
        public void EveryStatusAnswersWhetherToWarnTheUser(string status, bool expected)
        {
            var a = FreshDataAssessment.ForStatus(status);

            Assert.Equal(expected, a.NeedsUserNotice);
            Assert.Equal(expected, !string.IsNullOrWhiteSpace(a.BuildUserBanner()));
        }

        /// <summary>有一個真的數字才算拿到資料。</summary>
        [Fact]
        public void OneRealValueIsEnoughToCountAsData()
        {
            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    new()
                    {
                        FactType = "revenue", Value = "US$23.86B",
                        AuthorityLevel = FactOwnership.AuthorityOfficial,
                        UsageRole = FactOwnership.UsageNumericFactSource,
                    },
                },
            };

            var a = FreshDataAssessment.Evaluate(
                new Dictionary<string, object> { ["verified_facts"] = facts });

            Assert.True(a.HasUsableData);
        }
    }
}
