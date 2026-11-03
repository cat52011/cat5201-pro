using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-03 機器學習作業實測：1,700 行的 Python 程式交到使用者手上時已經不能跑。
    /// - 空行後第一行的縮排全部消失（271 行全中）——續寫去重複時每段 Trim；
    /// - 內容相同的程式碼區塊第二個整個不見——去重複連程式碼也比；
    /// - 「STUDENT_NAME = "請填寫姓名"」被切成兩行——續寫接縫硬塞換行；
    /// - split()[0]、data[[TARGET]] 被刪成 split()、data[]——引用記號的規則套到程式碼上。
    /// 這些步驟對每一個答案都會跑，不只是這份作業。
    /// </summary>
    public class LongCodeAnswerTests
    {
        private readonly NodeTextProcessingService _text = new();

        private const string Python =
            "```python\n" +
            "def read_csv_auto_encoding(file_path):\n" +
            "    encodings = [\"utf-8-sig\", \"cp950\"]\n" +
            "\n" +
            "    for encoding in encodings:\n" +
            "        try:\n" +
            "            return pd.read_csv(file_path, encoding=encoding)\n" +
            "        except UnicodeDecodeError:\n" +
            "            continue\n" +
            "\n" +
            "    raise UnicodeDecodeError(\"unknown\", b\"\", 0, 1, \"無法判斷 CSV 編碼\")\n" +
            "```";

        [Fact]
        public void RepeatedBlockCleanup_KeepsIndentationAfterBlankLines()
        {
            string answer = "程式如下：\n\n" + Python + "\n\n執行後會印出結果。";

            string cleaned = _text.RemoveRepeatedBlocks(answer);

            Assert.Contains("\n\n    for encoding in encodings:\n", cleaned);
            Assert.Contains("\n\n    raise UnicodeDecodeError(", cleaned);
            Assert.Equal(answer, cleaned);   // 沒有重複的東西時，什麼都不該變
        }

        [Fact]
        public void RepeatedBlockCleanup_NeverDropsCode_ButStillDropsLongRepeatedProse()
        {
            string files = "```text\n學號_姓名_HW1作業檔案.docx\n學號_姓名_HW1_Regression_Analysis.py\n```";
            string longProse = "程式會依序產生缺失值圖、總價分布圖、行政區箱型圖與相關係數熱圖，並輸出敘述統計表格。";

            string answer = "並將檔案名稱整理為：\n\n" + files + "\n\n" + longProse + "\n\n" +
                            "最終應上傳：\n\n" + files + "\n\n" + longProse + "\n\n執行：\n\n執行：";

            string cleaned = _text.RemoveRepeatedBlocks(answer);

            Assert.Equal(2, cleaned.Split("```text").Length - 1);       // 兩個相同的程式碼區塊都在
            Assert.Single(cleaned.Split('\n'), l => l == longProse);     // 重講的長段落只留一次
            Assert.Equal(2, cleaned.Split("執行：").Length - 1);          // 短的正常重複不刪
        }

        [Fact]
        public void Continuation_JoinsMidLine_WithoutInsertingALineBreak()
        {
            string existing = "```python\nDEPARTMENT = \"資科系\"\nSTUDENT_NAME = ";
            string reply = "\"請填寫姓名\"\nprint(STUDENT_NAME)\n```";

            string joined = existing + _text.JoinContinuation(existing, reply);

            Assert.Contains("STUDENT_NAME = \"請填寫姓名\"\n", joined);
        }

        [Fact]
        public void Continuation_KeepsLeadingIndentation()
        {
            string existing = "```python\ndef f():\n    x = 1\n";
            string reply = "\n    for i in range(3):\n        print(i)\n```";

            string joined = existing + _text.JoinContinuation(existing, reply);

            Assert.Contains("\n    for i in range(3):\n        print(i)", joined);
        }

        [Fact]
        public void Continuation_InsideACodeBlock_DropsTheReopenedFence()
        {
            string existing = "```python\nfor axis, (label, pred) in zip(";
            string reply = "```python\naxes, predictions):\n    axis.plot(pred)\n```";

            string joined = existing + _text.JoinContinuation(existing, reply);

            Assert.Equal(2, joined.Split("```").Length - 1);   // 只有一開一關
            Assert.Contains("axes, predictions):", joined);
        }

        [Fact]
        public void Continuation_ACoincidentalOneCharacterMatch_IsNotTreatedAsOverlap()
        {
            string existing = "print(round(x, 2)";
            string reply = ")\nprint(\"done\")";

            Assert.StartsWith(")", _text.JoinContinuation(existing, reply));
        }

        [Fact]
        public void Sanitizer_LeavesCodeAlone_ButStillRemovesCitationsInProse()
        {
            string answer = "根據報告[1]，模型表現穩定[2]。\n\n" +
                            "```python\nfirst = text.split()[0]\nX = data[[TARGET]]\nrow = items[i]\n\n    indented = True\n```\n\n" +
                            "用 `values[0]` 取第一筆。";

            string cleaned = FinalAnswerSanitizer.Sanitize(answer, enforceSynthesisFormat: false);

            Assert.Contains("text.split()[0]", cleaned);
            Assert.Contains("data[[TARGET]]", cleaned);
            Assert.Contains("\n\n    indented = True", cleaned);
            Assert.Contains("`values[0]`", cleaned);
            Assert.DoesNotContain("[1]", cleaned.Split("```")[0]);   // 文字裡的引用記號照樣清掉
            Assert.Contains("根據報告，模型表現穩定。", cleaned);
        }

        [Fact]
        public void HeadingLabelWithPrefix_NamesTheFile()
        {
            string answer = "## 程式碼檔案：`HW1_Regression_Analysis.py`\n\n```python\nimport pandas as pd\nprint(1)\n```";

            var r = CodeFileExtractor.Extract(answer);

            Assert.Equal("HW1_Regression_Analysis.py", Assert.Single(r.Files).FileName);
        }

        [Fact]
        public void AnswerThatDeliversCode_IsRecognized_EvenIfTheRequestDidNotSayCode()
        {
            string longCode = "```python\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"x{i} = {i}")) + "\n```";
            string snippet = "```python\nx = 1\nprint(x)\n```";

            Assert.True(CodeFileExtractor.LooksLikeCodeDeliverable("說明\n\n" + longCode));
            Assert.True(CodeFileExtractor.LooksLikeCodeDeliverable("## 程式碼：`hw.py`\n\n```python\nimport os\nprint(os.name)\n```"));
            Assert.False(CodeFileExtractor.LooksLikeCodeDeliverable("例如：\n\n" + snippet));
            Assert.False(CodeFileExtractor.WantsCodeFiles("完成此作業並說明", NodeTaskMode.Research));   // 要求本身確實沒寫
        }
    }
}
