using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 檔案 builders 是使用者的「主要交付物」（PDF/Word/簡報/Excel），此前零測試（Codex #14）。
    /// 這裡做結構驗證：產出的 bytes 要能被對應格式的正式解析器打開、關鍵內容要在——
    /// 防止套件升級或重構後產出「存得下來但打不開」的壞檔。
    /// </summary>
    public class FileBuildersTests
    {
        private const string SampleMarkdown =
            "# 台積電投資摘要\n\n## 關鍵資料\n- 營收：**NT$1,134.10B**\n- 毛利率：66.2%\n\n## 結論\n基本面強勁。";

        [Fact]
        public void Docx_OpensAndContainsHeading()
        {
            byte[] bytes = DocxReportBuilder.Build(SampleMarkdown);
            Assert.True(bytes.Length > 0);

            using var ms = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(ms, false);   // 打不開＝壞檔，直接炸
            string text = doc.MainDocumentPart!.Document.Body!.InnerText;
            Assert.Contains("台積電投資摘要", text);
            Assert.Contains("66.2%", text);
        }

        [Fact]
        public void Pdf_OpensAndContainsHeading()
        {
            byte[] bytes = PdfReportBuilder.Build(SampleMarkdown);
            Assert.True(bytes.Length > 0);

            using var pdf = PdfDocument.Open(bytes);
            Assert.True(pdf.NumberOfPages >= 1);
            string allText = string.Concat(pdf.GetPages().Select(p => p.Text));
            Assert.Contains("台積電投資摘要", allText);
        }

        [Fact]
        public void Xlsx_OpensWithWorksheet()
        {
            byte[] bytes = XlsxReportBuilder.Build("項目,數值\n營收,1134\n毛利率,66.2");
            Assert.True(bytes.Length > 0);

            using var ms = new MemoryStream(bytes);
            using var doc = SpreadsheetDocument.Open(ms, false);
            Assert.NotNull(doc.WorkbookPart);
            Assert.NotEmpty(doc.WorkbookPart!.WorksheetParts);
        }

        private static PresentationOutlinePayload SampleOutline() => new()
        {
            Title = "測試簡報",
            Topic = "單元測試",
            SlideCount = 3,
            Slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "測試簡報" },
                new() { Order = 2, Kind = "content", Heading = "第一頁重點", Bullets = new[] { "重點一", "重點二" } },
                new() { Order = 3, Kind = "content", Heading = "第二頁重點", Bullets = new[] { "重點三" } },
            },
        };

        [Fact]
        public void Pptx_OpensWithExpectedSlideCount()
        {
            byte[] bytes = PptxBuilder.Build(SampleOutline());
            Assert.True(bytes.Length > 0);

            using var ms = new MemoryStream(bytes);
            using var doc = PresentationDocument.Open(ms, false);
            int slideCount = doc.PresentationPart!.SlideParts.Count();
            Assert.Equal(3, slideCount);
        }

        [Fact]
        public void DeckPdf_OpensWithPages()
        {
            byte[] bytes = DeckPdfBuilder.Build(SampleOutline(), coverImagePng: null);
            Assert.True(bytes.Length > 0);

            using var pdf = PdfDocument.Open(bytes);
            Assert.True(pdf.NumberOfPages >= 3);   // 一張投影片一頁
        }
    }
}
