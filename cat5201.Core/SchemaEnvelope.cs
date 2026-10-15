using System;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 落地檔案的版本信封。
    ///
    /// 為什麼需要：2026-10 出過一次事——簡報內嵌資料的指紋換了演算法，但檔案裡沒有任何
    /// 版本標記，於是硬碟上既有的簡報全部被判定成「使用者手動改過」而失去單頁編輯。
    /// 格式變更本身沒錯，錯在沒有辦法分辨「這個檔是哪一版寫的」。
    ///
    /// 用法：寫入時包一層 <c>{"schema":N,"data":…}</c>；讀取時回傳版本與內容，
    /// 讀到舊的裸格式（沒有信封）就回 <see cref="LegacySchema"/>，呼叫端據此分流。
    /// 既有檔案不需要遷移，也不會因為這個改動而失效——這正是當初缺的那一半。
    /// </summary>
    public static class SchemaEnvelope
    {
        /// <summary>沒有信封的舊檔。</summary>
        public const int LegacySchema = 0;

        private const string SchemaProperty = "schema";
        private const string DataProperty = "data";

        public static string Write<T>(T payload, int schema, bool indented = true)
            => JsonSerializer.Serialize(
                new Envelope<T>(schema, payload),
                new JsonSerializerOptions { WriteIndented = indented });

        /// <summary>
        /// 讀出版本與內容。沒有信封的舊檔回 (LegacySchema, 整份當 T 解析)；
        /// 整份解析不出來回 (LegacySchema, default)，呼叫端自行決定要不要當成空的。
        /// </summary>
        public static (int Schema, T? Data) Read<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (LegacySchema, default);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(SchemaProperty, out var schemaEl) &&
                    schemaEl.ValueKind == JsonValueKind.Number &&
                    root.TryGetProperty(DataProperty, out var dataEl))
                {
                    return (schemaEl.GetInt32(), JsonSerializer.Deserialize<T>(dataEl.GetRawText()));
                }

                // 舊檔：整份就是內容本身。
                return (LegacySchema, JsonSerializer.Deserialize<T>(json));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Schema", "讀取落地檔案失敗（當成空的處理）", ex);
                return (LegacySchema, default);
            }
        }

        private sealed record Envelope<T>(int schema, T data);
    }
}
