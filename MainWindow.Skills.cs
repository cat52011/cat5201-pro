using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace Cat5201
{
    // MainWindow 部分類別 — §19 Skills 技能(新增/編輯/GitHub 匯入)
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        // ===== §19 技能注入：設定面板「技能」區的新增/開關/刪除；存個人化、餵 SkillsRegistry =====

        // 就地編輯：點清單裡的技能名稱 → 帶回輸入框，按鈕變「儲存修改」。
        private SkillDefinition? _editingSkill;

        private void AddSkill_Click(object sender, RoutedEventArgs e)
        {
            string name = (SkillNameInput?.Text ?? "").Trim();
            string content = (SkillContentInput?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(content))
                return; // 內容是技能的本體，沒內容不收

            var list = new List<SkillDefinition>(SkillsRegistry.GetAll());
            if (_editingSkill != null && list.Contains(_editingSkill))
            {
                _editingSkill.Name = name;      // 就地更新，保留啟用狀態與排序
                _editingSkill.Content = content;
            }
            else
            {
                list.Add(new SkillDefinition { Name = name, Content = content, Enabled = true });
            }
            SkillsRegistry.SetAll(list);
            SavePreferences();
            ExitSkillEditMode();
            RefreshSkillsList();
        }

        private void EnterSkillEditMode(SkillDefinition skill)
        {
            _editingSkill = skill;
            if (SkillNameInput != null) SkillNameInput.Text = skill.Name;
            if (SkillContentInput != null) SkillContentInput.Text = skill.Content;
            if (AddSkillButton != null) AddSkillButton.Content = "✓ 儲存修改";
            if (SkillImportHint != null)
            {
                SkillImportHint.Text = $"編輯中：「{(string.IsNullOrWhiteSpace(skill.Name) ? "(未命名)" : skill.Name)}」——改完按「儲存修改」。";
                SkillImportHint.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x54, 0xFF));
            }
        }

        private void ExitSkillEditMode()
        {
            _editingSkill = null;
            if (SkillNameInput != null) SkillNameInput.Text = "";
            if (SkillContentInput != null) SkillContentInput.Text = "";
            if (AddSkillButton != null) AddSkillButton.Content = "＋ 新增技能";
            if (SkillImportHint != null) SkillImportHint.Text = "";
        }

        // §19：從 GitHub 匯入技能檔（SKILL.md / 任意 .md / 純文字）。blob 連結自動轉 raw。
        private static readonly HttpClient _skillImportHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

        private async void ImportSkillFromGitHub_Click(object sender, RoutedEventArgs e)
        {
            string url = (SkillImportUrlInput?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return;

            void Hint(string msg, bool error = false)
            {
                if (SkillImportHint == null) return;
                SkillImportHint.Text = msg;
                SkillImportHint.Foreground = new SolidColorBrush(
                    error ? Color.FromRgb(0xC0, 0x3A, 0x4B) : Color.FromRgb(0x2E, 0x7D, 0x4F));
            }

            try
            {
                // github.com/{owner}/{repo}/blob/{branch}/{path} → raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
                string fetchUrl = url;
                var m = Regex.Match(url, @"^https://github\.com/([^/]+)/([^/]+)/blob/(.+)$");
                if (m.Success)
                    fetchUrl = $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}";

                Hint("下載中…");
                string text = await _skillImportHttp.GetStringAsync(fetchUrl);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Hint("檔案是空的。", error: true);
                    return;
                }
                if (text.Length > 20_000)
                    text = text[..20_000]; // 防超大檔把 prompt 撐爆

                // 解析 SKILL.md 式 frontmatter（--- name: … ---）；沒有就用檔名當名稱、全文當內容。
                string name = "";
                string content = text.Trim();
                var fm = Regex.Match(text, @"^\s*---\s*\r?\n(.*?)\r?\n---\s*\r?\n?", RegexOptions.Singleline);
                if (fm.Success)
                {
                    var nameMatch = Regex.Match(fm.Groups[1].Value, @"(?m)^name\s*:\s*(.+)$");
                    if (nameMatch.Success)
                        name = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    content = text[(fm.Index + fm.Length)..].Trim();
                    if (content.Length == 0)
                        content = text.Trim(); // frontmatter-only 的怪檔：退回全文
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = System.IO.Path.GetFileNameWithoutExtension(new Uri(fetchUrl).AbsolutePath); }
                    catch { name = "匯入的技能"; }
                }

                var list = new List<SkillDefinition>(SkillsRegistry.GetAll())
                {
                    new SkillDefinition { Name = name, Content = content, Enabled = true }
                };
                SkillsRegistry.SetAll(list);
                SavePreferences();
                RefreshSkillsList();
                if (SkillImportUrlInput != null) SkillImportUrlInput.Text = "";
                Hint($"✓ 已匯入「{name}」（{content.Length} 字）");
                AppLog.Info("Skills", $"GitHub 匯入技能：{name} ← {fetchUrl}");
            }
            catch (Exception ex)
            {
                Hint("匯入失敗：" + ex.Message, error: true);
                AppLog.Warn("Skills", "GitHub 匯入失敗：" + url, ex);
            }
        }

        // 以程式生成列（與記憶清單同慣例），避免 DataTemplate 綁定複雜度。
        private void RefreshSkillsList()
        {
            if (SkillsListPanel == null)
                return;

            SkillsListPanel.Children.Clear();
            var skills = SkillsRegistry.GetAll();

            foreach (var skill in skills)
            {
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xFB)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var s = skill; // 閉包固定

                var toggle = new CheckBox
                {
                    IsChecked = s.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "啟用/停用此技能（停用＝保留但不注入）",
                };
                toggle.Checked += (_, __) => { s.Enabled = true; SavePreferences(); };
                toggle.Unchecked += (_, __) => { s.Enabled = false; SavePreferences(); };
                Grid.SetColumn(toggle, 0);

                var nameText = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(s.Name) ? "(未命名技能)" : s.Name,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = s.Content + "\n（點一下編輯）",
                    Cursor = Cursors.Hand,
                };
                nameText.MouseLeftButtonUp += (_, __) => EnterSkillEditMode(s); // 就地編輯
                Grid.SetColumn(nameText, 1);

                var del = new Button
                {
                    Content = "✕",
                    FontSize = 12,
                    Width = 24,
                    Height = 24,
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9E)),
                    BorderThickness = new Thickness(0),
                    ToolTip = "刪除此技能",
                };
                del.Click += (_, __) =>
                {
                    var remaining = new List<SkillDefinition>(SkillsRegistry.GetAll());
                    remaining.Remove(s);
                    SkillsRegistry.SetAll(remaining);
                    SavePreferences();
                    if (ReferenceEquals(_editingSkill, s)) ExitSkillEditMode(); // 刪掉正在編輯的 → 退出編輯
                    RefreshSkillsList();
                };
                Grid.SetColumn(del, 2);

                grid.Children.Add(toggle);
                grid.Children.Add(nameText);
                grid.Children.Add(del);
                row.Child = grid;
                SkillsListPanel.Children.Add(row);
            }
        }
    }
}
