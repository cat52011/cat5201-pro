using System.Collections.Generic;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-06 實測：京都簡報第 2 頁開了四個照片框，只有兩張照片，另外兩格印著「照片未取得」，
    /// 資料來源頁還多一段「未取得素材」。原因是提示叫模型「明示缺素材」，又沒說「照手上的照片排版」；
    /// 獨立審查也沒把它當問題。不捏造照片的原則不變——缺了什麼寫在講者備註，不印在投影片上。
    /// </summary>
    public class DeckPhotoPlaceholderTests
    {
        private static List<ClaudeDocumentSkillsService.ContainerFile> FourPhotos() => new()
        {
            new("f0", "cover.png", "封面用；主題：京都旅遊介紹"),
            new("f1", "photo-a.png", AgentRuntime.DescribeSlidePhoto("必訪景點", "Fushimi Inari torii gates")),
            new("f2", "photo-b.png", AgentRuntime.DescribeSlidePhoto("必訪景點", "Arashiyama bamboo grove")),
            new("f3", "photo-c.png", AgentRuntime.DescribeSlidePhoto("文化體驗", "tea ceremony still life")),
        };

        [Fact]
        public void PhotoBlock_SaysHowManyPhotosThereAre_AndNoEmptyFrames()
        {
            string block = AgentRuntime.PhotoBlock(FourPhotos());

            Assert.Contains("照片一共只有上面這 4 張", block);
            Assert.Contains(AgentRuntime.NoEmptyPhotoFrames, block);
            Assert.Contains("不要開照片框", block);
            Assert.Contains("講者備註", block);                // 缺什麼寫在備註，誠實不打折
            Assert.Contains("找不到相符的頁就不要放", block);   // 原本的擺放規則還在
        }

        [Fact]
        public void PhotoBlock_IsEmpty_WhenThereAreNoPhotos()
            => Assert.Equal("", AgentRuntime.PhotoBlock(new List<ClaudeDocumentSkillsService.ContainerFile>()));

        [Fact]
        public void DeckBrief_NoLongerAsksToStateMissingMaterialOnSlides()
        {
            string prompt = DocumentSkillsPrompt.BuildUserPrompt(new DocumentSkillsPrompt.Request
            {
                UserInput = "做一份 3 頁簡報介紹京都",
                MainContent = "京都是日本的古都。",
                WantsDeck = true,
            });

            Assert.DoesNotContain("明示缺素材", prompt);
            Assert.Contains(AgentRuntime.NoEmptyPhotoFrames, prompt);
            Assert.Contains("不捏造照片", prompt);             // 不捏造的原則不變
        }

        [Fact]
        public void DesignBrief_KeepsNoFabrication_ButNoPlaceholders()
        {
            string brief = PresentationDesignBrief.Build("");

            Assert.DoesNotContain("明示缺少素材", brief);
            Assert.Contains("不虛構作品內容", brief);
            Assert.Contains("講者備註", brief);
        }

        [Fact]
        public void Review_FlagsMakerNotesOnSlides_ButNotGeneratedImageDisclosure()
        {
            Assert.Contains("未取得", ReportReview.Instructions);
            Assert.Contains("給製作者看", ReportReview.Instructions);
            Assert.Contains("正當的揭露，不算問題", ReportReview.Instructions);
        }
    }
}
