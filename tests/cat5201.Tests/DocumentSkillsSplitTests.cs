using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-15：`prompt is too long: 1,634,773 tokens`，而我們自己的提示只有 1,419 字。
    /// 核對過官方規格，送出去的每個欄位都是對的——那一百多萬 token 幾乎全是
    /// 伺服器注入的技能內容。一次掛三個技能（pptx + docx + xlsx）就會爆掉。
    ///
    /// 拆成一次一個技能：注入量剩三分之一，而且一種產出失敗不會連累另外兩種。
    /// </summary>
    public class DocumentSkillsSplitTests
    {
        private static DocumentSkillsPrompt.Request Req(bool deck, bool report, bool table)
            => new()
            {
                UserInput = "做一份簡報、一份報告和一張表",
                MainContent = "內容",
                WantsDeck = deck,
                WantsReport = report,
                WantsTable = table,
            };

        [Fact]
        public void 三種產出會拆成三次請求()
        {
            var parts = DocumentSkillsPrompt.SplitByArtifact(Req(true, true, true));

            Assert.Equal(3, parts.Count);

            // 每一份最多掛一個技能；簡報那一份一個都不掛
            //（pptx 技能注入超過 100 萬 token，改用沙箱預裝的 python-pptx）。
            Assert.All(parts, p => Assert.True(DocumentSkillsPrompt.SkillIds(p).Count <= 1));
            Assert.Empty(DocumentSkillsPrompt.SkillIds(parts.Single(p => p.WantsDeck)));
            Assert.Single(DocumentSkillsPrompt.SkillIds(parts.Single(p => p.WantsReport)));
            Assert.Single(DocumentSkillsPrompt.SkillIds(parts.Single(p => p.WantsTable)));
        }

        [Fact]
        public void 每一份子請求只保留自己那一種()
        {
            var parts = DocumentSkillsPrompt.SplitByArtifact(Req(true, true, true));

            Assert.Contains(parts, p => p.WantsDeck && !p.WantsReport && !p.WantsTable);
            Assert.Contains(parts, p => !p.WantsDeck && p.WantsReport && !p.WantsTable);
            Assert.Contains(parts, p => !p.WantsDeck && !p.WantsReport && p.WantsTable);
        }

        [Fact]
        public void 每一份子請求的副檔名也只剩自己那一種()
        {
            var deck = DocumentSkillsPrompt.SplitByArtifact(Req(true, true, true))
                .Single(p => p.WantsDeck);

            var exts = DocumentSkillsPrompt.WantedExtensions(deck);
            Assert.Contains(".pptx", exts);
            Assert.DoesNotContain(".docx", exts);
            Assert.DoesNotContain(".xlsx", exts);
            Assert.Contains(".pdf", exts);   // 對照 PDF 一直都要
        }

        [Fact]
        public void 只要一種的時候不會多拆()
        {
            Assert.Single(DocumentSkillsPrompt.SplitByArtifact(Req(true, false, false)));
            Assert.Single(DocumentSkillsPrompt.SplitByArtifact(Req(false, false, true)));
        }

        [Fact]
        public void 其他欄位原樣帶過去()
        {
            var original = Req(true, true, false) with { RequestedSlides = 12, WantsImages = true, Language = "English" };
            var deck = DocumentSkillsPrompt.SplitByArtifact(original).Single(p => p.WantsDeck);

            Assert.Equal(12, deck.RequestedSlides);
            Assert.True(deck.WantsImages);
            Assert.Equal("English", deck.Language);
            Assert.Equal(original.MainContent, deck.MainContent);
        }

        [Fact]
        public void 什麼都不要時不會回空清單()
        {
            // 呼叫端照舊跑一次就好，不要因為清單是空的而靜靜什麼都不做。
            Assert.Single(DocumentSkillsPrompt.SplitByArtifact(Req(false, false, false)));
        }
    }
}
