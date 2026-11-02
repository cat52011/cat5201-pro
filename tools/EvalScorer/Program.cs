using System.Globalization;
using System.Text;
using Cat5201;

namespace Cat5201.EvalScorer
{
    /// <summary>
    /// 評測計分器。五個子命令：
    ///
    ///   validate  檢查 tasks/ 底下每一份必要內容清單是否寫得合法、有沒有過期
    ///   freeze    把題目規格的內容雜湊寫成事前登錄檔 —— 證明清單沒有事後調整過
    ///   ingest    從 App 的專案存檔讀出產出與客觀欄，產生 outputs/ 與 metrics.csv
    ///   blind     把產出複製成代號檔名、另存對照表 —— 盲評不是靠自律，是靠看不到組別
    ///   score     套清單算字樣命中率，併入成本/耗時，輸出 scores.csv 與分組摘要
    ///
    /// 這支程式刻意不碰任何 API、不需要金鑰、不會產生費用：
    /// 評分必須能在別人的電腦上用同一份資料重跑出同一個數字，否則不叫驗證。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string cmd = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";
            string tasksDir = Arg(args, "--tasks") ?? Path.Combine("docs", "eval", "tasks");
            string runDir = Arg(args, "--run") ?? "";

            try
            {
                return cmd switch
                {
                    "validate" => Validate(tasksDir),
                    "freeze" => Freeze(tasksDir, runDir),
                    "ingest" => Ingest(tasksDir, runDir, Arg(args, "--project") ?? ""),
                    "blind" => Blind(runDir),
                    "score" => Score(tasksDir, runDir),
                    _ => Help(),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("失敗：" + ex.Message);
                return 2;
            }
        }

        private static int Help()
        {
            Console.WriteLine("""
                用法（在 repo 根目錄執行）：

                  dotnet run --project tools/EvalScorer -- validate
                  dotnet run --project tools/EvalScorer -- freeze  --run docs/eval/runs/2026-10-29
                  dotnet run --project tools/EvalScorer -- ingest  --run docs/eval/runs/2026-10-29 --project "D:\path\eval.json"
                  dotnet run --project tools/EvalScorer -- blind   --run docs/eval/runs/2026-10-29
                  dotnet run --project tools/EvalScorer -- score   --run docs/eval/runs/2026-10-29

                一次 run 的資料夾長這樣：

                  docs/eval/runs/<日期>/
                    preregistration.txt      ← freeze 產生：題目規格雜湊 + 程式版本
                    outputs/T01-A-1.txt      ← 產出原文（檔名＝題號-組別-第幾次）
                    metrics.csv              ← 客觀欄；ingest 由專案存檔自動產生
                    blind/XXXX.txt           ← blind 產生，給人評分用
                    blind/key.csv            ← blind 產生的對照表，評分完才打開
                    scores.csv               ← score 產生
                """);
            return 1;
        }

        private static string? Arg(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ───────────────────────── validate ─────────────────────────

        private static Dictionary<string, EvalRubricSpec> LoadSpecs(string tasksDir)
        {
            if (!Directory.Exists(tasksDir))
                throw new DirectoryNotFoundException("找不到題目資料夾：" + Path.GetFullPath(tasksDir));

            var specs = new Dictionary<string, EvalRubricSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(tasksDir, "*.json").OrderBy(x => x))
            {
                var spec = EvalRubricSpec.Parse(File.ReadAllText(file));
                if (!specs.TryAdd(spec.TaskId, spec))
                    throw new InvalidDataException("task_id 重複：" + spec.TaskId);
            }
            if (specs.Count == 0)
                throw new InvalidDataException("題目資料夾裡沒有任何 .json");
            return specs;
        }

