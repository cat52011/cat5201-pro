using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 長任務日誌（#10 / Codex P2-19 的影片切片）：Veo 影片生成是「先建 operation、再輪詢數分鐘」的
    /// 長跑任務——程式中途關閉/崩潰時,雲端 operation 其實還在跑（錢已經付了）,但舊版把
    /// operation name 只留在記憶體 → 白花錢。
    ///
    /// 本日誌把每個 operation 落地（建立就記、完成/失敗就標）,重啟時 GetResumable() 找出
    /// 「pending 且未過期」的任務,由 MainWindow 詢問使用者後續輪 + 下載,不重複付費。
    /// 模式同 SpendLedger / ApiKeyStore：static + Initialize(dir),AtomicFile 原子寫入。
    /// </summary>
    public static class VideoJobJournal
    {
        public sealed class VideoJob
        {
            public string OperationName { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string Status { get; set; } = "pending"; // pending / done / failed
            public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
            public string Note { get; set; } = "";

            // 成本可稽查：送出時已入帳的秒數與金額。CostRecorded=false 的舊紀錄（本欄位出現前建立）
            // 在恢復取回成功時才補記帳，避免漏記也避免重複記。
            public double Seconds { get; set; }
            public double CostUsd { get; set; }
            public bool CostRecorded { get; set; }
        }

        // Veo operation 在 Google 端的保存期有限（延伸 API 文件明載來源影片兩天內）——
        // 超過 48 小時的 pending 視為過期,不再提供恢復（誠實：撈不回來就別假裝能）。
        private static readonly TimeSpan ResumableWindow = TimeSpan.FromHours(48);

        private static readonly object _lock = new();
        private static string _filePath = "";
        private static List<VideoJob> _jobs = new();

        public static void Initialize(string dir)
        {
            lock (_lock)
            {
                _filePath = Path.Combine(dir, "_video_jobs.json");
                try
                {
                    string? json = AtomicFile.ReadAllTextWithFallback(_filePath);
                    _jobs = string.IsNullOrWhiteSpace(json)
                        ? new List<VideoJob>()
                        : (JsonSerializer.Deserialize<List<VideoJob>>(json!) ?? new List<VideoJob>());
                }
                catch (Exception ex)
                {
                    AppLog.Warn("VideoJobs", "影片任務日誌載入失敗，改用空日誌", ex);
                    _jobs = new List<VideoJob>();
                }
            }
        }

        /// <summary>operation 建立成功即記錄（此刻起「錢已經付了」，值得被記住）。</summary>
        public static void Record(string operationName, string prompt, double seconds = 0, double costUsd = 0, bool costRecorded = false)
        {
            if (string.IsNullOrWhiteSpace(operationName))
                return;

            lock (_lock)
            {
                if (_jobs.Any(j => j.OperationName == operationName))
                    return;
                _jobs.Add(new VideoJob
                {
                    OperationName = operationName,
                    Prompt = prompt ?? "",
                    Seconds = seconds,
                    CostUsd = costUsd,
                    CostRecorded = costRecorded
                });
                TrimAndSaveLocked();
            }
        }

        /// <summary>取某個 operation 的紀錄副本（查不到回 null）。</summary>
        public static VideoJob? Find(string operationName)
        {
            lock (_lock)
            {
                var job = _jobs.FirstOrDefault(j => j.OperationName == operationName);
                return job == null ? null : new VideoJob
                {
                    OperationName = job.OperationName,
                    Prompt = job.Prompt,
                    Status = job.Status,
                    CreatedAtUtc = job.CreatedAtUtc,
                    Note = job.Note,
                    Seconds = job.Seconds,
                    CostUsd = job.CostUsd,
                    CostRecorded = job.CostRecorded
                };
            }
        }

        /// <summary>恢復取回時補記了舊紀錄的成本後呼叫，之後不再重複記。</summary>
        public static void MarkCostRecorded(string operationName, double costUsd)
        {
            lock (_lock)
            {
                var job = _jobs.FirstOrDefault(j => j.OperationName == operationName);
                if (job == null)
                    return;
                job.CostUsd = costUsd;
                job.CostRecorded = true;
                TrimAndSaveLocked();
            }
        }

        public static void MarkDone(string operationName) => Mark(operationName, "done", "");

        public static void MarkFailed(string operationName, string note) => Mark(operationName, "failed", note);

        private static void Mark(string operationName, string status, string note)
        {
            if (string.IsNullOrWhiteSpace(operationName))
                return;

            lock (_lock)
            {
                var job = _jobs.FirstOrDefault(j => j.OperationName == operationName);
                if (job == null)
                    return;
                job.Status = status;
                job.Note = note ?? "";
                TrimAndSaveLocked();
            }
        }

        /// <summary>各狀態的任務數（診斷用，不含提示詞內容）。</summary>
        public static IReadOnlyDictionary<string, int> GetStatusCounts()
        {
            lock (_lock)
            {
                return _jobs.GroupBy(j => j.Status).ToDictionary(g => g.Key, g => g.Count());
            }
        }

        /// <summary>可恢復的任務：pending 且未超過 48 小時（新→舊）。</summary>
        public static IReadOnlyList<VideoJob> GetResumable()
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - ResumableWindow;
                return _jobs
                    .Where(j => j.Status == "pending" && j.CreatedAtUtc >= cutoff)
                    .OrderByDescending(j => j.CreatedAtUtc)
                    .ToList();
            }
        }

        /// <summary>
        /// 恢復提示與取回檔名用的短標籤。送進 Veo 的 prompt 是「風格標籤 + 空行 + 鏡頭內容」，
        /// 開頭永遠是同一串風格字——直接截前幾十字，每支片看起來都一樣。改取最後一段（鏡頭內容）再截斷。
        /// </summary>
        public static string Summarize(string? prompt, int maxChars = 40)
        {
            const string extendMark = "(影片延伸) ";
            string p = (prompt ?? "").Replace("\r\n", "\n").Trim();
            bool isExtend = p.StartsWith(extendMark, StringComparison.Ordinal);
            if (isExtend)
                p = p.Substring(extendMark.Length);

            var parts = p.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string body = parts.Length > 0 ? parts[^1] : "";
            if (body.Length > maxChars)
                body = body.Substring(0, maxChars) + "…";

            return (isExtend ? extendMark : "") + body;
        }

        private static void TrimAndSaveLocked()
        {
            // 已完成/過期的舊紀錄只留 30 天內，避免日誌無限長大。
            var cutoff = DateTime.UtcNow.AddDays(-30);
            _jobs.RemoveAll(j => j.CreatedAtUtc < cutoff && j.Status != "pending");

            if (string.IsNullOrWhiteSpace(_filePath))
                return;

            try
            {
                AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(_jobs, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Warn("VideoJobs", "影片任務日誌寫入失敗", ex); }
        }
    }
}
