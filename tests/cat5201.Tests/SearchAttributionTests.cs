using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 決策窗不得顯示沒有被呼叫、也沒有被計費的模型。
    ///
    /// 2026-10-29 使用者回報：關掉 Auto 之後，決策窗的「模型」欄仍寫著 Perplexity Sonar。
    /// 查下去是工作區推導產物模型時寫死了「搜尋固定由 Perplexity Sonar 執行」；
    /// 實際上搜尋走的是 Perplexity Agent API + openai/gpt-5.6-sol，
    /// 九次評測執行的逐筆帳目裡一次 pplx-sonar 都沒有（D 組的 pplx-sonar 是主回覆模型，另一回事）。
    ///
    /// 對一個主打「每一次呼叫都攤開給你看」的產品，顯示一個沒跑過的模型是最不能出現的那種錯：
    /// 它不是少講，是多講，而且多講的部分看起來最像證據。
    /// </summary>
    public class SearchAttributionTests
    {
        private static AgentWorkspaceArtifactRecord BuildRecord(
            string itemType, object payload, string modelId)
        {
            var workspace = new AgentWorkspace();
            workspace.Add(new AgentWorkspaceItem
            {
                RunId = "run-1",
                NodeId = "node-1",
                SourceAgentId = "research-agent",
                ModelId = modelId,
                ItemType = itemType,
                ArtifactKind = itemType == "verified_facts" ? "facts" : "search",
                Payload = payload,
                CreatedAtUtc = DateTime.UtcNow,
            });

            return workspace.BuildSummary().Artifacts.Single();
        }

        [Fact]
        public void SearchArtifactReportsTheModelThatActuallyRan()
        {
            var record = BuildRecord(
                "search_summary",
                new SearchSummaryPayload { Status = SearchStatus.Succeeded },
                "openai/gpt-5.6-sol");

            Assert.Equal("openai/gpt-5.6-sol", record.ModelId);
        }

        [Fact]
        public void FactsArtifactReportsTheModelThatActuallyRan()
        {
            var record = BuildRecord(
                "verified_facts",
                new VerifiedFactPayload { Facts = new List<VerifiedFactItem>() },
                "openai/gpt-5.6-sol");

            Assert.Equal("openai/gpt-5.6-sol", record.ModelId);
        }

        /// <summary>
        /// 推導不出模型時要留空，不要猜一個名字。
        /// 留空只是少講一件事；猜錯是把沒跑過的模型講成參與者。
        /// </summary>
        [Theory]
        [InlineData("search_summary")]
        [InlineData("verified_facts")]
        public void UnknownModelStaysEmptyInsteadOfGuessingPerplexity(string itemType)
        {
            object payload = itemType == "verified_facts"
                ? new VerifiedFactPayload { Facts = new List<VerifiedFactItem>() }
                : new SearchSummaryPayload { Status = SearchStatus.Succeeded };

            var record = BuildRecord(itemType, payload, modelId: "");

            Assert.Equal("", record.ModelId);
            Assert.DoesNotContain("pplx", record.ModelId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>能力層回報的模型要一路帶到產物上，中間不能被吃掉。</summary>
        [Fact]
        public void CapabilityResultCarriesItsModelId()
        {
            var result = AgentCapabilityResult.WithData(
                "search_summary",
                new SearchSummaryPayload { Status = SearchStatus.Succeeded },
                "openai/gpt-5.6-sol");

            Assert.Equal("openai/gpt-5.6-sol", result.ModelId);

            var item = AgentWorkspaceBuilder.FromCapabilityData(
                new AgentWorkspace(), null!, "research-agent",
                "search_summary", result.Data["search_summary"], modelId: result.ModelId);

            Assert.Equal("openai/gpt-5.6-sol", item.ModelId);
        }

        /// <summary>沒有指定模型時，WithData 維持舊行為（空字串），不會變成 null。</summary>
        [Fact]
        public void WithDataWithoutModelIdIsEmptyNotNull()
            => Assert.Equal("", AgentCapabilityResult.WithData("x", new object()).ModelId);
    }
}
