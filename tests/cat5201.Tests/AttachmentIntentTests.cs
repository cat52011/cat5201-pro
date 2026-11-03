using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-03 機器學習作業：使用者只打「完成此作業並說明」，要交「HW1作業檔案」與「程式碼檔案」的要求
    /// 寫在附件裡。輸出判斷只看那一句，報告沒有產出。
    /// </summary>
    public class AttachmentIntentTests
    {
        // 那份作業實際的附件。
        private static IReadOnlyList<AttachmentIntentHints.Hint> HomeworkAttachments() =>
            AttachmentIntentHints.Build(
                new (string, string?)[]
                {
                    ("115Taipei.csv", "x/115Taipei.csv"),
                    ("HW1作業範本.docx", "x/HW1作業範本.docx"),
                    ("HW1欄位說明.docx", "x/HW1欄位說明.docx"),
                    ("螢幕擷取畫面 2026-11-03 154555.png", "x/shot.png"),
                },
                (path, name) => name switch
                {
                    "HW1作業範本.docx" => "資科系 機器學習HW1_Regression Analysis 程式碼語言: 內容: Part I: 1. 資料清理與視覺化圖表 " + new string('說', 2000),
                    "HW1欄位說明.docx" => "資料集欄位說明 鄉鎮市區 交易標的",
                    _ => "不該讀到這裡",   // CSV 與圖片不該被讀內容
                });

        [Fact]
        public void TemplateAttachment_PlusCompleteRequest_MeansAReport()
        {
            var intent = AttachmentIntentHints.ApplyTemplateRule(OutputIntent.None, "完成此作業並說明", HomeworkAttachments());

            Assert.True(intent.WantsReport);
            Assert.False(intent.WantsPresentation);
            Assert.False(intent.WantsTable);
        }

        [Theory]
        [InlineData("這份範本的格式有什麼問題？")]      // 只是在問範本，不是要你完成它
        [InlineData("說明一下欄位的意思")]
        public void TemplateAttachment_WithoutAskingToCompleteIt_ChangesNothing(string request)
            => Assert.False(AttachmentIntentHints.ApplyTemplateRule(OutputIntent.None, request, HomeworkAttachments()).WantsReport);

        [Fact]
        public void NoTemplate_ChangesNothing()
        {
            var hints = AttachmentIntentHints.Build(new (string, string?)[] { ("合約.docx", null), ("data.csv", null) });
            Assert.False(AttachmentIntentHints.ApplyTemplateRule(OutputIntent.None, "完成這份合約審閱", hints).WantsReport);
        }

        [Fact]
        public void TemplateType_DecidesTheOutputType()
        {
            var hints = AttachmentIntentHints.Build(new (string, string?)[]
            {
                ("期末報告簡報模板.pptx", null),
                ("Budget template.xlsx", null),
            });

            var intent = AttachmentIntentHints.ApplyTemplateRule(OutputIntent.None, "幫我依照範本完成", hints);

            Assert.True(intent.WantsPresentation);
            Assert.True(intent.WantsTable);
            Assert.False(intent.WantsReport);
        }

        [Fact]
        public void Hints_ReadDocumentsOnly_AndKeepThemShort()
        {
            var hints = HomeworkAttachments();

            Assert.Null(hints.Single(h => h.FileName == "115Taipei.csv").Excerpt);                     // 資料檔不讀
            Assert.Null(hints.Single(h => h.FileName.EndsWith(".png")).Excerpt);                       // 圖片不讀
            string template = hints.Single(h => h.FileName == "HW1作業範本.docx").Excerpt!;
            Assert.StartsWith("資科系 機器學習HW1", template);
            Assert.True(template.Length <= AttachmentIntentHints.MaxExcerptChars + 1);                  // 截短，不是整份
        }

        [Fact]
        public void JudgePrompt_ListsTheAttachments()
        {
            string prompt = OutputIntentResolver.BuildUserPrompt("完成此作業並說明", HomeworkAttachments());

            Assert.Contains("完成此作業並說明", prompt);
            Assert.Contains("- HW1作業範本.docx", prompt);
            Assert.Contains("115Taipei.csv（資料檔）", prompt);
            Assert.Contains("（圖片，內容未讀）", prompt);
            Assert.Contains("開頭內容：資科系 機器學習HW1", prompt);
            Assert.DoesNotContain("不該讀到這裡", prompt);
        }

        [Fact]
        public void NoAttachments_PromptIsUnchanged()
            => Assert.DoesNotContain("使用者附上的檔案", OutputIntentResolver.BuildUserPrompt("做一份簡報", null));
    }
}
