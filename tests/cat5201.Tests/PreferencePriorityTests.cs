using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 個人化的優先順序只能有一份規則。之前一般節點說「本次請求 ＞ 全域偏好」，
    /// 文件生成卻說「全域偏好最高優先」——全域設繁中、這次要求英文時，
    /// 模型會同時收到兩條互相矛盾的指令。
    /// </summary>
    public class PreferencePriorityTests
    {
        [Fact]
        public void EveryPromptUsesTheSameRule()
        {
            // 規則本身要講清楚「本次請求優先」，否則矛盾只是換個地方出現。
            Assert.Contains("本次請求", PreferencePriority.Rule);
            Assert.Contains("全域偏好", PreferencePriority.Rule);
            Assert.DoesNotContain("最高優先", PreferencePriority.Rule);

            // 文件生成的提示不能再自己宣稱偏好最高優先。
            string documentPrompt = DocumentSkillsPrompt.BuildUserPrompt(new DocumentSkillsPrompt.Request
            {
                UserInput = "做一份簡報",
                WantsDeck = true,
                PreferenceBlock = "- 回答一律用繁體中文",
            });

            Assert.Contains(PreferencePriority.Rule, documentPrompt);
            Assert.DoesNotContain("最高優先", documentPrompt);
        }

        [Fact]
        public void RequestedLanguageIsResolvedBeforeItReachesTheModel()
        {
            // 本次指定英文時，提示要直接寫出「本次輸出語言：English」，
            // 而不是把兩條規則丟給模型自己喬。
            string prompt = DocumentSkillsPrompt.BuildUserPrompt(new DocumentSkillsPrompt.Request
            {
                UserInput = "make me a deck in English",
                WantsDeck = true,
                PreferenceBlock = "- 回答一律用繁體中文",
                Language = "English",
            });

            Assert.Contains("本次輸出語言】English", prompt);
        }

        [Theory]
        [InlineData("- 回答一律用繁體中文", "English", true)]
        [InlineData("- 回答一律用繁體中文", "", false)]
        [InlineData("- 簡報盡量精簡", "English", false)]
        public void LanguagePreferenceIsMarkedOverriddenOnlyWhenThisRequestSaysSo(
            string preference, string requestedLanguage, bool overridden)
        {
            var effective = PreferencePriority.Resolve(preference, requestedLanguage);

            if (overridden)
            {
                Assert.Single(effective.Overridden);
                Assert.Contains(requestedLanguage, effective.Overridden[0]);
                Assert.Contains("本次請求覆寫", effective.Summary());
            }
            else
            {
                Assert.Empty(effective.Overridden);
                Assert.Single(effective.Applied);
            }
        }

        [Fact]
        public void NoPreferencesMeansNothingToShow()
            => Assert.Equal("", PreferencePriority.Resolve("", "English").Summary());
    }
}
