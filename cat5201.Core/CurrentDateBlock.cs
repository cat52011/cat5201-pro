using System;

namespace Cat5201
{
    /// <summary>
    /// 告訴模型「今天是哪一天」。API 呼叫的模型不知道現在的日期，ChatGPT 網頁版會在系統提示裡給，這裡也要給。
    ///
    /// 2026-11-06 實測：問「東京這週末會下雨嗎？」，搜尋拿回 10 月的舊文章和 11/5 的新預報混在一起，
    /// 模型不知道今天幾號，分不出「這週末」是哪兩天，只回「資料不足，請提供日期」。
    /// 週末日期由程式算好一起給，不讓模型自己推。
    /// </summary>
    public static class CurrentDateBlock
    {
        private static readonly string[] Weekdays = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

        /// <summary>這個週末：今天是週日就是昨天＋今天，其他日子是接下來的週六＋週日。搜尋查詢也用這一份算法。</summary>
        public static (DateTime Saturday, DateTime Sunday) ThisWeekend(DateTime today)
        {
            DateTime saturday = today.DayOfWeek == DayOfWeek.Sunday
                ? today.AddDays(-1)
                : today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);
            return (saturday, saturday.AddDays(1));
        }

        public static string Build(DateTimeOffset now)
        {
            DateTime today = now.Date;
            var (saturday, sunday) = ThisWeekend(today);

            TimeSpan offset = now.Offset;
            string zone = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm");

            return
                $"【目前日期】今天是 {today:yyyy-MM-dd}（{Weekdays[(int)today.DayOfWeek]}），時區 {zone}；" +
                $"這個週末是 {saturday:yyyy-MM-dd}（星期六）與 {sunday:yyyy-MM-dd}（星期日）。\n" +
                "使用者說的「今天」「明天」「這週末」「最近」這類相對時間，一律以上面的日期換算。" +
                "參考資料裡日期比這更早的內容，不要當成現在的情況。";
        }
    }
}
