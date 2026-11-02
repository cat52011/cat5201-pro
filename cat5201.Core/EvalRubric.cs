using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 必要內容清單裡的一個項目。
    ///
    /// 存在的理由：本專題一直無法回答「壓到指定頁數時，被刪掉的是不是重要的東西」，
    /// 也無法回答「這份產出有沒有把該講的講到」——因為「必要」從來沒有被寫下來過。
    /// 這個型別就是把「必要」寫成機器讀得懂的形式：每題事先列出必要項目，
    /// 產出完成後逐項比對，涵蓋率才有定義、才可重複量測。
    ///
    /// 誠實界線（報告與答辯都要講）：
    /// 本比對量的是「必要主題有沒有出現」，不是「說得對不對」。
    /// 事實正確率仍然必須由人評分；機器只負責不必靠判斷的那一半。
    /// </summary>
    public sealed class EvalRequiredItem
    {
        public const string KindAnyOf = "any_of";       // 任一樣式出現即算涵蓋（最常用）
        public const string KindAllOf = "all_of";       // 全部樣式都要出現
        public const string KindAtLeast = "at_least";   // 至少命中 min_count 個不同樣式（「舉三個案例」）
        public const string KindNumber = "number";      // 別名附近要有落在容許誤差內的數字
        public const string KindSource = "source";      // 要有指定網域下的可點連結
        public const string KindDeclines = "declines";  // 要明確表示「無法確認／尚未公布」
        public const string KindLength = "length";      // 字數落在區間內（「300 字的信」）

        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string Kind { get; init; } = KindAnyOf;

        /// <summary>要比對的字樣。any_of / all_of / declines 用。</summary>
        public List<string> Patterns { get; init; } = new();

        /// <summary>number 用：期望的數值。</summary>
        public double? Number { get; init; }

        /// <summary>number 用：容許的相對誤差（百分比）。預設 1%。</summary>
        public double TolerancePercent { get; init; } = 1.0;

        /// <summary>number 用：這個數字的別名，必須出現在數字附近。</summary>
        public List<string> Aliases { get; init; } = new();

        /// <summary>at_least 用：至少要命中幾個不同的樣式。</summary>
        public int MinCount { get; init; } = 1;

        /// <summary>length 用：字數下限／上限（0 = 不限）。計字時不含空白。</summary>
        public int MinChars { get; init; }
        public int MaxChars { get; init; }

        /// <summary>source 用：可接受的網域（比對到網域邊界，不是字串包含）。</summary>
        public List<string> Domains { get; init; } = new();

        /// <summary>
        /// 缺這一項就直接不及格，而不只是扣涵蓋率。
        /// 用在結論、限制、必答主題上——正是「壓頁數時不准犧牲」的那一類。
        /// </summary>
        public bool Critical { get; init; }
    }

    /// <summary>不該出現的東西：幻覺陷阱題的反面條件。</summary>
    public sealed class EvalForbiddenItem
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public List<string> Patterns { get; init; } = new();
    }

    /// <summary>一道評測題的完整規格：題目原文 + 必要內容清單 + 禁止內容。</summary>
    public sealed class EvalRubricSpec
    {
        public string TaskId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public string Prompt { get; init; } = "";

        /// <summary>
        /// 這題的答案會隨時間改變，過了這天就不該再用（例如「尚未公布」的事一旦公布就不再是陷阱題）。
        /// 空白＝不過期。比對器不會因為過期就拒絕評分，但會把它列成警告——
        /// 一份悄悄失效的題目會讓分數看起來還有效，那比沒有題目更危險。
        /// </summary>
        public string ExpiresOn { get; init; } = "";

        public List<EvalRequiredItem> Required { get; init; } = new();
        public List<EvalForbiddenItem> Forbidden { get; init; } = new();

        /// <summary>這題的事實正確率要人工抽查哪幾個數字／主張（機器不判斷對錯）。</summary>
        public List<string> HumanChecks { get; init; } = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>
        /// 解析並檢查規格。刻意在這裡丟例外而不是靜默跳過：
        /// 一份寫壞的清單會讓涵蓋率看起來很漂亮，那比沒有清單更糟。
        /// </summary>
        public static EvalRubricSpec Parse(string json)
        {
            var spec = JsonSerializer.Deserialize<EvalRubricSpec>(json, JsonOpts)
                       ?? throw new FormatException("評測題規格解析結果為空");

            if (string.IsNullOrWhiteSpace(spec.TaskId))
                throw new FormatException("評測題規格缺少 task_id");
            if (spec.Required.Count == 0)
                throw new FormatException(spec.TaskId + "：必要內容清單是空的——沒有清單就量不出涵蓋率");

            var dupe = spec.Required.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault(g => g.Count() > 1);
            if (dupe != null)
                throw new FormatException(spec.TaskId + "：必要項目 id 重複（" + dupe.Key + "）");

            foreach (var item in spec.Required)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                    throw new FormatException(spec.TaskId + "：有必要項目沒有 id");

                string kind = (item.Kind ?? "").Trim().ToLowerInvariant();
                if (kind == EvalRequiredItem.KindNumber && item.Number == null)
                    throw new FormatException(spec.TaskId + "/" + item.Id + "：kind=number 但沒給 number");
                if (kind == EvalRequiredItem.KindSource && item.Domains.Count == 0)
                    throw new FormatException(spec.TaskId + "/" + item.Id + "：kind=source 但沒給 domains");
                if ((kind == EvalRequiredItem.KindAnyOf || kind == EvalRequiredItem.KindAllOf
                     || kind == EvalRequiredItem.KindAtLeast)
                    && item.Patterns.Count == 0)
                    throw new FormatException(spec.TaskId + "/" + item.Id + "：沒給 patterns");
                if (kind == EvalRequiredItem.KindAtLeast && item.MinCount > item.Patterns.Count)
                    throw new FormatException(spec.TaskId + "/" + item.Id
                        + "：min_count 比 patterns 還多，這一項永遠不可能通過");
                if (kind == EvalRequiredItem.KindLength && item.MinChars <= 0 && item.MaxChars <= 0)
                    throw new FormatException(spec.TaskId + "/" + item.Id + "：kind=length 但沒給字數區間");
            }

            return spec;
        }

        /// <summary>這題是否已經過了有效期限（ExpiresOn 空白時永遠為 false）。</summary>
        public bool IsExpired(DateTime nowLocal)
            => DateTime.TryParse(ExpiresOn, CultureInfo.InvariantCulture,
                                 DateTimeStyles.None, out var due)
               && nowLocal.Date > due.Date;
    }

    /// <summary>一次比對的結果。</summary>
    public sealed class EvalScore
    {
        public string TaskId { get; init; } = "";
        public int RequiredTotal { get; init; }
        public int RequiredCovered { get; init; }
        public IReadOnlyList<string> CoveredIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MissingIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MissingLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CriticalMissingIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ForbiddenHitIds { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 必要項的**字樣命中率**。
        ///
        /// 刻意不叫「內容涵蓋率」或「保留率」：2026-10-28 第四輪 AI 輔助審查以一行
        /// 「排隊、變異、批量、瓶頸、量測。」命中了 T09 的五個重點項——那不是一份摘要，只是列詞。
        /// 這個數字量的是「必要字樣有沒有出現」，離「這件事有沒有做完」還很遠。
        /// 題目規格可以用 at_least（要求數個相關字樣共同出現）與 length（字數下限）把差距縮小，
        /// 但縮小不等於消除；拿它當品質證據時必須連這句話一起引用。
        /// </summary>
        public double Coverage => RequiredTotal == 0 ? 1.0 : (double)RequiredCovered / RequiredTotal;

        /// <summary>
        /// 必備項齊備，且沒踩到禁止內容。
        ///
        /// 刻意不叫 Passed：「及格」會被讀成「這份產出完成了任務」，而這裡只驗了字樣。
        /// 它是必要條件，不是充分條件——真正的驗收還要加上人工評分的事實正確率。
        /// </summary>
        public bool CriticalItemsSatisfied => CriticalMissingIds.Count == 0 && ForbiddenHitIds.Count == 0;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(TaskId).Append("：字樣命中 ").Append(RequiredCovered).Append('/').Append(RequiredTotal)
              .Append('（').Append((Coverage * 100).ToString("0.0", CultureInfo.InvariantCulture)).Append("%）")
              .Append(CriticalItemsSatisfied ? "，必備項齊備" : "，必備項不齊");
            if (CriticalMissingIds.Count > 0)
                sb.Append("：缺 ").Append(string.Join("、", CriticalMissingIds));
            if (ForbiddenHitIds.Count > 0)
                sb.Append("；踩到禁止內容：").Append(string.Join("、", ForbiddenHitIds));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 必要內容清單的比對器。
    ///
    /// 這是把「內容保留率」從形容詞變成數字的那一步：
    /// 同一份清單既可比對最終產出（評測用），也可比對壓縮前後的大綱（回歸用），
    /// 後者正好回答「壓到 N 頁之後，必要的東西還在不在」。
    /// </summary>
    public static class EvalRubric
    {
        /// <summary>數字與別名之間允許隔多遠（字元）。太寬會把無關的數字算成命中。</summary>
        private const int NumberProximityWindow = 120;

        private static readonly string[] DeclineVocabulary =
        {
            "尚未公布", "尚未公佈", "還沒公布", "未公布", "無法確認", "無法查證", "查不到",
            "沒有可靠來源", "不知道", "無法得知", "目前沒有", "尚無", "not announced",
            "not yet announced", "cannot confirm", "no reliable source", "unable to verify",
            "no information",
        };

        private static readonly Regex UrlPattern =
            new(@"https?://[^\s<>()\[\]""，。、；）】]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 123 / -5 / 1,234.5 / 12.3% / 3.5億 / 1.2兆 / 23.86B / 負 5
        //
        // 正負號是 2026-10-28 第四輪 AI 輔助審查抓到的洞：原本的樣式從「EPS: -5」裡
        // 只抽出 5，於是「EPS 必須是 5」這個必要項被 -5 判定為命中。
        // 一個會把符號相反的數字算成命中的計分器，算出來的分數不能用來支持任何品質主張。
        //
        // 負號只在「前一個字元不是數字」時才算符號，否則 2024-05 的 -05 會被讀成 -5。
        // 不過反過來 2024-05 仍會被當成 5（日期裡的數字），所以 number 項一律要求給別名，
        // 讓比對限制在別名附近。這個殘留限制寫在 docs/eval/README.md。
        private static readonly Regex NumberPattern =
            new(@"(?<sign>(?<![0-9])[-−﹣－]|負\s*)?(?<num>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?<suffix>兆|億|萬|千|%|bn|mn|[bmk]|billion|million|thousand)?",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 比對正規化：全形轉半形、轉小寫、刪掉所有空白。
        /// 刪空白是因為模型常寫「2026 年第二季」而清單寫「2026年第二季」，
        /// 這種差異跟內容有沒有講到完全無關。
        ///
        /// public 的理由：題目比對（把節點的提示詞對回題號）要用同一把尺，
        /// 兩邊各自實作一次正規化，遲早會對不起來。
        /// </summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;

                char ch = c;
                if (ch >= '！' && ch <= '～') ch = (char)(ch - 0xFEE0);  // 全形 ASCII
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        internal static bool ContainsPattern(string normalizedText, string pattern)
        {
            string p = Normalize(pattern);
            return p.Length > 0 && normalizedText.Contains(p, StringComparison.Ordinal);
        }

        /// <summary>把數字字樣換算成實際數值（含正負號、中文與英文量詞）。</summary>
        /// <summary>
        /// 把一段文字裡的每一個數字（含正負號、量詞）抽出來。
        /// SignificantDigits＝原文寫了幾位有效數字（1,494.24 → 6），用來排除「5」「10」這種到處都有的整數。
        /// 與 HasNumberNearAlias 用同一套樣式，數字比對規則只有一份。
        /// </summary>
        internal static IEnumerable<(double Value, string Text, int Index, int SignificantDigits)> ExtractNumbers(string? text)
        {
            foreach (Match m in NumberPattern.Matches(text ?? ""))
            {
                double? value = ParseNumber(m.Groups["num"].Value, m.Groups["suffix"].Value, m.Groups["sign"].Value);
                if (value == null) continue;

                string digits = new string(m.Groups["num"].Value.Where(char.IsDigit).ToArray()).TrimStart('0');
                yield return (value.Value, m.Value.Trim(), m.Index, digits.TrimEnd('0').Length);
            }
        }

        internal static double? ParseNumber(string raw, string? suffix, string? sign = null)
        {
            if (!double.TryParse(raw.Replace(",", ""), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double v))
                return null;

            if (!string.IsNullOrWhiteSpace(sign)) v = -v;

            double scale = (suffix ?? "").Trim().ToLowerInvariant() switch
            {
                "兆" => 1e12,
                "億" => 1e8,
                "萬" => 1e4,
                "千" or "k" or "thousand" => 1e3,
                "m" or "mn" or "million" => 1e6,
                "b" or "bn" or "billion" => 1e9,
                _ => 1.0,
            };
            return v * scale;
        }

        /// <summary>
        /// 數字是否出現在某個別名附近且落在容許誤差內。
        /// 要求「附近」是為了避免「文章裡剛好有個 989」被算成「營收 989 億」。
        /// </summary>
        internal static bool HasNumberNearAlias(
            string rawText, double expected, double tolerancePercent, IReadOnlyList<string> aliases)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return false;

            double tolerance = Math.Abs(expected) * Math.Max(tolerancePercent, 0) / 100.0;
            if (tolerance == 0) tolerance = Math.Abs(expected) * 1e-9;

            var aliasPositions = new List<int>();
            foreach (string alias in aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                int from = 0;
                while (from < rawText.Length)
                {
                    int at = rawText.IndexOf(alias.Trim(), from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    aliasPositions.Add(at);
                    from = at + 1;
                }
            }

            // 沒給別名就退回「整份文件裡有這個數字」——寬鬆，所以規格裡要求給別名。
            bool aliasRequired = aliases.Any(a => !string.IsNullOrWhiteSpace(a));
            if (aliasRequired && aliasPositions.Count == 0) return false;

            foreach (Match m in NumberPattern.Matches(rawText))
            {
                double? value = ParseNumber(
                    m.Groups["num"].Value, m.Groups["suffix"].Value, m.Groups["sign"].Value);
                if (value == null || Math.Abs(value.Value - expected) > tolerance) continue;

                if (!aliasRequired) return true;
                if (aliasPositions.Any(p => Math.Abs(p - m.Index) <= NumberProximityWindow)) return true;
            }

            return false;
        }

        /// <summary>文中是否出現指定網域下的連結。網域比對沿用 SearchCapability 的邊界規則。</summary>
        internal static bool HasSourceUnder(string rawText, IReadOnlyList<string> domains)
        {
            foreach (Match m in UrlPattern.Matches(rawText ?? ""))
            {
                string candidate = m.Value.TrimEnd('.', '，', '。', ')', '）');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;

                string host = uri.Host.ToLowerInvariant();
                if (domains.Any(d => SearchCapability.HostIsUnder(host, (d ?? "").Trim().ToLowerInvariant())))
                    return true;
            }
            return false;
        }

        internal static bool IsCovered(EvalRequiredItem item, string rawText, string normalizedText)
        {
            switch ((item.Kind ?? "").Trim().ToLowerInvariant())
            {
                case EvalRequiredItem.KindAllOf:
                    return item.Patterns.Count > 0
                           && item.Patterns.All(p => ContainsPattern(normalizedText, p));

                case EvalRequiredItem.KindAtLeast:
                    return item.Patterns.Count(p => ContainsPattern(normalizedText, p))
                           >= Math.Max(item.MinCount, 1);

                case EvalRequiredItem.KindLength:
                    // 用正規化後的長度計字：不含空白，中英文都算一個字元。
                    // 這不是嚴格的「字數」定義，但三組用同一把尺，比較才公平。
                    return normalizedText.Length >= item.MinChars
                           && (item.MaxChars <= 0 || normalizedText.Length <= item.MaxChars);

                case EvalRequiredItem.KindNumber:
                    return item.Number != null
                           && HasNumberNearAlias(rawText, item.Number.Value, item.TolerancePercent, item.Aliases);

                case EvalRequiredItem.KindSource:
                    return HasSourceUnder(rawText, item.Domains);

                case EvalRequiredItem.KindDeclines:
                    return item.Patterns.Concat(DeclineVocabulary)
                               .Any(p => ContainsPattern(normalizedText, p));

                default: // any_of
                    return item.Patterns.Any(p => ContainsPattern(normalizedText, p));
            }
        }

        /// <summary>比對一份純文字產出。</summary>
        public static EvalScore Score(EvalRubricSpec spec, string? outputText)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            string raw = outputText ?? "";
            string norm = Normalize(raw);

            var covered = new List<string>();
            var missing = new List<EvalRequiredItem>();

            foreach (var item in spec.Required)
            {
                if (IsCovered(item, raw, norm)) covered.Add(item.Id);
                else missing.Add(item);
            }

            var hits = spec.Forbidden
                .Where(f => f.Patterns.Any(p => ContainsPattern(norm, p)))
                .Select(f => f.Id)
                .ToList();

            return new EvalScore
            {
                TaskId = spec.TaskId,
                RequiredTotal = spec.Required.Count,
                RequiredCovered = covered.Count,
                CoveredIds = covered,
                MissingIds = missing.Select(x => x.Id).ToList(),
                MissingLabels = missing.Select(x => x.Label).ToList(),
                CriticalMissingIds = missing.Where(x => x.Critical).Select(x => x.Id).ToList(),
                ForbiddenHitIds = hits,
            };
        }

        /// <summary>把簡報大綱攤平成可比對的文字（簡報標題 + 主題 + 每頁標題與重點）。</summary>
        public static string Flatten(PresentationOutlinePayload? outline)
        {
            if (outline == null) return "";

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(outline.Title)) sb.AppendLine(outline.Title);
            if (!string.IsNullOrWhiteSpace(outline.Topic)) sb.AppendLine(outline.Topic);

            foreach (var slide in outline.Slides ?? Array.Empty<PresentationSlidePayload>())
            {
                if (!string.IsNullOrWhiteSpace(slide.Heading)) sb.AppendLine(slide.Heading);
                foreach (string b in slide.Bullets ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(b)) sb.AppendLine(b);
            }
            return sb.ToString();
        }

        public static EvalScore ScoreOutline(EvalRubricSpec spec, PresentationOutlinePayload? outline)
            => Score(spec, Flatten(outline));

        /// <summary>
        /// 壓縮前後的必要內容保留率。
        /// 這就是「要求 5 頁卻刪掉結論」那個問題的量尺：
        /// LostIds 就是為了守住頁數而真正失去的必要內容。
        /// </summary>
        public static (EvalScore Before, EvalScore After, IReadOnlyList<string> LostIds) Retention(
            EvalRubricSpec spec, PresentationOutlinePayload? before, PresentationOutlinePayload? after)
        {
            var b = ScoreOutline(spec, before);
            var a = ScoreOutline(spec, after);
            var lost = b.CoveredIds.Except(a.CoveredIds, StringComparer.OrdinalIgnoreCase).ToList();
            return (b, a, lost);
        }
    }
}
