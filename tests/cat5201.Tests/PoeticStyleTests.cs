using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-06：
    /// 1. 簡報風格（藝術方向）改由第一層語意判斷決定。以前用關鍵字比對使用者那句話，
    ///    「做一份 3 頁簡報介紹京都」一個關鍵字都沒中，被套上商業科技藍。
    /// 2. 使用者偏好藝術類的簡報與圖片往「復古雜誌」的美感靠（PoeticPrintStyle），寫實類不受影響。
    ///    使用者要的是美術風格，不是一份照做的規格，也不要寫死——只描述美感，構圖交給模型。
    /// 3. 照片依主體挑（最多 6 張），封面不重複內容頁已畫的主體——清水寺那格沒照片、封面和內容頁都是鳥居。
    /// </summary>
    public class PoeticStyleTests
    {
        // ===== 語意判斷 → 藝術方向 =====

        [Theory]
        [InlineData("\"art_direction\":\"poetic\"", "poetic")]
        [InlineData("\"art_direction\":\"POETIC\"", "poetic")]
        [InlineData("\"art_direction\":\"editorial\"", "editorial")]
        [InlineData("\"art_direction\":\"watercolor\"", "")]   // 自創的代號不收
        [InlineData("\"art_direction\":\"\"", "")]
        public void Parser_ReadsOnlyKnownArtDirections(string field, string expected)
        {
            var intent = OutputIntentResolver.TryParse("{\"presentation\":true," + field + "}");
            Assert.Equal(expected, intent!.ArtDirection);
        }

        [Fact]
        public void Parser_MissingArtDirection_IsEmpty()
            => Assert.Equal("", OutputIntentResolver.TryParse("{\"presentation\":true}")!.ArtDirection);

        [Fact]
        public void Prompt_AsksForArtDirection_WithArtisticSubjectsGoingPoetic()
        {
            string prompt = typeof(OutputIntentResolver)
                .GetMethod("BuildSystemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null) as string ?? "";

            Assert.Contains("\"art_direction\"", prompt);
            Assert.Contains("poetic＝藝術類（復古雜誌風）", prompt);
            Assert.Contains("「做一份 3 頁簡報介紹京都」 → presentation=true、art_direction=\"poetic\"", prompt);
            Assert.Contains("寫實用途的圖片", prompt);   // 寫實類不受影響
        }

        [Fact]
        public void Resolve_SemanticWins_ThenKeywordsOnlyInRuleAuto_ElseModern()
        {
            var poetic = new OutputIntent { ArtDirection = "poetic" };

            // 語意判斷有結果：不管模式、不管字面。
            Assert.Equal("poetic", AgentRuntime.ResolveArtDirectionId(poetic, allowKeywordFallback: false, "季度財報分析"));
            // 沒有判斷：規則式 Auto 照舊看關鍵字……
            Assert.Equal("nature", AgentRuntime.ResolveArtDirectionId(null, allowKeywordFallback: true, "冰島極光旅遊"));
            // ……其他模式不用關鍵字。
            Assert.Equal("modern", AgentRuntime.ResolveArtDirectionId(null, allowKeywordFallback: false, "冰島極光旅遊"));
        }

        [Fact]
        public void Outline_UsesTheJudgedDirection_NotTheTitleKeywords()
        {
            var outline = new PresentationOutlinePayload { Title = "季度財報分析", Topic = "營收" };
            Assert.Equal("finance", DeckArtDirection.ForOutline(outline).Id);   // 舊專案（沒有這欄）照舊

            outline.ArtDirectionId = "poetic";
            Assert.Equal("poetic", DeckArtDirection.ForOutline(outline).Id);
        }

        [Fact]
        public void ClaudeDeckBrief_UsesTheJudgedDirection()
        {
            string prompt = DocumentSkillsPrompt.BuildUserPrompt(new DocumentSkillsPrompt.Request
            {
                UserInput = "做一份 3 頁簡報介紹京都",
                MainContent = "京都是日本的古都。",
                WantsDeck = true,
                ArtDirectionId = "poetic",
            });

            Assert.Contains("Vintage magazine", prompt);
            Assert.DoesNotContain("#1F5AE0", prompt);   // 不再是商業科技藍
        }

        // ===== 復古雜誌美感：只描述風格，不寫死版型 =====

        [Fact]
        public void PoeticDirection_IsAMagazineStyle_WithWarmPaperAndSerif()
        {
            Assert.Contains("poetic", DeckArtDirection.AllIds);
            var art = DeckArtDirection.ById("poetic");
            Assert.Equal("復古雜誌", art.DisplayName);
            Assert.Equal("editorial", art.Family);           // 雜誌家族
            Assert.Equal("F3EEE4", art.Background);
            Assert.Equal("Georgia", art.DisplayLatinFont);
            Assert.Contains("never default to blue", art.DirectionForModel);
            Assert.Contains("not a template", art.DirectionForModel);
            Assert.Equal(PoeticPrintStyle.DeckPhotoStyle, IllustrationStyle.StyleFor(art));
        }

        [Fact]
        public void DeckPhotos_StayCleanAndTrueToLife()
        {
            // 2026-11-06：清水寺那張整張蓋滿網點、還多一圈白紙邊，細節都糊了。
            Assert.Contains("true-to-life", PoeticPrintStyle.DeckPhotoStyle);
            Assert.Contains("No visible grain, no halftone dots", PoeticPrintStyle.DeckPhotoStyle);
            Assert.Contains("no paper border", PoeticPrintStyle.DeckPhotoStyle);
            Assert.Contains("no added grain, dots or borders", PoeticPrintStyle.DeckDirection);
        }

        [Fact]
        public void ArtisticImages_KeepTextureFaint_NeverObscuringDetail()
        {
            Assert.Contains("never obscures detail", PoeticPrintStyle.ImageStyle);
            Assert.Contains("no visible dot screen", PoeticPrintStyle.ImageStyle);
            Assert.Contains("no borders or frames", PoeticPrintStyle.ImageStyle);
        }

        [Theory]
        [InlineData("ORIGINAL")]        // 不再規定放原圖縮圖
        [InlineData("3:4")]             // 不規定比例
        [InlineData("poem")]            // 不規定要寫詩句
        [InlineData("UPPERCASE")]       // 不規定一定要一個大寫英文字
        public void ImageStyle_DescribesTheLook_WithoutFixedStructure(string fixedRule)
        {
            Assert.DoesNotContain(fixedRule, PoeticPrintStyle.ImageStyle);
            Assert.DoesNotContain(fixedRule, PoeticPrintStyle.DeckDirection);
        }

        [Fact]
        public void ImageStyle_LeavesCompositionToTheModel()
        {
            Assert.Contains("not a fixed layout", PoeticPrintStyle.ImageStyle);
            Assert.Contains("Choose whatever composition suits the subject", PoeticPrintStyle.ImageStyle);
            Assert.Contains("never a default blue", PoeticPrintStyle.ImageStyle);
        }

        [Fact]
        public void IsPoetic_OnlyFollowsTheIntent()
        {
            Assert.True(AgentRuntime.IsPoetic(new OutputIntent { ArtDirection = "poetic" }));
            Assert.False(AgentRuntime.IsPoetic(new OutputIntent { ArtDirection = "modern" }));
            Assert.False(AgentRuntime.IsPoetic(null));
        }

        // ===== 照片依主體挑、封面不重複 =====

        private static PresentationOutlinePayload KyotoOutline() => new()
        {
            Title = "京都簡介",
            Slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "京都" },
                new() { Order = 2, Kind = "content", Heading = "必訪景點",
                    Bullets = new[] { "清水寺：建於山坡之上", "伏見稻荷大社：千本鳥居", "金閣寺：倒映池中", "嵐山：竹林與渡月橋" } },
                new() { Order = 3, Kind = "content", Heading = "觀光數據",
                    Bullets = new[] { "觀光收入 426 億元", "年增 8.4%，旅客 5,000 萬人次" } },
            },
        };

        [Fact]
        public void PhotoSubjects_AreOnePerItem_NotOnePerSlide()
        {
            var subjects = AgentRuntime.BuildPhotoSubjectSections(KyotoOutline());

            Assert.Equal(new[] { 201, 202, 203, 204 }, subjects.Select(s => s.Id));   // 四個景點各一個候選
            Assert.Contains(subjects, s => s.Body.StartsWith("清水寺"));
            Assert.DoesNotContain(subjects, s => s.Title == "觀光數據");                  // 數據頁不配圖
            Assert.StartsWith("必訪景點／清水寺", subjects[0].Label);
        }

        [Fact]
        public void PhotoLimit_AndPlanRule_CoverEveryNamedItem()
        {
            Assert.Equal(6, AgentRuntime.MaxDeckPhotos);
            Assert.Contains("每一個都有自己的一張照片", AgentRuntime.DeckPhotoPlanRule);
            Assert.Contains("不要兩張畫同一個主體", AgentRuntime.DeckPhotoPlanRule);
        }

        [Fact]
        public void CoverBrief_AvoidsSubjectsAlreadyPhotographed()
        {
            string material = AgentRuntime.BuildCoverBriefMaterial(KyotoOutline(),
                new[] { "vermilion torii gates on a forest path" });

            Assert.Contains("封面不要再畫同一個主體", material);
            Assert.Contains("vermilion torii gates", material);
            Assert.DoesNotContain("封面不要再畫", AgentRuntime.BuildCoverBriefMaterial(KyotoOutline(), null));
        }
    }
}
