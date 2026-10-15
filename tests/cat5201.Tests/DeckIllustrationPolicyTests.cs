using System;
using System.Collections.Generic;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-13 使用者的要求：「如果簡報內容需要圖片本來就該配圖，只要文字的簡報當然不用配圖，
    /// 你應該自己判斷而不是每次都要我設定。」
    ///
    /// 人工驗一次要跑一份完整簡報（好幾分鐘、好幾毛錢），而且只驗得到一種主題。
    /// 這裡用大綱直接餵判斷，一秒跑完十幾種。
    /// </summary>
    public class DeckIllustrationPolicyTests
    {
        private static PresentationOutlinePayload Outline(string title, params string[] headings)
            => new()
            {
                Title = title,
                Topic = "",
                Slides = BuildSlides(headings),
            };

        private static IReadOnlyList<PresentationSlidePayload> BuildSlides(string[] headings)
        {
            var list = new List<PresentationSlidePayload>();
            for (int i = 0; i < headings.Length; i++)
                list.Add(new PresentationSlidePayload { Order = i + 2, Heading = headings[i] });
            return list;
        }

        // ───────────────────────── 內容需要圖

        [Fact]
        public void 日本高檔餐飲的簡報要配圖()
        {
            // 這就是使用者實際做的那一份：請求裡一個圖片相關的字都沒有，
            // 但每一頁都在講擺盤、食材、店面——沒有圖的話這份簡報不成立。
            var outline = Outline(
                "日本高檔餐飲的虛以藝術",
                "從季節出發的食材哲學", "懷石料理的擺盤語言", "代表主廚與他們的餐廳", "台灣的在地實踐");

            bool yes = DeckIllustrationPolicy.ShouldIllustrate(
                "介紹日本高檔餐飲的虛以藝術，包含歷史脈絡、代表主廚、擺盤哲學、台灣的在地實踐。",
                outline, explicitFromResolver: false, out var reason);

            Assert.True(yes);
            Assert.Equal(DeckIllustrationPolicy.Reason.VisualSubject, reason);
        }

        [Theory]
        [InlineData("京都三日自由行", "清水寺周邊散步", "嵐山的竹林與季節風景", "住宿與街區選擇")]
        [InlineData("2026 春夏服裝趨勢", "色彩與材質", "品牌視覺與包裝", "伸展台的版型語言")]
        [InlineData("北歐室內設計入門", "空間與採光", "家具材質與工藝", "配色原則")]
        public void 看得見的主題都要配圖(string title, string a, string b, string c)
        {
            Assert.True(DeckIllustrationPolicy.ShouldIllustrate(
                title, Outline(title, a, b, c), explicitFromResolver: false, out _));
        }

        // ───────────────────────── 內容不需要圖

        [Fact]
        public void 財報分析的簡報不配圖()
        {
            var outline = Outline(
                "台積電最近一季財報分析",
                "營收與毛利率", "EPS 與現金流", "資本支出與指引", "估值與風險");

            bool yes = DeckIllustrationPolicy.ShouldIllustrate(
                "分析台積電最近一季財報。", outline, explicitFromResolver: false, out var reason);

            Assert.False(yes);
            Assert.Equal(DeckIllustrationPolicy.Reason.TextualSubject, reason);
        }

        [Theory]
        [InlineData("系統架構說明", "API 與資料庫", "部署流程", "資安與權限")]
        [InlineData("個資法規遵循重點", "法規與條款", "稽核流程", "合約修訂")]
        public void 看不見的主題不配圖(string title, string a, string b, string c)
        {
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                title, Outline(title, a, b, c), explicitFromResolver: false, out _));
        }

        // ───────────────────────── 使用者講了就照辦

        [Fact]
        public void 使用者說不要圖時連視覺主題也不配()
        {
            var outline = Outline("京都美食地圖", "拉麵與街區", "甜點與咖啡");

            bool yes = DeckIllustrationPolicy.ShouldIllustrate(
                "做一份京都美食簡報，不要圖片。", outline, explicitFromResolver: false, out var reason);

            Assert.False(yes);
            Assert.Equal(DeckIllustrationPolicy.Reason.UserDeclined, reason);
        }

        [Fact]
        public void 使用者說不要圖的優先權高於語意判斷()
        {
            // 第一層判斷說「要圖」也擋得住：使用者的明確指示永遠最大。
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                "純文字就好", Outline("京都美食地圖", "拉麵與街區"),
                explicitFromResolver: true, out var reason));
            Assert.Equal(DeckIllustrationPolicy.Reason.UserDeclined, reason);
        }

        [Fact]
        public void 使用者說要圖時連數字主題也要配()
        {
            var outline = Outline("營收與毛利分析", "EPS 與現金流", "估值");

            bool yes = DeckIllustrationPolicy.ShouldIllustrate(
                "做一份財報簡報，附配圖。", outline, explicitFromResolver: false, out var reason);

            Assert.True(yes);
            Assert.Equal(DeckIllustrationPolicy.Reason.UserAsked, reason);
        }

        [Fact]
        public void 語意判斷說使用者要圖也算明講()
        {
            // 關鍵字表只認得繁中講法；英文、日文的「要圖」交給第一層語意判斷。
            Assert.True(DeckIllustrationPolicy.ShouldIllustrate(
                "please add illustrations", Outline("quarterly numbers", "EPS 與現金流"),
                explicitFromResolver: true, out var reason));
            Assert.Equal(DeckIllustrationPolicy.Reason.UserAsked, reason);
        }

        // ───────────────────────── 邊界

        [Fact]
        public void 沒有大綱時退回用內容文字判斷()
        {
            // 文件技能那條路沒有大綱，只有整合好的主要內容。
            Assert.True(DeckIllustrationPolicy.ShouldIllustrate(
                "做一份簡報", null, false, out _,
                contentText: "本份介紹日本懷石料理的擺盤與食材，以及代表主廚的餐廳空間設計。"));

            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                "做一份簡報", null, false, out _,
                contentText: "本份整理營收、毛利率、EPS 與現金流的季度變化，並比較同業估值。"));
        }

        [Fact]
        public void 看不出來的時候不自己花錢生圖()
        {
            // 生成圖是最貴也最慢的一步。兩邊都沒訊號時不要賭。
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                "做一份簡報", Outline("本週進度", "已完成", "進行中", "下週計畫"),
                explicitFromResolver: false, out var reason));
            Assert.Equal(DeckIllustrationPolicy.Reason.TextualSubject, reason);
        }

        [Fact]
        public void 空的輸入不會爆掉()
        {
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(null, null, false, out _));
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate("", Outline(""), false, out _));
        }
    }
}
