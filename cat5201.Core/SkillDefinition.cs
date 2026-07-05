using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace test
{
    /// <summary>
    /// §19 Skills 注入：使用者自訂的可重複使用「技能」（指示/流程/格式模板）。
    /// 存進全域個人化 _preferences.json（跨專案、重啟不丟、永不被系統覆蓋），執行時注入 prompt。
    /// </summary>
    public sealed class SkillDefinition
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Skills 的執行期單一真相：MainWindow 於載入/變更偏好時餵入，
    /// NodePromptBuilder 組 prompt 時取用。static 快照模式與 AiAutoCostPolicy 同一慣例（單一視窗下安全）。
    /// </summary>
    public static class SkillsRegistry
    {
        private static IReadOnlyList<SkillDefinition> _skills = Array.Empty<SkillDefinition>();

        public static IReadOnlyList<SkillDefinition> GetAll() => _skills;

        public static void SetAll(IEnumerable<SkillDefinition>? skills)
        {
            _skills = (skills ?? Enumerable.Empty<SkillDefinition>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Content))
                .ToList();
        }

        /// <summary>啟用中的技能組成 prompt 區塊；沒有啟用中的技能回空字串。</summary>
        public static string BuildPromptBlock()
        {
            var active = _skills.Where(s => s.Enabled).ToList();
            if (active.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("【使用者技能（個人化注入，全域生效）】");
            sb.AppendLine("以下是使用者自訂的可重複使用指示/能力，執行任務時一律遵循；優先級與全域偏好同級——若與上游鏈設定或本節點的明確指示衝突，以後者為準。");
            foreach (var s in active)
            {
                string name = string.IsNullOrWhiteSpace(s.Name) ? "(未命名技能)" : s.Name.Trim();
                sb.Append("◆ ").Append(name).AppendLine("：").AppendLine(s.Content.Trim());
            }
            return sb.ToString().TrimEnd() + "\n\n";
        }
    }
}
