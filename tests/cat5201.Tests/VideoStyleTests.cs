using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-09-17 影片原廠風格改版（舊膠片 → 大畫幅史詩電影）的回歸釘：
    /// 舊膠片字眼不能再從任何「送給模型」的地方漏出去，片名/人名也不能進 prompt（會污染內容）。
    /// </summary>
    public class VideoStyleTests
    {
        [Theory]
        [InlineData("grain")]
        [InlineData("16mm")]
        [InlineData("scratch")]
        [InlineData("halation")]
        [InlineData("haze")]
        [InlineData("mist")]
        [InlineData("archival")]
        [InlineData("low contrast")]
        public void VeoTags_HaveNoOldFilmLook(string oldWord)
            => Assert.DoesNotContain(oldWord, VideoStyle.DefaultVeoRenderTags, System.StringComparison.OrdinalIgnoreCase);

        [Theory]
        [InlineData("膠片劣化")]
        [InlineData("劣化膠片")]
        [InlineData("Donkey Skin")]
        [InlineData("Voku")]
        [InlineData("soft fine film grain")]   // 舊版開頭關鍵字清單（NOT 清單裡的 film grain 是正確的反向指示，不算）
        public void DirectorPrompt_HasNoOldFilmDirection(string oldText)
        {
            string prompt = VideoPlanBuilder.BuildDirectorPrompt("一隻貓在窗邊", 8, VideoStyle.DefaultCinematicPrompt);
            Assert.DoesNotContain(oldText, prompt);
        }

        [Theory]
        [InlineData("Odyssey")]
        [InlineData("Nolan")]
        [InlineData("奧德賽")]
        public void ModelFacingText_NeverNamesTheReferenceFilm(string name)
        {
            string prompt = VideoPlanBuilder.BuildDirectorPrompt("城市夜景", 8, VideoStyle.DefaultCinematicPrompt);
            Assert.DoesNotContain(name, prompt, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, VideoStyle.DefaultVeoRenderTags, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RenderTags_DefaultStyle_UsesShortTags_CustomStyle_PassesThrough()
        {
            Assert.Equal(VideoStyle.DefaultVeoRenderTags, VideoStyle.RenderTagsFor(VideoStyle.Resolve("")));
            Assert.Equal("my own look", VideoStyle.RenderTagsFor(VideoStyle.Resolve("  my own look  ")));
        }

        [Fact]
        public void JournalSummary_SkipsStyleHead_ShowsSceneContent()
        {
            string prompt = VideoStyle.DefaultVeoRenderTags + "\n\nA white cat sits by a rain-streaked window. In the final 2-3 seconds...";
            Assert.Equal("A white cat sits by a rain-streaked wind…", VideoJobJournal.Summarize(prompt));
        }

        [Fact]
        public void JournalSummary_KeepsExtendMarker_AndHandlesEmpty()
        {
            Assert.Equal("(影片延伸) Waves crash", VideoJobJournal.Summarize("(影片延伸) style tags\r\n\r\nWaves crash"));
            Assert.Equal("", VideoJobJournal.Summarize(null));
        }
    }
}