        private static int Validate(string tasksDir)
        {
            var specs = LoadSpecs(tasksDir);
            int warn = 0;

            foreach (var spec in specs.Values.OrderBy(x => x.TaskId, StringComparer.Ordinal))
            {
                int critical = spec.Required.Count(x => x.Critical);
                Console.WriteLine($"{spec.TaskId}  必要 {spec.Required.Count} 項（必備 {critical}）" +
                                  $"／禁止 {spec.Forbidden.Count} 項／人工抽查 {spec.HumanChecks.Count} 項  {spec.Title}");

                if (critical == 0)
                {
                    Console.WriteLine("   ⚠ 沒有任何 critical 項：這題永遠不會不及格，等於沒有驗收條件");
                    warn++;
                }
                if (spec.IsExpired(DateTime.Now))
                {
                    Console.WriteLine($"   ⚠ 已過有效期限 {spec.ExpiresOn}：答案可能已經公開，這題不再有效");
                    warn++;
                }
                if (spec.HumanChecks.Count == 0 && spec.Required.Any(x => x.Kind == EvalRequiredItem.KindSource))
                {
                    Console.WriteLine("   ⚠ 有 source 項卻沒列人工抽查：機器只驗網域，不驗那一頁是否真的支持數字");
                    warn++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"共 {specs.Count} 題，{warn} 項警告。");
            return warn == 0 ? 0 : 1;
        }

        // ───────────────────────── freeze（事前登錄） ─────────────────────────

        /// <summary>
        /// 把題目規格的內容雜湊與程式版本寫進 run 資料夾。
        ///
        /// 為什麼需要：整份實驗的可信度取決於「清單是跑之前就定好的」。
        /// 口頭保證沒有用；雜湊可以讓任何人事後驗證那十份清單一個字都沒改過。
        /// 刻意用檔案內容的 SHA-256，而不是只記 git commit——
        /// 未提交的修改不會反映在 commit hash 上，那個漏洞正好是這份登錄要堵的。
        /// </summary>
        private static int Freeze(string tasksDir, string runDir)
        {
            if (string.IsNullOrWhiteSpace(runDir)) throw new ArgumentException("請用 --run 指定 run 資料夾");

            var specs = LoadSpecs(tasksDir);
            Directory.CreateDirectory(runDir);

            var sb = new StringBuilder();
            sb.Append("# 事前登錄（pre-registration）\n\n")
              .Append("產生時間：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n')
              .Append("題目資料夾：").Append(Path.GetFullPath(tasksDir)).Append('\n')
              .Append("題數：").Append(specs.Count).Append("\n\n")
              .Append("這份檔案的用途：證明必要內容清單在執行之前就已定稿。\n")
              .Append("跑完之後若有人懷疑清單被事後調整過，重算一次雜湊即可驗證。\n")
              .Append("雜湊對不上就表示清單變動過，該次結果不得引用。\n\n")
              .Append("## 題目規格 SHA-256\n\n");

            using var sha = System.Security.Cryptography.SHA256.Create();
            foreach (string file in Directory.GetFiles(tasksDir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                string hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file))).ToLowerInvariant();
                sb.Append(hash).Append("  ").Append(Path.GetFileName(file)).Append('\n');
            }

            sb.Append("\n## 固定素材 SHA-256\n\n");
            string materials = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tasksDir))!, "materials");
            if (Directory.Exists(materials))
                foreach (string file in Directory.GetFiles(materials).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file))).ToLowerInvariant();
                    sb.Append(hash).Append("  ").Append(Path.GetFileName(file)).Append('\n');
                }
            else
                sb.Append("（找不到 materials 資料夾）\n");

            sb.Append("\n## 每題的驗收條件\n\n");
            foreach (var spec in specs.Values.OrderBy(x => x.TaskId, StringComparer.Ordinal))
                sb.Append(spec.TaskId).Append("：必要 ").Append(spec.Required.Count)
                  .Append(" 項（必備 ").Append(spec.Required.Count(x => x.Critical))
                  .Append("）／禁止 ").Append(spec.Forbidden.Count)
                  .Append(" 項／人工抽查 ").Append(spec.HumanChecks.Count)
                  .Append(" 項　").Append(spec.Title).Append('\n');

            string outPath = Path.Combine(runDir, "preregistration.txt");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine("已寫入事前登錄：" + Path.GetFullPath(outPath));
            Console.WriteLine("請在開跑前把這個檔案一起提交（或至少記下雜湊），跑完不要重新產生。");
            return 0;
        }

        // ───────────────────────── ingest（從 App 存檔取數） ─────────────────────────

        /// <summary>
        /// 從 App 的專案存檔讀出每一次執行的產出與客觀欄。
        ///
        /// 為什麼不讓人從決策窗抄：抄寫會出錯，而且抄下來的數字沒有來源可追。
        /// 專案存檔裡本來就有每一次執行的 UsageRecords（逐筆的實際 token 與 USD）、
        /// 耗時、選模型方式與實際模型，直接讀它既精確又可稽查——
        /// 任何人拿同一個存檔重跑 ingest，會得到同一份 metrics.csv。
        ///
        /// 組別由存檔自己決定，不由操作者填：
        ///   API Auto → D（產品）、Manual + GPT → A、Manual + Claude → B。
        /// 題目由節點的提示詞與題目規格的 prompt 比對決定。
        /// </summary>
        private static int Ingest(string tasksDir, string runDir, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(runDir)) throw new ArgumentException("請用 --run 指定 run 資料夾");
            if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("請用 --project 指定 App 的專案存檔 (.json)");
            if (!File.Exists(projectPath)) throw new FileNotFoundException("找不到專案存檔", projectPath);

            var specs = LoadSpecs(tasksDir);
            var state = System.Text.Json.JsonSerializer.Deserialize<AppState>(File.ReadAllText(projectPath))
                        ?? throw new InvalidDataException("專案存檔解析失敗");

            // 判斷規則都在 Core（EvalRunIngest），有測試釘住；這裡只負責讀檔與寫檔。
            var result = EvalRunIngest.Build(state, specs.Values);

            foreach (string w in result.Warnings)
                Console.Error.WriteLine(w);

            if (result.Rows.Count == 0)
                throw new InvalidDataException("這個存檔裡沒有任何可用的執行紀錄");

            string outputsDir = Path.Combine(runDir, "outputs");
            Directory.CreateDirectory(outputsDir);

            // single_model 放最後一欄：score 讀前八欄，加在後面不會讓舊的讀法錯位。
            var csv = new StringBuilder("task,group,rep,tokens,cost_usd,cost_twd,seconds,models,single_model\n");

            Console.WriteLine("題目  組 次  tokens    US$      秒   模型");
            foreach (var r in result.Rows)
            {
                // 產出檔裡只放產出本文：盲評複製的時候不該帶著任何組別線索。
                File.WriteAllText(
                    Path.Combine(outputsDir, $"{r.TaskId}-{r.Group}-{r.Rep}.txt"),
                    r.Output, new UTF8Encoding(true));

                csv.Append(r.TaskId).Append(',').Append(r.Group).Append(',').Append(r.Rep).Append(',')
                   .Append(r.Tokens).Append(',')
                   .Append(r.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                   .Append((r.CostUsd * SpendLedger.UsdToTwdRate).ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                   .Append(Quote(r.Models)).Append(',')
                   .Append(r.Group == EvalRunIngest.GroupAutoProduct ? "n/a" : r.SingleModel ? "yes" : "no").Append('\n');

                Console.WriteLine(
                    $"{r.TaskId,-5} {r.Group,-2} {r.Rep,1}  {r.Tokens,8}  {r.CostUsd,7:0.0000}  {r.Seconds,6:0.0}  {r.Models}");
            }

            File.WriteAllText(Path.Combine(runDir, "metrics.csv"), csv.ToString(), new UTF8Encoding(true));

            Console.WriteLine();
            Console.WriteLine($"已寫入 {result.Rows.Count} 份產出 → {Path.GetFullPath(outputsDir)}");
            Console.WriteLine("已寫入 metrics.csv（成本為 UsageRecords 的逐筆實際金額加總，非顯示字串）");

            var thin = result.Rows.GroupBy(x => x.TaskId + "-" + x.Group)
                                 .Where(g => g.Count() < 3).Select(g => g.Key).ToList();
            if (thin.Count > 0)
                Console.WriteLine("⚠ 重複次數不足 3 的格子：" + string.Join("、", thin) +
                                  "（主實驗每格要 3 次才估得出組內變異）");

            return result.Warnings.Count == 0 ? 0 : 1;
        }

        // ───────────────────────── blind ─────────────────────────

        private readonly record struct RunFile(string TaskId, string Group, int Rep, string Path);

        /// <summary>檔名 T01-A-1.txt → 題號 T01、組別 A、第 1 次。</summary>
        private static RunFile? ParseName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[2], out int rep)) return null;
            return new RunFile(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), rep, path);
        }

        private static List<RunFile> LoadRunFiles(string runDir)
        {
            string outputs = Path.Combine(runDir, "outputs");
            if (!Directory.Exists(outputs))
                throw new DirectoryNotFoundException("找不到產出資料夾：" + Path.GetFullPath(outputs));

            var files = new List<RunFile>();
            foreach (string f in Directory.GetFiles(outputs, "*.*")
                                          .Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                                   || x.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                                          .OrderBy(x => x, StringComparer.Ordinal))
            {
                var parsed = ParseName(f);
                if (parsed == null)
                    Console.Error.WriteLine($"略過檔名不合規的檔案（應為 題號-組別-次數.txt）：{Path.GetFileName(f)}");
                else
                    files.Add(parsed.Value);
            }

            if (files.Count == 0) throw new InvalidDataException("outputs/ 裡沒有可讀的產出檔");
            return files;
        }

        /// <summary>
        /// 盲評前處理：把產出複製成四位代號，另寫一份對照表。
        /// 評分者只拿到 blind/ 資料夾；key.csv 要等所有評分收齊才打開。
        /// 用固定種子＋run 資料夾名當亂數來源，所以同一個 run 重跑會得到同一組代號（可重現）。
        /// </summary>
        private static int Blind(string runDir)
        {
            if (string.IsNullOrWhiteSpace(runDir)) throw new ArgumentException("請用 --run 指定 run 資料夾");

            var files = LoadRunFiles(runDir);
            string blindDir = Path.Combine(runDir, "blind");
            Directory.CreateDirectory(blindDir);

            var rng = new Random(StableSeed(Path.GetFileName(Path.TrimEndingDirectorySeparator(runDir))));
            var shuffled = files.OrderBy(_ => rng.Next()).ToList();

            var key = new StringBuilder("code,task,group,rep,source_file\n");
            for (int i = 0; i < shuffled.Count; i++)
            {
                string code = "S" + (1000 + i).ToString(CultureInfo.InvariantCulture);
                File.Copy(shuffled[i].Path, Path.Combine(blindDir, code + ".txt"), overwrite: true);
                key.Append(code).Append(',').Append(shuffled[i].TaskId).Append(',')
                   .Append(shuffled[i].Group).Append(',').Append(shuffled[i].Rep).Append(',')
                   .Append(Path.GetFileName(shuffled[i].Path)).Append('\n');
            }

            File.WriteAllText(Path.Combine(blindDir, "key.csv"), key.ToString(), new UTF8Encoding(true));

            // 給評分者的空白表：只有代號，沒有組別。
            var sheet = new StringBuilder("code,事實正確_1to5,來源可查_1to5,指令遵守_1to5,交付品質_1to5,評分者,備註\n");
            foreach (var f in shuffled.Select((x, i) => "S" + (1000 + i)))
                sheet.Append(f).Append(",,,,,,\n");
            File.WriteAllText(Path.Combine(blindDir, "rating_sheet.csv"), sheet.ToString(), new UTF8Encoding(true));

            Console.WriteLine($"已產生 {shuffled.Count} 份盲評檔 → {blindDir}");
            Console.WriteLine("把 blind/ 裡的 S####.txt 與 rating_sheet.csv 交給評分者；key.csv 先不要給。");
            return 0;
        }

        private static int StableSeed(string text)
        {
            int h = 17;
            foreach (char c in text ?? "") h = unchecked(h * 31 + c);
            return h;
        }

        // ───────────────────────── score ─────────────────────────

        private readonly record struct Metrics(string Tokens, string CostUsd, string CostTwd, string Seconds, string Models);

        /// <summary>讀 metrics.csv（題號,組別,次數,tokens,cost_usd,cost_twd,seconds,models）。缺檔不是錯誤。</summary>
        private static Dictionary<string, Metrics> LoadMetrics(string runDir)
        {
            var map = new Dictionary<string, Metrics>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(runDir, "metrics.csv");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("（沒有 metrics.csv，scores.csv 的成本/耗時欄會是空的）");
                return map;
            }

            foreach (string line in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split(',');
                if (c.Length < 4) continue;

                string key = $"{c[0].Trim().ToUpperInvariant()}-{c[1].Trim().ToUpperInvariant()}-{c[2].Trim()}";
                map[key] = new Metrics(
                    At(c, 3), At(c, 4), At(c, 5), At(c, 6), At(c, 7));
            }
            return map;

            static string At(string[] c, int i) => i < c.Length ? c[i].Trim() : "";
        }

        private static int Score(string tasksDir, string runDir)
        {
            if (string.IsNullOrWhiteSpace(runDir)) throw new ArgumentException("請用 --run 指定 run 資料夾");

            var specs = LoadSpecs(tasksDir);
            var files = LoadRunFiles(runDir);
            var metrics = LoadMetrics(runDir);

            var rows = new StringBuilder(
                "task,group,rep,hits,total,hit_rate,critical_ok,critical_missing,forbidden_hits," +
                "missing_ids,tokens,cost_usd,cost_twd,seconds,models\n");

            var byGroup = new Dictionary<string, List<(double HitRate, bool CriticalOk)>>(StringComparer.OrdinalIgnoreCase);
            int missingSpec = 0;

            foreach (var f in files)
            {
                if (!specs.TryGetValue(f.TaskId, out var spec))
                {
                    Console.Error.WriteLine($"{Path.GetFileName(f.Path)}：沒有對應的題目規格 {f.TaskId}，略過");
                    missingSpec++;
                    continue;
                }

                var score = EvalRubric.Score(spec, File.ReadAllText(f.Path));
                var m = metrics.TryGetValue($"{f.TaskId}-{f.Group}-{f.Rep}", out var mm)
                    ? mm : new Metrics("", "", "", "", "");

                rows.Append(f.TaskId).Append(',').Append(f.Group).Append(',').Append(f.Rep).Append(',')
                    .Append(score.RequiredCovered).Append(',').Append(score.RequiredTotal).Append(',')
                    .Append(score.Coverage.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(score.CriticalItemsSatisfied ? "yes" : "no").Append(',')
                    .Append(Quote(string.Join(" ", score.CriticalMissingIds))).Append(',')
                    .Append(Quote(string.Join(" ", score.ForbiddenHitIds))).Append(',')
                    .Append(Quote(string.Join(" ", score.MissingIds))).Append(',')
                    .Append(Quote(m.Tokens)).Append(',').Append(Quote(m.CostUsd)).Append(',')
                    .Append(Quote(m.CostTwd)).Append(',').Append(Quote(m.Seconds)).Append(',')
                    .Append(Quote(m.Models)).Append('\n');

                if (!byGroup.TryGetValue(f.Group, out var list))
                    byGroup[f.Group] = list = new List<(double, bool)>();
                list.Add((score.Coverage, score.CriticalItemsSatisfied));

                Console.WriteLine($"{Path.GetFileName(f.Path),-18} {score.Describe()}");
            }

            string outPath = Path.Combine(runDir, "scores.csv");
            File.WriteAllText(outPath, rows.ToString(), new UTF8Encoding(true));

            Console.WriteLine();
            Console.WriteLine("分組摘要。注意這裡只有「必要字樣有沒有出現」，");
            Console.WriteLine("不是「說得對不對」，也不是「任務有沒有完成」——那兩項一律由人評。");
            Console.WriteLine("組別  份數  平均字樣命中率  必備項齊備率");
            foreach (var g in byGroup.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                double hit = g.Value.Average(x => x.HitRate) * 100;
                double ok = g.Value.Count(x => x.CriticalOk) * 100.0 / g.Value.Count;
                Console.WriteLine($"{g.Key,-5} {g.Value.Count,4}  {hit,13:0.0}%  {ok,11:0.0}%");
            }

            Console.WriteLine();
            Console.WriteLine("→ " + Path.GetFullPath(outPath));
            if (byGroup.Count < 2)
                Console.WriteLine("⚠ 只有一組資料，無法比較——對照實驗至少要兩組同題同日。");

            return missingSpec == 0 ? 0 : 1;
        }

        private static string Quote(string? v)
        {
            string s = v ?? "";
            return s.Contains(',') || s.Contains('"')
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }
    }
}
