using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhisperWin
{
    /// System-tray application shell: icon, menu, hotkey wiring, settings window.
    public class TrayContext : ApplicationContext
    {
        private readonly AppConfig _cfg;
        private readonly Overlay _overlay;
        private readonly DictationController _controller;
        private readonly HotkeyManager _hotkey;
        private readonly NotifyIcon _tray;
        private readonly SynchronizationContext _ui;

        private readonly Icon _iconIdle = TrayIcons.Make(Color.White);
        private readonly Icon _iconRec = TrayIcons.Make(Color.FromArgb(255, 82, 82));
        private readonly Icon _iconBusy = TrayIcons.Make(Color.FromArgb(255, 170, 60));

        private ToolStripMenuItem _miToggle;
        private ToolStripMenuItem _miCloud;
        private ToolStripMenuItem _miCorrection;
        private ToolStripMenuItem _miAutostart;
        private readonly List<ToolStripMenuItem> _languageItems = new List<ToolStripMenuItem>();
        private SettingsForm _settings;
        private DictionaryForm _dictionary;
        private bool _syncingMenu;

        public TrayContext()
        {
            _cfg = AppConfig.Load();
            _overlay = new Overlay(); // creating the first Form installs the WinForms SynchronizationContext
            _ui = SynchronizationContext.Current;

            _controller = new DictationController(_cfg, _overlay);
            _controller.StageChanged += OnStageChanged;

            _hotkey = new HotkeyManager();
            ApplyHotkeySettings();
            // hook callbacks run inside message dispatch — post so the hook returns instantly
            _hotkey.OnKeyDown = delegate
            {
                _ui.Post(delegate { if (_cfg.HotkeyHoldMode) _controller.Start(); else _controller.Toggle(); }, null);
            };
            _hotkey.OnKeyUp = delegate
            {
                _ui.Post(delegate { _controller.StopAndProcess(); }, null);
            };
            _hotkey.Install();

            _tray = new NotifyIcon
            {
                Icon = _iconIdle,
                Visible = true,
                ContextMenuStrip = BuildMenu(),
            };
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _controller.Toggle();
            };
            UpdateTexts();

            Log.Info("WhisperApp started (hotkey: " + _hotkey.DisplayString + ")");

            var needsSetup = _cfg.UseCloudStt
                ? !_cfg.SttConfigured()
                : LocalWhisper.FindExe(_cfg) == null || LocalWhisper.FindModel(_cfg) == null;
            if (needsSetup)
            {
                _tray.ShowBalloonTip(6000, "WhisperApp",
                    _cfg.UseCloudStt
                        ? "ยังไม่ได้ตั้งค่า API key — เปิดหน้าตั้งค่าเพื่อเริ่มใช้งาน"
                        : "ยังไม่พบ whisper.cpp/model — เปิดหน้าตั้งค่าเพื่อดูวิธีตั้งค่า",
                    ToolTipIcon.Info);
                OpenSettings();
            }
        }

        private void ApplyHotkeySettings()
        {
            _hotkey.Vk = _cfg.HotkeyVk;
            _hotkey.Ctrl = _cfg.HotkeyCtrl;
            _hotkey.Alt = _cfg.HotkeyAlt;
            _hotkey.Shift = _cfg.HotkeyShift;
            _hotkey.HoldMode = _cfg.HotkeyHoldMode;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var header = new ToolStripMenuItem("WhisperApp — พูดแล้วพิมพ์ให้อัตโนมัติ") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            _miToggle = new ToolStripMenuItem("เริ่มพูด");
            _miToggle.Click += delegate { _controller.Toggle(); };
            menu.Items.Add(_miToggle);
            menu.Items.Add(new ToolStripSeparator());

            _miCloud = new ToolStripMenuItem("ใช้ STT บน cloud") { CheckOnClick = true };
            _miCloud.CheckedChanged += delegate
            {
                if (_syncingMenu) return;
                _cfg.UseCloudStt = _miCloud.Checked;
                _cfg.Save();
            };
            menu.Items.Add(_miCloud);

            _miCorrection = new ToolStripMenuItem("เกลาข้อความด้วย AI") { CheckOnClick = true };
            _miCorrection.CheckedChanged += delegate
            {
                if (_syncingMenu) return;
                _cfg.UseCorrection = _miCorrection.Checked;
                _cfg.Save();
            };
            menu.Items.Add(_miCorrection);
            menu.Items.Add(new ToolStripSeparator());

            var lang = new ToolStripMenuItem("ภาษา");
            AddLang(lang, Languages.Auto);
            lang.DropDownItems.Add(new ToolStripSeparator());
            foreach (var language in Languages.All) AddLang(lang, language);
            menu.Items.Add(lang);
            menu.Items.Add(new ToolStripSeparator());

            var settings = new ToolStripMenuItem("ตั้งค่า…");
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            var updates = new ToolStripMenuItem("ตรวจสอบเวอร์ชันใหม่…");
            updates.Click += delegate { OpenUrl("https://github.com/Gamezxz/WhisperApp/releases/latest"); };
            menu.Items.Add(updates);

            var whatsNew = new ToolStripMenuItem("What's New…");
            whatsNew.Click += delegate { OpenUrl("https://gamezxz.github.io/WhisperApp/changelog"); };
            menu.Items.Add(whatsNew);

            var dictionary = new ToolStripMenuItem("พจนานุกรมส่วนตัว…");
            dictionary.Click += delegate { OpenDictionary(); };
            menu.Items.Add(dictionary);

            _miAutostart = new ToolStripMenuItem("เริ่มพร้อม Windows") { CheckOnClick = true };
            _miAutostart.CheckedChanged += delegate { Autostart.Set(_miAutostart.Checked); };
            menu.Items.Add(_miAutostart);

            var openDir = new ToolStripMenuItem("เปิดโฟลเดอร์ข้อมูล (config/log)");
            openDir.Click += delegate
            {
                try
                {
                    System.IO.Directory.CreateDirectory(AppConfig.Dir);
                    Process.Start("explorer.exe", AppConfig.Dir);
                }
                catch { }
            };
            menu.Items.Add(openDir);
            menu.Items.Add(new ToolStripSeparator());

            var about = new ToolStripMenuItem("เกี่ยวกับ WhisperApp");
            about.Click += delegate
            {
                MessageBox.Show(
                    "WhisperApp for Windows\n\n" +
                    "พูดผ่านไมค์ → ถอดเสียงเป็นข้อความ → AI เกลาให้ → พิมพ์ลงแอปที่ใช้อยู่อัตโนมัติ\n" +
                    "(port มาจากเวอร์ชัน macOS: github.com/Gamezxz/WhisperApp)\n\n" +
                    "ปุ่มลัดปัจจุบัน: " + _hotkey.DisplayString +
                    (_cfg.HotkeyHoldMode ? " (กดค้างเพื่อพูด)" : " (กดเริ่ม/หยุด)"),
                    "WhisperApp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(about);

            var exit = new ToolStripMenuItem("ออก");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            return menu;
        }

        private void AddLang(ToolStripMenuItem parent, Language language)
        {
            var item = new ToolStripMenuItem(language.Name) { Tag = language.Code };
            item.Click += delegate
            {
                _cfg.Language = (string)item.Tag;
                _cfg.Save();
                UpdateTexts();
            };
            parent.DropDownItems.Add(item);
            _languageItems.Add(item);
        }

        private void OpenSettings()
        {
            if (_settings != null && !_settings.IsDisposed)
            {
                _settings.Activate();
                return;
            }
            _settings = new SettingsForm(_cfg);
            _settings.Saved += delegate
            {
                ApplyHotkeySettings();
                UpdateTexts();
            };
            _settings.Show();
            _settings.Activate();
        }

        private void OpenDictionary()
        {
            if (_dictionary != null && !_dictionary.IsDisposed)
            {
                _dictionary.Activate();
                return;
            }
            _dictionary = new DictionaryForm();
            _dictionary.Show();
            _dictionary.Activate();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Error("Open URL: " + ex.Message); }
        }

        private void OnStageChanged(Stage stage)
        {
            switch (stage)
            {
                case Stage.Recording:
                    _tray.Icon = _iconRec;
                    _miToggle.Text = "หยุดพูดแล้วพิมพ์ (" + _hotkey.DisplayString + ")";
                    break;
                case Stage.Transcribing:
                case Stage.Correcting:
                    _tray.Icon = _iconBusy;
                    break;
                default:
                    _tray.Icon = _iconIdle;
                    _miToggle.Text = "เริ่มพูด (" + _hotkey.DisplayString + ")";
                    break;
            }
        }

        private void UpdateTexts()
        {
            _miToggle.Text = "เริ่มพูด (" + _hotkey.DisplayString + ")";
            _syncingMenu = true;
            try
            {
                _miCloud.Checked = _cfg.UseCloudStt;
                _miCorrection.Checked = _cfg.UseCorrection;
                _miAutostart.Checked = Autostart.IsEnabled();
                foreach (var item in _languageItems)
                    item.Checked = (string)item.Tag == _cfg.Language;
            }
            finally { _syncingMenu = false; }

            var tip = "WhisperApp — กด " + _hotkey.DisplayString + (_cfg.HotkeyHoldMode ? " ค้างเพื่อพูด" : " เพื่อเริ่ม/หยุด");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private void ExitApp()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _hotkey.Dispose();
            _controller.Dispose();
            _overlay.Dispose();
            _iconIdle.Dispose();
            _iconRec.Dispose();
            _iconBusy.Dispose();
            ExitThread();
        }
    }

    /// Runtime-drawn microphone tray icons (no .ico asset needed).
    public static class TrayIcons
    {
        public static Icon Make(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var b = new SolidBrush(color))
                using (var pen = new Pen(color, 3f))
                {
                    // mic capsule
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(11, 3, 10, 10, 180, 180);
                        path.AddArc(11, 10, 10, 10, 0, 180);
                        path.CloseFigure();
                        g.FillPath(b, path);
                    }
                    // pickup arc
                    g.DrawArc(pen, 7, 8, 18, 16, 20, 140);
                    // stem + base
                    g.DrawLine(pen, 16, 24, 16, 28);
                    g.DrawLine(pen, 10, 29, 22, 29);
                }

                var hIcon = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally { DestroyIcon(hIcon); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }

    /// Start-with-Windows via HKCU Run key.
    public static class Autostart
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string Name = "WhisperApp";

        public static bool IsEnabled()
        {
            try
            {
                using (var rk = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return rk != null && rk.GetValue(Name) != null;
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var rk = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (rk == null) return;
                    if (on) rk.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                    else rk.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Log.Error("Autostart: " + ex.Message); }
        }
    }
}
