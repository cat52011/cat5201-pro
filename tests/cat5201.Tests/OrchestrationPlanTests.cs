using System.Linq;
using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>
    /// 編排規劃腦（OrchestrationPlanner.Build）的整合式測試：
    /// 給一句輸入，驗證「任務型別 + 產出階段組合」是否正確。
    /// 這層錯＝多產檔/漏產檔/純手動被偷加媒體——全是使用者直接看得到的行為。
    /// </summary>
    public class OrchestrationPlanTests
    {
        private static OrchestrationPlanPayload Plan(
            string input, OutputIntent? intent = null, bool allowMedia = true)
            => OrchestrationPlanner.Build(
                input, decision: null!, runtimeAgent: null!, capabilityPlan: null!,
                autoMode: false, hasAttachments: false,
                outputIntent: intent, allowMediaGeneration: allowMedia);

        private static bool HasStage(OrchestrationPlanPayload plan, string stageId)
            => plan.Stages.Any(s => s.Id == stageId);

        [Fact]
        public void PlainChat_NoGenerationStages()
        {
            var plan = Plan("幫我寫一段自我介紹");
            Assert.False(HasStage(plan, "generate_image"));
            Assert.False(HasStage(plan, "generate_video"));
            Assert.False(HasStage(plan, "presentation_outline"));
            Assert.False(HasStage(plan, "generate_file"));
            Assert.True(HasStage(plan, "final_synthesis")); // 主合成永遠在
        }

        [Fact]
        public void Presentation_AddsOutlineStage()
        {
            var plan = Plan("幫我做一份簡報");
            Assert.Equal(OrchestrationTaskType.Presentation, plan.TaskType);
            Assert.True(HasStage(plan, "presentation_outline"));
        }

        [Fact]
        public void Presentation_Denied_DowngradesToText()
        {
            // 使用者在確認框選「否」→ 降級純文字，不得殘留任何產出階段。
            var plan = Plan("幫我做一份簡報", allowMedia: false);
            Assert.NotEqual(OrchestrationTaskType.Presentation, plan.TaskType);
            Assert.False(HasStage(plan, "presentation_outline"));
            Assert.True(HasStage(plan, "final_synthesis"));
        }

        [Fact]
        public void ImageRequest_AddsImageStage()
        {
            var plan = Plan("生成一張關於這個作曲家的照片");
            Assert.Equal(OrchestrationTaskType.ImageGeneration, plan.TaskType);
            Assert.True(HasStage(plan, "generate_image"));
        }

        [Fact]
        public void LlmIntent_OverridesKeywordMiss_ForVideo()
        {
            // 關鍵字抓不到，但第一層 LLM 意圖說要影片 → 以 LLM 為準。
            var intent = new OutputIntent { WantsVideo = true, Source = "test" };
            var plan = Plan("來一個十五秒的東西", intent);
            Assert.Equal(OrchestrationTaskType.VideoGeneration, plan.TaskType);
            Assert.True(HasStage(plan, "generate_video"));
        }

        [Fact]
        public void PresentationPlusReport_AddsBothStages()
        {
            var intent = new OutputIntent { WantsPresentation = true, WantsReport = true, Source = "test" };
            var plan = Plan("幫我做一份簡報", intent);
            Assert.True(HasStage(plan, "presentation_outline"));
            Assert.True(HasStage(plan, "generate_file")); // 簡報＋書面報告 → 兩個產出階段都要有
        }

        [Fact]
        public void DeniedIntent_ProducesNoStages_EvenWithWants()
        {
            var intent = new OutputIntent { WantsPresentation = true, WantsReport = true, WantsImage = true, Source = "test" };
            var plan = Plan("幫我做一份簡報", intent, allowMedia: false);
            Assert.False(HasStage(plan, "presentation_outline"));
            Assert.False(HasStage(plan, "generate_file"));
            Assert.False(HasStage(plan, "generate_image"));
        }
    }

    /// <summary>Fallback 候選鏈：主模型永遠第一、不重複。</summary>
    public class AiFallbackPlannerTests
    {
        [Fact]
        public void Candidates_StartWithPrimary_NoDuplicates()
        {
            string primary = AiModelRegistry.Default.Id;
            var candidates = AiFallbackPlanner.BuildCandidates(primary, NodeTaskMode.Chat);

            Assert.NotEmpty(candidates);
            Assert.Equal(
                AiModelHelper.NormalizeNodeModel(primary),
                candidates[0].ModelId);
            Assert.Equal(
                candidates.Count,
                candidates.Select(c => c.ModelId.ToLowerInvariant()).Distinct().Count());
        }
    }
}
