using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WhisperWin
{
    /// Small editor for the persistent STT correction dictionary.
    public class DictionaryForm : Form
    {
        private sealed class Rule
        {
            public string From;
            public string To;
        }

        private readonly List<Rule> _rules = new List<Rule>();
        private TextBox _txtFrom;
        private TextBox _txtTo;
        private ListBox _list;
        private Button _btnRemove;
        private Label _message;

        /// Creates the dictionary editor window.
        public DictionaryForm()
        {
            Text = "WhisperApp — พจนานุกรมส่วนตัว";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 470);

            BuildUi();
            LoadRules();
        }

        private void BuildUi()
        {
            var title = new Label
            {
                Text = "แก้คำที่ STT มักฟังผิดก่อนนำไปพิมพ์",
                Left = 18, Top = 16, Width = 510, Height = 24,
                Font = new Font(Font, FontStyle.Bold),
            };
            Controls.Add(title);

            var help = new Label
            {
                Text = "เพิ่มกฎแบบ ผิด → ถูก และมีผลทันที แม้ปิด AI correction ก็ยังใช้กฎนี้",
                Left = 18, Top = 43, Width = 510, Height = 34,
                ForeColor = Color.DimGray,
            };
            Controls.Add(help);

            _txtFrom = new TextBox { Left = 18, Top = 86, Width = 190 };
            _txtTo = new TextBox { Left = 238, Top = 86, Width = 190 };
            var arrow = new Label { Text = "→", Left = 214, Top = 89, Width = 20, TextAlign = ContentAlignment.MiddleCenter };
            var add = new Button { Text = "เพิ่ม", Left = 440, Top = 84, Width = 94, Height = 28 };
            add.Click += delegate { AddRule(); };
            _txtFrom.KeyDown += OnEntryKeyDown;
            _txtTo.KeyDown += OnEntryKeyDown;
            Controls.AddRange(new Control[] { _txtFrom, arrow, _txtTo, add });

            _list = new ListBox { Left = 18, Top = 130, Width = 516, Height = 230, HorizontalScrollbar = true };
            _list.SelectedIndexChanged += delegate { _btnRemove.Enabled = _list.SelectedIndex >= 0; };
            Controls.Add(_list);

            _btnRemove = new Button { Text = "ลบรายการที่เลือก", Left = 18, Top = 372, Width = 140, Height = 28, Enabled = false };
            _btnRemove.Click += delegate { RemoveRule(); };
            var open = new Button { Text = "เปิดไฟล์ dictionary.txt", Left = 374, Top = 372, Width = 160, Height = 28 };
            open.Click += delegate { OpenFile(); };
            Controls.AddRange(new Control[] { _btnRemove, open });

            _message = new Label { Left = 18, Top = 414, Width = 516, Height = 24, ForeColor = Color.DarkGreen };
            Controls.Add(_message);

            AcceptButton = add;
        }

        private void OnEntryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddRule();
                e.SuppressKeyPress = true;
            }
        }

        private void LoadRules()
        {
            _rules.Clear();
            try
            {
                if (File.Exists(CorrectionDictionary.FilePath))
                {
                    foreach (var line in File.ReadAllLines(CorrectionDictionary.FilePath))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                        var arrow = trimmed.IndexOf("->", StringComparison.Ordinal);
                        if (arrow < 0) continue;
                        var from = trimmed.Substring(0, arrow).Trim();
                        var to = trimmed.Substring(arrow + 2).Trim();
                        if (from.Length > 0 && to.Length > 0)
                            _rules.Add(new Rule { From = from, To = to });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Dictionary editor load: " + ex.Message);
                ShowMessage("อ่านไฟล์ dictionary ไม่สำเร็จ", true);
            }
            RefreshList();
        }

        private void AddRule()
        {
            var from = (_txtFrom.Text ?? "").Trim();
            var to = (_txtTo.Text ?? "").Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                ShowMessage("กรอกทั้งคำที่ฟังผิดและคำที่ต้องการแทน", true);
                return;
            }

            _rules.Add(new Rule { From = from, To = to });
            _txtFrom.Clear();
            _txtTo.Clear();
            SaveRules();
            RefreshList();
            ShowMessage("เพิ่มกฎแล้ว", false);
        }

        private void RemoveRule()
        {
            var index = _list.SelectedIndex;
            if (index < 0 || index >= _rules.Count) return;
            _rules.RemoveAt(index);
            SaveRules();
            RefreshList();
            ShowMessage("ลบกฎแล้ว", false);
        }

        private void RefreshList()
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var rule in _rules)
                    _list.Items.Add(rule.From + "  →  " + rule.To);
            }
            finally { _list.EndUpdate(); }
            _btnRemove.Enabled = _list.SelectedIndex >= 0;
        }

        private void SaveRules()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Dir);
                var lines = new List<string>();
                foreach (var rule in _rules)
                    lines.Add(rule.From + " -> " + rule.To);
                File.WriteAllText(CorrectionDictionary.FilePath, string.Join(Environment.NewLine, lines.ToArray()));
                File.SetLastWriteTimeUtc(CorrectionDictionary.FilePath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Error("Dictionary editor save: " + ex.Message);
                ShowMessage("บันทึก dictionary ไม่สำเร็จ", true);
            }
        }

        private void OpenFile()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Dir);
                if (!File.Exists(CorrectionDictionary.FilePath)) File.WriteAllText(CorrectionDictionary.FilePath, "");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + CorrectionDictionary.FilePath + "\"",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Log.Error("Open dictionary: " + ex.Message);
                ShowMessage("เปิดไฟล์ไม่สำเร็จ", true);
            }
        }

        private void ShowMessage(string message, bool error)
        {
            _message.Text = message;
            _message.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
        }
    }
}
