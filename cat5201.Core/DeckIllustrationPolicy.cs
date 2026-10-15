using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 這份簡報要不要配圖。
    ///
    /// 以前的規則是「使用者沒特別提就不配圖」，於是一份介紹日本高檔餐飲的簡報做出來是 10 頁純文字，
    /// 而使用者得每次都記得在提示裡加「附配圖」。那不是設定問題，是判斷問題：
    /// **內容需要圖就該配圖，只有文字的簡報當然不用。**
    ///
    /// 判斷順序：
    ///   1. 使用者明講不要 → 不配（任何情況都尊重）
    ///   2. 使用者明講要 → 配
    ///   3. 都沒講 → 看這份簡報**實際寫了什麼**（標題 + 每頁標題 + 條列），不是看請求那一行字
    ///
    /// 為什麼看內容而不是看請求：「介紹日本高檔餐飲」裡沒有任何一個圖片相關的字，
    /// 但做出來的每一頁都在講擺盤、食材、店面——這種簡報沒有圖就不成立。
    /// 反過來「分析台積電最近一季財報」的每一頁都是數字與走勢，配生成圖只會顯得廉價。
    ///
    /// 放在 Core 而不是埋在 AgentRuntime：這是可以、也應該被釘住的領域規則。
    /// 人工驗一次要花好幾分鐘跑一份簡報，而且只驗得到一種主題。
    /// </summary>
    public static class DeckIllustrationPolicy
    {
        public enum Reason
        {
            /// <summary>使用者明說不要圖。</summary>
            UserDeclined,

            /// <summary>使用者明說要圖。</summary>
            UserAsked,

            /// <summary>內容是看得見的東西（食物、地方、作品、產品…），需要圖。</summary>
            VisualSubject,

            /// <summary>內容是數字、流程、條文這類看不出畫面的東西，不配圖。</summary>
            TextualSubject,
        }

        /// <summary>看得見的主題：有圖才講得清楚，或沒圖就少了一半。</summary>
        private static readonly string[] VisualTerms =
        {
            // 飲食
            "餐飲", "美食", "料理", "菜單", "擺盤", "食材", "甜點", "烘焙", "咖啡", "茶", "酒",
            "餐廳", "主廚", "廚師", "食譜", "小吃", "宴席", "懷石", "壽司",
            // 地方與旅遊
            "旅遊", "觀光", "景點", "城市", "街區", "建築", "風景", "地景", "民宿", "飯店",
            "古蹟", "廟", "公園", "海岸", "山", "季節", "櫻花", "楓",
            // 創作與美感
            "藝術", "設計", "展覽", "攝影", "繪畫", "雕塑", "工藝", "美學", "時尚", "服裝",
            "品牌", "包裝", "視覺", "風格", "色彩", "空間", "室內",
            // 物與人
            "產品", "外觀", "質感", "材質", "手作", "職人", "人物", "角色",
            // 文化與自然
            "文化", "傳統", "節慶", "祭典", "歷史場景", "自然", "生態", "動物", "植物", "花",
            // 活動
            "運動", "賽事", "表演", "演出", "音樂會",
        };

        /// <summary>看不見的主題：配生成圖只會變成無意義的裝飾。</summary>
        private static readonly string[] TextualTerms =
        {
            // 財務
            "財報", "營收", "毛利", "淨利", "eps", "財務", "估值", "股價", "損益", "現金流",
            "預算", "成本結構", "投資報酬",
            // 技術
            "架構", "系統", "api", "規格", "演算法", "資料庫", "部署", "效能", "協定", "原始碼",
            "流程圖", "工作流", "權限", "資安",
            // 制度
            "法規", "合約", "條款", "政策", "合規", "稽核", "專利", "智慧財產",
            // 數據分析
            "統計", "指標", "kpi", "成長率", "市占", "市場規模", "問卷", "樣本", "迴歸",
            "比較表", "試算", "敏感度",
        };

        /// <summary>使用者明講不要的說法。</summary>
        private const string DeclinePattern =
            @"(不要|不用|不需要|無需|別|勿)[^。，,\n]{0,6}(圖片|照片|插圖|插畫|配圖|附圖|圖)";

        /// <summary>使用者明講要的說法。</summary>
        private const string AskPattern =
            @"圖片|照片|插畫|插圖|配圖|附圖|示意圖|圖像|圖文並茂|視覺素材";

        /// <summary>
        /// 這份簡報要不要配圖。
        /// </summary>
        /// <param name="userInput">使用者的原始請求（只用來看他有沒有明講）。</param>
        /// <param name="outline">已經寫好的大綱——判斷內容需不需要圖，看的是這個。</param>
        /// <param name="explicitFromResolver">
        /// 第一層語意判斷認為使用者明確要求配圖（模型判斷，認得各種語言的講法）。
        /// 只當成「使用者講了要」的訊號；它不會因為沒講就代表不要。
        /// </param>
        /// <param name="contentText">
        /// 沒有大綱時用來判斷的內容（報告正文、整合後的主要內容）。
        /// 報告跟簡報是同一個道理：講的是看得見的東西就該有圖，講的是數字與條文就不該有。
        /// </param>
        public static bool ShouldIllustrate(
            string? userInput,
            PresentationOutlinePayload? outline,
            bool explicitFromResolver,
            out Reason reason,
            string? contentText = null)
        {
            string request = userInput ?? "";

            if (System.Text.RegularExpressions.Regex.IsMatch(request, DeclinePattern)
                || request.Contains("純文字", StringComparison.Ordinal)
                || request.Contains("no image", StringComparison.OrdinalIgnoreCase)
                || request.Contains("text only", StringComparison.OrdinalIgnoreCase))
            {
                reason = Reason.UserDeclined;
                return false;
            }

            if (explicitFromResolver
                || System.Text.RegularExpressions.Regex.IsMatch(request, AskPattern)
                || System.Text.RegularExpressions.Regex.IsMatch(
                       request, @"[一二三四五六七八九十兩幾些\d]+\s*[張幅]\s*圖(?!表)"))
            {
                reason = Reason.UserAsked;
                return true;
            }

            bool visual = ContentIsVisual(outline, string.IsNullOrWhiteSpace(contentText) ? request : contentText);
            reason = visual ? Reason.VisualSubject : Reason.TextualSubject;
            return visual;
        }

        /// <summary>這份簡報講的是看得見的東西嗎。</summary>
        public static bool ContentIsVisual(PresentationOutlinePayload? outline, string? fallbackText = null)
        {
            string text = Flatten(outline);
            if (text.Length == 0)
                text = (fallbackText ?? "").ToLowerInvariant();

            if (text.Length == 0)
                return false;

            int visual = Score(text, VisualTerms);
            int textual = Score(text, TextualTerms);

            // 平手或兩邊都沒訊號 → 不配圖。
            // 生成圖是這個產品最貴也最慢的一步，「看不出來需不需要」時不該自己花這個錢。
            return visual > textual;
        }

        /// <summary>大綱攤平成一段小寫文字：標題、主題、每頁標題與條列。</summary>
        private static string Flatten(PresentationOutlinePayload? outline)
        {
            if (outline == null)
                return "";

            var sb = new StringBuilder();
            sb.Append(outline.Title).Append(' ').Append(outline.Topic).Append(' ');

            foreach (var slide in outline.Slides ?? Array.Empty<PresentationSlidePayload>())
            {
                if (slide == null) continue;
                sb.Append(slide.Heading).Append(' ');
                foreach (var b in slide.Bullets ?? Array.Empty<string>())
                    sb.Append(b).Append(' ');
            }

            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>命中幾個不同的詞（同一個詞出現十次仍只算一次，避免一個字洗版）。</summary>
        private static int Score(string text, IReadOnlyList<string> terms)
            => terms.Count(t => text.Contains(t, StringComparison.Ordinal));
    }
}
