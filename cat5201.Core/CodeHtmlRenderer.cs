using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 程式碼檔的預覽：行號＋基本的語法上色（註解、字串、數字、關鍵字、HTML 標籤）。
    ///
    /// 不載入外部的上色函式庫：預覽要能離線用，而且這只是「看得清楚」，不是編輯器。
    /// 上色規則刻意寬鬆——認不出來的就照原樣顯示，不會因為猜錯而把程式碼弄亂。
    /// HTML 檔本身另外走網頁預覽（直接看執行結果），這裡處理其餘的程式碼檔。
    /// </summary>
    public static class CodeHtmlRenderer
    {
        private enum Family { Hash, CLike, Sql, Lua, Markup, Css, Batch, Plain }

        private sealed record Syntax(Family Family, string Language, bool CaseInsensitive, HashSet<string> Keywords);

        public static string Build(string fileName, string content)
        {
            var syntax = SyntaxFor(fileName);
            string code = (content ?? "").Replace("\r\n", "\n").TrimEnd('\n');
            int lineCount = code.Length == 0 ? 1 : code.Split('\n').Length;

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang='zh-TW'><meta charset='utf-8'><style>")
              .Append("*{box-sizing:border-box}")
              .Append("body{margin:0;padding:24px;background:#ECECE9;font-family:'Microsoft JhengHei',system-ui,sans-serif;color:#1B1B19}")
              .Append(".file{max-width:1100px;margin:0 auto;background:#fff;border:1px solid #DFDFDA;border-radius:6px;overflow:hidden;")
              .Append("box-shadow:0 2px 6px #0000000f}")
              .Append(".head{display:flex;gap:12px;align-items:baseline;padding:10px 16px;border-bottom:1px solid #EDEDE8;background:#F7F7F4}")
              .Append(".name{font:600 13px 'Segoe UI',sans-serif}.meta{font-size:12px;color:#88887F}")
              .Append(".body{display:flex;overflow:auto}")
              .Append("pre{margin:0;padding:12px 0;font:13px/1.6 'Cascadia Mono',Consolas,'Courier New',monospace;tab-size:4}")
              .Append(".gutter{padding:12px 12px 12px 16px;text-align:right;color:#B5B5AE;background:#FAFAF8;border-right:1px solid #EFEFEA;user-select:none}")
              .Append(".code{padding:12px 16px;flex:1;white-space:pre}")
              .Append(".c{color:#6A737D;font-style:italic}.s{color:#0A7D3E}.n{color:#B35900}.k{color:#7A3FC0;font-weight:600}.t{color:#1F5FBF}")
              .Append("</style><body><div class='file'><div class='head'>")
              .Append("<span class='name'>").Append(WebUtility.HtmlEncode(fileName ?? "")).Append("</span>")
              .Append("<span class='meta'>").Append(WebUtility.HtmlEncode(syntax.Language)).Append("・").Append(lineCount).Append(" 行</span>")
              .Append("</div><div class='body'><pre class='gutter'>");

            for (int i = 1; i <= lineCount; i++)
                sb.Append(i).Append('\n');

            sb.Append("</pre><pre class='code'>").Append(Highlight(code, syntax)).Append("</pre></div></div></body></html>");
            return sb.ToString();
        }

        /// <summary>只上色、不加外框（測試與其他地方用）。</summary>
        internal static string Highlight(string code, string fileName) => Highlight(code, SyntaxFor(fileName));

        private static string Highlight(string code, Syntax syntax)
        {
            var sb = new StringBuilder(code.Length * 2);
            int i = 0, n = code.Length;
            bool inTag = false;   // Markup：只有標籤裡面的引號才是字串，內文的 don't 不是

            void Emit(string cls, int start, int end)
            {
                string piece = WebUtility.HtmlEncode(code[start..end]);
                if (cls.Length == 0) sb.Append(piece);
                else sb.Append("<span class='").Append(cls).Append("'>").Append(piece).Append("</span>");
            }

            while (i < n)
            {
                // 區塊註解
                var block = BlockComment(syntax.Family);
                if (block != null && At(code, i, block.Value.Open))
                {
                    int end = code.IndexOf(block.Value.Close, i + block.Value.Open.Length, StringComparison.Ordinal);
                    end = end < 0 ? n : end + block.Value.Close.Length;
                    Emit("c", i, end);
                    i = end;
                    continue;
                }

                // 行註解
                if (IsLineCommentStart(code, i, syntax))
                {
                    int end = code.IndexOf('\n', i);
                    end = end < 0 ? n : end;
                    Emit("c", i, end);
                    i = end;
                    continue;
                }

                char ch = code[i];

                if (syntax.Family == Family.Markup)
                {
                    if (ch == '<' && i + 1 < n && (char.IsLetter(code[i + 1]) || code[i + 1] == '/' || code[i + 1] == '!'))
                    {
                        int j = i + 1;
                        while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] is '/' or '!' or '-' or ':')) j++;
                        Emit("t", i, j);
                        inTag = true;
                        i = j;
                        continue;
                    }
                    if (ch == '>' && inTag) { inTag = false; Emit("t", i, i + 1); i++; continue; }
                }

                // 字串
                bool quotesActive = syntax.Family != Family.Markup || inTag;
                if (quotesActive && (ch == '"' || ch == '\'' || (ch == '`' && syntax.Family == Family.CLike)))
                {
                    int end = ScanString(code, i, ch);
                    Emit("s", i, end);
                    i = end;
                    continue;
                }

                // 數字（不能是識別字的一部分，例如 x1、utf8）
                if (char.IsDigit(ch) && (i == 0 || !IsIdentChar(code[i - 1])))
                {
                    int j = i;
                    while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '.' || code[j] == '_')) j++;
                    Emit("n", i, j);
                    i = j;
                    continue;
                }

                // 識別字／關鍵字
                if (char.IsLetter(ch) || ch == '_' || ch == '$' || ch == '@')
                {
                    int j = i + 1;
                    while (j < n && IsIdentChar(code[j])) j++;
                    string word = code[i..j];
                    bool keyword = syntax.Family != Family.Markup &&
                                   syntax.Keywords.Contains(syntax.CaseInsensitive ? word.ToLowerInvariant() : word);
                    Emit(keyword ? "k" : "", i, j);
                    i = j;
                    continue;
                }

                Emit("", i, i + 1);
                i++;
            }

            return sb.ToString();
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        private static bool At(string s, int i, string token)
            => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

        private static int ScanString(string code, int start, char quote)
        {
            // Python 三引號、JS 反引號可以跨行；其餘字串到行尾為止（沒收尾就不要吞掉下一行）。
            bool triple = quote != '`' && start + 2 < code.Length && code[start + 1] == quote && code[start + 2] == quote;
            if (triple)
            {
                string fence = new(quote, 3);
                int end = code.IndexOf(fence, start + 3, StringComparison.Ordinal);
                return end < 0 ? code.Length : end + 3;
            }

            bool multiline = quote == '`';
            for (int j = start + 1; j < code.Length; j++)
            {
                char c = code[j];
                if (c == '\\') { j++; continue; }
                if (c == quote) return j + 1;
                if (c == '\n' && !multiline) return j;
            }
            return code.Length;
        }

        private static (string Open, string Close)? BlockComment(Family f) => f switch
        {
            Family.CLike or Family.Sql or Family.Css => ("/*", "*/"),
            Family.Markup => ("<!--", "-->"),
            Family.Lua => ("--[[", "]]"),
            _ => null,
        };

        private static bool IsLineCommentStart(string code, int i, Syntax syntax)
        {
            switch (syntax.Family)
            {
                case Family.Hash:
                    return code[i] == '#';
                case Family.CLike:
                    return At(code, i, "//");
                case Family.Sql:
                case Family.Lua:
                    return At(code, i, "--");
                case Family.Batch:
                    {
                        // REM 與 :: 只在行首（前面只有空白）才是註解。
                        int k = i - 1;
                        while (k >= 0 && code[k] is ' ' or '\t') k--;
                        bool lineStart = k < 0 || code[k] == '\n';
                        return lineStart && (At(code, i, "::") ||
                               (i + 3 <= code.Length && code.Substring(i, 3).Equals("rem", StringComparison.OrdinalIgnoreCase) &&
                                (i + 3 == code.Length || char.IsWhiteSpace(code[i + 3]))));
                    }
                default:
                    return false;
            }
        }

        private static Syntax SyntaxFor(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".py" => new(Family.Hash, "Python", false, Python),
                ".rb" => new(Family.Hash, "Ruby", false, Ruby),
                ".sh" => new(Family.Hash, "Shell", false, Shell),
                ".ps1" => new(Family.Hash, "PowerShell", true, PowerShell),
                ".r" => new(Family.Hash, "R", false, R),
                ".yaml" or ".yml" => new(Family.Hash, "YAML", false, Literals),
                ".toml" => new(Family.Hash, "TOML", false, Literals),
                ".js" or ".mjs" or ".cjs" or ".jsx" => new(Family.CLike, "JavaScript", false, JavaScript),
                ".ts" or ".tsx" => new(Family.CLike, "TypeScript", false, JavaScript),
                ".cs" => new(Family.CLike, "C#", false, CSharp),
                ".java" => new(Family.CLike, "Java", false, Java),
                ".kt" => new(Family.CLike, "Kotlin", false, Kotlin),
                ".go" => new(Family.CLike, "Go", false, Go),
                ".rs" => new(Family.CLike, "Rust", false, Rust),
                ".c" or ".h" => new(Family.CLike, "C", false, C),
                ".cpp" or ".hpp" or ".cc" => new(Family.CLike, "C++", false, Cpp),
                ".swift" => new(Family.CLike, "Swift", false, Swift),
                ".php" => new(Family.CLike, "PHP", false, Php),
                ".dart" => new(Family.CLike, "Dart", false, JavaScript),
                ".json" => new(Family.CLike, "JSON", false, Literals),
                ".vue" or ".svelte" => new(Family.Markup, ext == ".vue" ? "Vue" : "Svelte", false, Empty),
                ".css" or ".scss" => new(Family.Css, ext == ".css" ? "CSS" : "SCSS", false, Empty),
                ".sql" => new(Family.Sql, "SQL", true, Sql),
                ".lua" => new(Family.Lua, "Lua", false, Lua),
                ".html" or ".htm" or ".xml" or ".xaml" or ".csproj" => new(Family.Markup, ext.TrimStart('.').ToUpperInvariant(), false, Empty),
                ".bat" or ".cmd" => new(Family.Batch, "Batch", true, Batch),
                _ => new(Family.Plain, ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() : "文字", false, Empty),
            };
        }

        private static HashSet<string> Words(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        private static readonly HashSet<string> Empty = new();
        private static readonly HashSet<string> Literals = Words("true false null");
        private static readonly HashSet<string> Python = Words("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield self match case");
        private static readonly HashSet<string> Ruby = Words("def end if elsif else unless while until for in do return class module require yield begin rescue ensure nil true false self then case when");
        private static readonly HashSet<string> Shell = Words("if then else elif fi for while do done case esac in function return export local echo exit");
        private static readonly HashSet<string> PowerShell = Words("if else elseif foreach for while do switch function param return try catch finally throw begin process end in");
        private static readonly HashSet<string> R = Words("if else for while repeat function return break next TRUE FALSE NULL NA in library");
        private static readonly HashSet<string> JavaScript = Words("async await break case catch class const continue default delete do else export extends false finally for from function if import in instanceof interface let new null of return static super switch this throw true try type typeof undefined var void while yield enum implements private public protected readonly");
        private static readonly HashSet<string> CSharp = Words("abstract as async await base bool break byte case catch char class const continue decimal default delegate do double else enum event explicit false finally float for foreach get if implicit in int interface internal is lock long namespace new null object out override params private protected public readonly record ref return sealed set short static string struct switch this throw true try typeof uint ulong using var virtual void while init required");
        private static readonly HashSet<string> Java = Words("abstract boolean break byte case catch char class continue default do double else enum extends final finally float for if implements import instanceof int interface long new null package private protected public return short static super switch this throw throws true false try void while var record");
        private static readonly HashSet<string> Kotlin = Words("val var fun class object interface if else when for while do return break continue null true false is in as package import private public internal override data sealed");
        private static readonly HashSet<string> Go = Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false");
        private static readonly HashSet<string> Rust = Words("as break const continue crate else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while async await dyn");
        private static readonly HashSet<string> C = Words("auto break case char const continue default do double else enum extern float for goto if int long register return short signed sizeof static struct switch typedef union unsigned void volatile while NULL include define");
        private static readonly HashSet<string> Cpp = Words("auto bool break case catch char class const constexpr continue default delete do double else enum explicit false float for friend if inline int long namespace new nullptr operator private protected public return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void while include define std");
        private static readonly HashSet<string> Swift = Words("class deinit enum extension func import init let protocol struct var break case continue default do else fallthrough for guard if in repeat return switch where while as false is nil self super true try throw");
        private static readonly HashSet<string> Php = Words("abstract and array as break case catch class const continue default do echo else elseif extends false final for foreach function if implements include interface new null private protected public require return static switch this throw true try use var while");
        private static readonly HashSet<string> Sql = new(("select from where and or not insert into values update set delete create table drop alter join left right inner outer on group by order having limit as distinct null is in like between primary key foreign references index view union all case when then else end count sum avg min max").Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> Lua = Words("and break do else elseif end false for function if in local nil not or repeat return then true until while");
        private static readonly HashSet<string> Batch = new(("echo set if else goto call exit for in do not exist errorlevel off on pause setlocal endlocal").Split(' '), StringComparer.Ordinal);
    }
}
