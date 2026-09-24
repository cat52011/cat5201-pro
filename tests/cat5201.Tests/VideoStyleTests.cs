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
        // 淡淡顆粒與薄霧是使用者要的（09-17 重測後加回）；舊膠片的「劣化」字眼才是禁區。
        [Theory]
        [InlineData("16mm")]
        [InlineData("scratch")]
        [InlineData("halation")]
        [InlineData("archival")]
        [InlineData("deteriorat")]
        [InlineData("gate weave")]
        [InlineData("low contrast")]
        public void VeoTags_HaveNoOldFilmLook(string oldWord)
            => Assert.DoesNotContain(oldWord, VideoStyle.DefaultVeoRenderTags, System.StringComparison.OrdinalIgnoreCase);

        [Fact]
        public void VeoTags_KeepSubtleGrainAndMist()
        {
            Assert.Contains("subtle fine organic grain", VideoStyle.DefaultVeoRenderTags);
            Assert.Contains("haze and light mist", VideoStyle.DefaultVeoRenderTags);
        }

        // 實測：「70mm IMAX」被 Veo 畫成畫面上的「70X」「IMAM」假浮水印。
        [Theory]
        [InlineData("IMAX")]
        [InlineData("70mm")]
        [InlineData("large-format")]
        [InlineData("widescreen")]
        [InlineData("no text")]
        public void VeoTags_HaveNoTextTriggeringWords(string word)
            => Assert.DoesNotContain(word, VideoStyle.DefaultVeoRenderTags, System.StringComparison.OrdinalIgnoreCase);

        [Fact]
        public void Sanitize_StripsFormatJargonAndNegatedOverlayList()
        {
            string raw = "Epic large-format 70mm IMAX cinematography, pristine crisp image. A lake at dawn. " +
                         "No text, no captions, no subtitles, no watermark, no logo, no distorted faces.";
            string clean = VideoStyle.SanitizeForVideoModel(raw);

            foreach (var bad in new[] { "IMAX", "70mm", "large-format", "No text", "captions", "watermark", "logo" })
                Assert.DoesNotContain(bad, clean, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("A lake at dawn.", clean);
            Assert.Contains("no distorted faces", clean);        // 非文字類的描述不誤刪
            Assert.DoesNotContain(" ,", clean);
            Assert.DoesNotContain(",,", clean);
        }

        [Fact]
        public void Sanitize_LeavesNormalLensTalkAlone()
        {
            // 焦段（35mm lens）是鏡頭語言不是片幅，不能被誤刪。
            Assert.Equal("Shot with a 35mm lens, slow push-in.", VideoStyle.SanitizeForVideoModel("Shot with a 35mm lens, slow push-in."));
        }

        [Theory]
        [InlineData("9:16", 9, 16)]
        [InlineData("16:9", 16, 9)]
        public void HeroStillSize_ExactlyMatchesVideoAspect_AndIsValidForImageModel(string aspect, int rw, int rh)
        {
            // 比例不精確 → Veo 補黑邊 → 黑邊上長出假字幕（實測 1024x1536 = 2:3 出事）。
            var parts = VideoPlanBuilder.HeroStillSizeFor(aspect).Split('x');
            int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
            Assert.Equal(w * rh, h * rw);                 // 精確比例
            Assert.Equal(0, w % 16);                      // gpt-image-2 自訂尺寸限制
            Assert.Equal(0, h % 16);
            Assert.True(w * h >= 655_360);
            Assert.True(ModelCostEstimator.ImageCostUsd($"{w}x{h}", "high") >= 0.25); // 有價目，不掉到較便宜的預設
        }

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
