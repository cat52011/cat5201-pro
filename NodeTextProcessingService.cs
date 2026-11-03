using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cat5201
{
    public sealed class NodeTextProcessingService
    {
        public bool HasEndMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("[[END_OF_RESPONSE]]", StringComparison.Ordinal);
        }

        /// <summary>
        /// 拿掉結束記號。只修掉尾端空白——開頭的換行與縮排是續寫內容的一部分。
        /// 2026-11-03：原本整段 Trim，續寫從「\n    for …」開始時，那一行的縮排就被吃掉。
        /// </summary>
        public string RemoveEndMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            return text.Replace("[[END_OF_RESPONSE]]", "").TrimEnd();
        }

        /// <summary>重疊至少這麼長才算「模型把前面的內容重講了一次」；一兩個字元相同只是巧合（例如兩邊都是右括號）。</summary>
        internal const int MinOverlapChars = 16;

        public string RemoveLeadingOverlap(string existing, string incoming)
        {
            existing ??= "";
            incoming ??= "";

            if (string.IsNullOrWhiteSpace(existing))
                return incoming;

            if (string.IsNullOrWhiteSpace(incoming))
                return "";

            int max = Math.Min(existing.Length, incoming.Length);
            int best = 0;

            for (int len = MinOverlapChars; len <= max; len++)
            {
                var a = existing.Substring(existing.Length - len, len);
                var b = incoming.Substring(0, len);

                if (string.Equals(a, b, StringComparison.Ordinal))
                    best = len;
            }

            return best > 0
                ? incoming.Substring(best)
                : incoming;
        }

        /// <summary>
        /// 把下一輪續寫接到已有的內容後面，回傳要附加的那一段（不需要附加時回空字串）。
        ///
        /// 2026-11-03 機器學習作業實測：答案太長分了好幾輪，接縫處被硬塞一個換行，
        /// 「STUDENT_NAME = "請填寫姓名"」被切成兩行；模型下一輪看到自己斷掉的程式，還在答案裡提醒使用者自己修。
        /// 模型被要求「從上一行未完成處繼續」，上一輪多半停在行中間（停在剛好換行的機率很低），所以直接接上。
        /// 上一輪停在程式碼區塊裡、這一輪又重開一個 ``` 時，把那個多出來的開頭拿掉，不然區塊會錯位。
        /// </summary>
        public string JoinContinuation(string existing, string reply)
        {
            existing ??= "";
            string append = RemoveLeadingOverlap(existing, RemoveEndMarker(reply ?? ""));

            if (string.IsNullOrWhiteSpace(append))
                return "";

            if (InsideCodeFence(existing))
            {
                string trimmed = append.TrimStart('\r', '\n');
                int firstBreak = trimmed.IndexOf('\n');
                string firstLine = firstBreak < 0 ? trimmed : trimmed[..firstBreak];
                if (firstLine.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
                    firstLine.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    append = firstBreak < 0 ? "" : "\n" + trimmed[(firstBreak + 1)..];
                }
            }

            // 只有夠長的段落才檢查「整段都已經出現過」——短短一行（例如「執行：」）重複是正常的。
            if (NormalizeForComparison(append).Length >= MinRepeatCheckChars &&
                NormalizeForComparison(existing).Contains(NormalizeForComparison(append), StringComparison.Ordinal))
                return "";

            return append.TrimEnd();
        }

        /// <summary>目前的文字是不是停在一個還沒關閉的程式碼區塊裡。</summary>
        internal static bool InsideCodeFence(string text)
        {
            bool inside = false;
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>段落至少這麼長（去掉空白後）才拿來比對重複。</summary>
        internal const int MinRepeatCheckChars = 40;

        /// <summary>
        /// 拿掉續寫時重講的段落。
        ///
        /// 2026-11-03 機器學習作業實測，舊版造成三種損壞：
        /// 1. 依空行切段後每段 Trim——空行後第一行的縮排全部消失，Python 程式直接不能跑（那份作業 271 行全中）；
        /// 2. 程式碼區塊也拿去比對重複——內容相同的區塊（例如兩次列出同樣的檔名）第二個整個不見；
        /// 3. 很短的段落也比對——「執行：」這種正常的重複也被刪。
        /// 而且這一步對每一個答案都跑，不只是分好幾輪的長答案。
        ///
        /// 現在：程式碼區塊原樣保留、不比對；一般段落保留開頭縮排（清單的續行要靠它），只修掉尾端空白；
        /// 夠長的段落才比對重複。
        /// </summary>
        public string RemoveRepeatedBlocks(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var result = new List<string>();
            var seenProse = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (block, isCode) in SplitBlocks(text))
            {
                if (!isCode)
                {
                    string key = NormalizeForComparison(block);
                    if (key.Length >= MinRepeatCheckChars && !seenProse.Add(key))
                        continue;
                }

                result.Add(block);
            }

            return string.Join("\n\n", result).Trim('\n', '\r');
        }

        /// <summary>依空行切段，但程式碼區塊（``` 或 ~~~ 之間）整塊算一段，裡面的空行不切。</summary>
        internal static IEnumerable<(string Block, bool IsCode)> SplitBlocks(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var current = new List<string>();
            bool inFence = false;
            string fence = "";

            IEnumerable<(string, bool)> Flush(bool isCode)
            {
                // 去掉段落前後的空行，但不動每一行開頭的空白。
                while (current.Count > 0 && current[0].Trim().Length == 0) current.RemoveAt(0);
                while (current.Count > 0 && current[^1].Trim().Length == 0) current.RemoveAt(current.Count - 1);
                if (current.Count > 0)
                    yield return (string.Join("\n", current.Select(l => l.TrimEnd())), isCode);
                current.Clear();
            }

            foreach (string line in lines)
            {
                string t = line.TrimStart();
                bool isFenceLine = t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);

                if (!inFence && isFenceLine)
                {
                    foreach (var b in Flush(false)) yield return b;
                    inFence = true;
                    fence = t.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                    current.Add(line);
                    continue;
                }

                if (inFence)
                {
                    // 程式碼區塊內：原樣保留（行尾空白也不動，Markdown 與部分語言會用到）。
                    current.Add(line);
                    if (isFenceLine && t.Trim().All(c => c == fence[0]) && t.Trim().Length >= 3)
                    {
                        inFence = false;
                        var block = string.Join("\n", current);
                        current.Clear();
                        yield return (block, true);
                    }
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    foreach (var b in Flush(false)) yield return b;
                    continue;
                }

                current.Add(line);
            }

            // 沒收尾的程式碼區塊（答案被截斷）也照原樣交出去。
            if (inFence && current.Count > 0)
            {
                yield return (string.Join("\n", current), true);
                current.Clear();
            }
            foreach (var b in Flush(false)) yield return b;
        }

        public bool IsHighlySimilarByContainment(string a, string b)
        {
            a = NormalizeForComparison(a);
            b = NormalizeForComparison(b);

            if (a.Length < MinRepeatCheckChars || b.Length < MinRepeatCheckChars)
                return false;

            return a.Contains(b, StringComparison.Ordinal) ||
                   b.Contains(a, StringComparison.Ordinal);
        }

        public bool SegmentLooksDuplicate(StringBuilder existingBuilder, string candidate)
        {
            string existing = NormalizeForComparison(existingBuilder?.ToString() ?? "");
            string incoming = NormalizeForComparison(candidate ?? "");

            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(incoming))
                return false;

            if (existing.Contains(incoming, StringComparison.Ordinal))
                return true;

            if (incoming.Length >= 30)
            {
                int partial = Math.Min(incoming.Length, 120);
                string head = incoming.Substring(0, partial);
                if (existing.Contains(head, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string NormalizeForComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var sb = new StringBuilder(text.Length);

            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }
    }
}