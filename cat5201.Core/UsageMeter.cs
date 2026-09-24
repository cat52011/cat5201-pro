using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Cat5201
{
    /// <summary>
    /// 一次「會花錢的呼叫」的帳目（成本可稽查的最小單位）。
    /// LLM：模型 × 輸入/輸出 token；媒體/搜尋：數量 × 單位價。節點的總成本＝這些帳目的加總，
    /// 不再是「整個節點的 token 總數 × 最後那個模型的單價」（混用模型時會失真）。
    /// </summary>
    public sealed class UsageRecord
    {
        public DateTime AtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>llm / image / video / search。</summary>
        public string Kind { get; init; } = UsageKinds.Llm;

        /// <summary>計價用的模型 ID（已從服務端 ID 正規化，例：sonar → pplx-sonar）。</summary>
        public string ModelId { get; init; } = "";

        /// <summary>這次呼叫在做什麼（主回覆 / 輸出意圖判斷 / 影片導演企劃…）。</summary>
        public string Purpose { get; init; } = "";

        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }

        /// <summary>true＝API 回報的真實用量；false＝依字數或尺寸估算。</summary>
        public bool IsActual { get; init; } = true;

        /// <summary>媒體/搜尋的數量（張、秒、次）；LLM 為 0。</summary>
        public double Quantity { get; init; }
        public string Unit { get; init; } = "";

        /// <summary>美元成本。負數＝沖銷（例如影片任務失敗，官方不收費）。</summary>
        public double UsdCost { get; init; }

        public string Note { get; init; } = "";
    }

    public static class UsageKinds
    {
        public const string Llm = "llm";
        public const string Image = "image";
        public const string Video = "video";
        public const string Search = "search";
    }

    /// <summary>一次節點執行收集帳目的容器。多條 async 流可能同時寫入，所以加鎖。</summary>
    public sealed class UsageScope
    {
        private readonly object _lock = new();
        private readonly List<UsageRecord> _records = new();

        public void Add(UsageRecord record)
        {
            if (record == null) return;
            lock (_lock) _records.Add(record);
        }

        public IReadOnlyList<UsageRecord> Snapshot()
        {
            lock (_lock) return _records.ToList();
        }
    }

    /// <summary>
    /// 全 App 的計費入口：每個 AI 服務在「呼叫完成的當下」回報一筆帳目，
    /// 這裡同時 ① 寫進每日花費帳本（SpendLedger）② 掛到目前節點的 UsageScope。
    ///
    /// 為什麼在服務層記：輔助呼叫（輸出意圖判斷、模型自動選擇、自動命名、翻譯、導演企劃…）
    /// 不經過節點主流程，舊版只在主流程累加 token → 這些呼叫的錢從來沒進帳本。
    /// 節點歸屬靠 AsyncLocal：節點開始執行時 BeginScope()，同一條 async 流裡的所有呼叫自動掛上去，
    /// 並行的其他節點各自有自己的 scope（與 NodeService 的 P0-5 修法同一個機制）。
    /// </summary>
    public static class UsageMeter
    {
        private static readonly AsyncLocal<UsageScope?> _scope = new();
        private static readonly AsyncLocal<string?> _purpose = new();

        public const string DefaultPurpose = "AI 呼叫";

        public static UsageScope? CurrentScope => _scope.Value;

        public static string CurrentPurpose =>
            string.IsNullOrWhiteSpace(_purpose.Value) ? DefaultPurpose : _purpose.Value!;

        /// <summary>
        /// 開一個新的帳目容器並設為目前流程的 scope。必須在「執行節點的 async 方法本體」裡呼叫：
        /// AsyncLocal 的值會流進它 await 的子呼叫，方法結束後自動還原，不會漏到呼叫端。
        /// </summary>
        public static UsageScope BeginScope()
        {
            var scope = new UsageScope();
            _scope.Value = scope;
            return scope;
        }

        /// <summary>標記接下來（using 區塊內）呼叫的用途。巢狀時內層覆蓋、離開後還原。</summary>
        public static IDisposable Purpose(string purpose)
        {
            string? previous = _purpose.Value;
            _purpose.Value = purpose;
            return new Restore(() => _purpose.Value = previous);
        }

        /// <summary>只有外層沒標用途時才標（例：主回覆流程被影片導演借用時，保留「影片導演企劃」）。</summary>
        public static IDisposable PurposeIfUnset(string purpose)
            => string.IsNullOrWhiteSpace(_purpose.Value) ? Purpose(purpose) : new Restore(() => { });

        /// <summary>
        /// 記一次 LLM 呼叫。actual 有值（&gt;0）用真實用量；否則依文字估算並標示為估算。
        /// 兩者皆 0（例如空回應且無 usage）不記。
        /// </summary>
        public static UsageRecord? RecordLlm(
            string? serviceModel,
            int actualInputTokens,
            int actualOutputTokens,
            string? estimateInputText = null,
            string? estimateOutputText = null,
            string note = "",
            double extraUsd = 0)
        {
            string modelId = ModelCostEstimator.ResolvePricingModelId(serviceModel);
            bool actual = actualInputTokens > 0 || actualOutputTokens > 0;

            var est = actual
                ? ModelCostEstimator.FromUsage(modelId, actualInputTokens, actualOutputTokens)
                : ModelCostEstimator.Compute(modelId, estimateInputText, estimateOutputText);

            if (est.TotalTokens == 0 && extraUsd <= 0)
                return null;

            var record = new UsageRecord
            {
                Kind = UsageKinds.Llm,
                ModelId = modelId,
                Purpose = CurrentPurpose,
                InputTokens = est.InputTokens,
                OutputTokens = est.OutputTokens,
                IsActual = actual,
                UsdCost = est.UsdCost + Math.Max(0, extraUsd),
                Note = note ?? ""
            };

            Commit(record);
            return record;
        }

        /// <summary>記一次按量計價的呼叫（圖片張數、影片秒數、搜尋次數）。</summary>
        public static UsageRecord RecordMetered(
            string kind,
            string modelId,
            double quantity,
            string unit,
            double usd,
            bool isActual = false,
            string note = "",
            string? purpose = null)
        {
            var record = new UsageRecord
            {
                Kind = kind,
                ModelId = modelId ?? "",
                Purpose = string.IsNullOrWhiteSpace(purpose) ? CurrentPurpose : purpose!,
                Quantity = quantity,
                Unit = unit ?? "",
                IsActual = isActual,
                UsdCost = usd,
                Note = note ?? ""
            };

            Commit(record);
            return record;
        }

        private static void Commit(UsageRecord record)
        {
            string label = string.IsNullOrWhiteSpace(record.ModelId)
                ? record.Purpose
                : $"{record.Purpose}・{record.ModelId}";

            if (record.UsdCost > 0)
                SpendLedger.Add(record.UsdCost, label);
            else if (record.UsdCost < 0)
                SpendLedger.Subtract(-record.UsdCost, label);

            _scope.Value?.Add(record);
        }

        private sealed class Restore : IDisposable
        {
            private Action? _action;
            public Restore(Action action) => _action = action;
            public void Dispose()
            {
                _action?.Invoke();
                _action = null;
            }
        }
    }

    /// <summary>把一次執行的帳目加總成決策窗/手機顯示用的數字與文字。</summary>
    public sealed class UsageSummary
    {
        public int LlmCalls { get; private init; }
        public int InputTokens { get; private init; }
        public int OutputTokens { get; private init; }
        public int TotalTokens => InputTokens + OutputTokens;
        public bool LlmAllActual { get; private init; }
        public double LlmUsd { get; private init; }
        public double MeteredUsd { get; private init; }
        public double TotalUsd => LlmUsd + MeteredUsd;
        public IReadOnlyList<string> Models { get; private init; } = Array.Empty<string>();
        public string MeteredLabel { get; private init; } = "";
        public bool IsEmpty { get; private init; }

        public static UsageSummary From(IEnumerable<UsageRecord>? records)
        {
            var list = (records ?? Array.Empty<UsageRecord>()).Where(r => r != null).ToList();
            var llm = list.Where(r => r.Kind == UsageKinds.Llm).ToList();
            var metered = list.Where(r => r.Kind != UsageKinds.Llm).ToList();

            return new UsageSummary
            {
                IsEmpty = list.Count == 0,
                LlmCalls = llm.Count,
                InputTokens = llm.Sum(r => r.InputTokens),
                OutputTokens = llm.Sum(r => r.OutputTokens),
                LlmAllActual = llm.All(r => r.IsActual),
                LlmUsd = llm.Sum(r => r.UsdCost),
                MeteredUsd = metered.Sum(r => r.UsdCost),
                Models = llm.Select(r => r.ModelId).Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MeteredLabel = BuildMeteredLabel(metered)
            };
        }

        /// <summary>
        /// 例：「實際 12.3k tokens（4 次 AI 呼叫・2 個模型）+ 圖片 1 張、影片 8 秒 · 合計約 NT$15.20」。
        /// 有任何一筆是估算就標「含估算」——不把估算包裝成實際。
        /// </summary>
        public string BuildCostDisplay()
        {
            if (IsEmpty)
                return "";

            var parts = new List<string>();

            if (LlmCalls > 0)
            {
                string prefix = LlmAllActual ? "實際" : "含估算";
                string models = Models.Count > 1 ? $"・{Models.Count} 個模型" : "";
                parts.Add($"{prefix} {ModelCostEstimator.FormatTokens(TotalTokens)} tokens（{LlmCalls} 次 AI 呼叫{models}）");
            }

            if (!string.IsNullOrWhiteSpace(MeteredLabel))
                parts.Add(MeteredLabel);

            return $"{string.Join(" + ", parts)} · 合計約 {ModelCostEstimator.FormatTwd(Math.Max(0, TotalUsd))}";
        }

        /// <summary>
        /// 決策窗「成本明細」：同用途＋同模型合併成一行，例：
        /// 「主回覆 · Claude Sonnet 5 ×2 · 12.3k tokens · 約 NT$1.20」、「影片生成 · veo-3.1-lite ×1 · 8 秒 · 約 NT$12.80」。
        /// </summary>
        public static IReadOnlyList<string> BuildBreakdownLines(IEnumerable<UsageRecord>? records)
        {
            var list = (records ?? Array.Empty<UsageRecord>()).Where(r => r != null).ToList();

            return list
                .GroupBy(r => (r.Purpose, r.ModelId, r.Kind, Refund: r.UsdCost < 0))
                .Select(g =>
                {
                    var first = g.First();
                    string model = DisplayModel(first.ModelId);
                    string count = g.Count() > 1 ? $" ×{g.Count()}" : "";
                    string amount = first.Kind == UsageKinds.Llm
                        ? $"{ModelCostEstimator.FormatTokens(g.Sum(r => r.InputTokens + r.OutputTokens))} tokens"
                        : $"{g.Sum(r => r.Quantity):0.##} {first.Unit}";
                    string estimated = g.Any(r => !r.IsActual) ? "（估算）" : "";
                    double usd = g.Sum(r => r.UsdCost);
                    string money = usd < 0
                        ? $"沖銷 {ModelCostEstimator.FormatTwd(-usd)}"
                        : $"約 {ModelCostEstimator.FormatTwd(usd)}";
                    string modelPart = string.IsNullOrWhiteSpace(model) ? "" : $" · {model}{count}";
                    return $"{first.Purpose}{modelPart} · {amount}{estimated} · {money}";
                })
                .ToList();
        }

        private static string DisplayModel(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return "";
            return AiModelRegistry.TryGetLegacyDisplayName(modelId)
                ?? AiModelRegistry.Find(modelId)?.DisplayName
                ?? modelId;
        }

        private static string BuildMeteredLabel(IReadOnlyList<UsageRecord> metered)
        {
            if (metered.Count == 0)
                return "";

            var labels = new List<string>();

            double images = metered.Where(r => r.Kind == UsageKinds.Image).Sum(r => r.Quantity);
            if (images > 0) labels.Add($"圖片 {images:0} 張");

            // 影片秒數只算正向帳目（沖銷帳目的秒數是「沒收費的那段」，不是多生成的）。
            var video = metered.Where(r => r.Kind == UsageKinds.Video).ToList();
            double videoSeconds = video.Where(r => r.UsdCost > 0).Sum(r => r.Quantity);
            double refundedSeconds = video.Where(r => r.UsdCost < 0).Sum(r => r.Quantity);
            if (videoSeconds > 0)
            {
                labels.Add(refundedSeconds > 0
                    ? $"影片 {videoSeconds:0} 秒（失敗沖銷 {refundedSeconds:0} 秒）"
                    : $"影片 {videoSeconds:0} 秒");
            }

            double searches = metered.Where(r => r.Kind == UsageKinds.Search).Sum(r => r.Quantity);
            if (searches > 0) labels.Add($"搜尋 {searches:0} 次");

            return string.Join("、", labels);
        }
    }
}
