using System.Collections.Generic;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>§19 Skills 注入：注入區塊的組成規則（啟用才注入、空內容不收、停用保留不注入）。</summary>
    public class SkillsRegistryTests
    {
        [Fact]
        public void BuildPromptBlock_Empty_WhenNoSkills()
        {
            try
            {
                SkillsRegistry.SetAll(null);
                Assert.Equal("", SkillsRegistry.BuildPromptBlock());
            }
            finally { SkillsRegistry.SetAll(null); }
        }

        [Fact]
        public void BuildPromptBlock_ContainsEnabledSkill_OmitsDisabled()
        {
            try
            {
                SkillsRegistry.SetAll(new List<SkillDefinition>
                {
                    new() { Name = "週報格式", Content = "以三段式撰寫", Enabled = true },
                    new() { Name = "停用的", Content = "不該出現的內容", Enabled = false },
                });

                string block = SkillsRegistry.BuildPromptBlock();
                Assert.Contains("週報格式", block);
                Assert.Contains("以三段式撰寫", block);
                Assert.DoesNotContain("不該出現的內容", block);
                Assert.Contains("使用者技能", block);
            }
            finally { SkillsRegistry.SetAll(null); }
        }

        [Fact]
        public void SetAll_DropsEmptyContent()
        {
            try
            {
                SkillsRegistry.SetAll(new List<SkillDefinition>
                {
                    new() { Name = "沒內容", Content = "  ", Enabled = true },
                });
                Assert.Empty(SkillsRegistry.GetAll());
            }
            finally { SkillsRegistry.SetAll(null); }
        }
    }
}
