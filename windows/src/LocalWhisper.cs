using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WhisperWin
{
    /// Offline STT via whisper.cpp (whisper-cli.exe) — mirrors WhisperService.swift.
    /// Exe: config path → %USERPROFILE%\.whisper-models\whisper-cli.exe → PATH
    /// Model: best .bin in %USERPROFILE%\.whisper-models (large-v3 > large > medium > small > base > tiny)
    public static class LocalWhisper
    {
        public static string DefaultModelDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".whisper-models"); }
        }

        public static string FindExe(AppConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.LocalWhisperExe) && File.Exists(cfg.LocalWhisperExe))
                return cfg.LocalWhisperExe;

            var inModels = Path.Combine(DefaultModelDir, "whisper-cli.exe");
            if (File.Exists(inModels)) return inModels;

            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "whisper-cli.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        public static string FindModel(AppConfig cfg)
        {
            var dir = string.IsNullOrWhiteSpace(cfg.LocalWhisperModelDir) ? DefaultModelDir : cfg.LocalWhisperModelDir;
            if (!Directory.Exists(dir)) return null;
            string[] bins;
            try { bins = Directory.GetFiles(dir, "*.bin"); }
            catch (Exception ex)
            {
                Log.Error("whisper model scan: " + ex.Message);
                return null;
            }
            if (bins.Length == 0) return null;

            var priority = new[] { "large-v3", "large", "medium", "small", "base", "tiny" };
            foreach (var key in priority)
            {
                var match = bins.FirstOrDefault(b => Path.GetFileName(b).Contains(key));
                if (match != null) return match;
            }
            return bins[0];
        }

        public static Task<string> TranscribeAsync(AppConfig cfg, string wavPath, string language)
        {
            return Task.Run(() =>
            {
                var exe = FindExe(cfg);
                if (exe == null)
                    throw new InvalidOperationException("ไม่พบ whisper-cli.exe (ตั้งค่า path ใน Settings)");
                var model = FindModel(cfg);
                if (model == null)
                    throw new InvalidOperationException("ไม่พบไฟล์โมเดล ggml-*.bin ใน " + DefaultModelDir);

                var args = "-m \"" + model + "\" -f \"" + wavPath + "\" -nt -np";
                // Omitting -l lets whisper.cpp auto-detect; "auto" is not accepted by
                // every Windows build of whisper-cli.
                if (!string.IsNullOrEmpty(language) && language != "auto")
                    args += " -l " + language;

                var workingDirectory = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(workingDirectory)) workingDirectory = Environment.CurrentDirectory;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) throw new InvalidOperationException("เริ่ม whisper-cli.exe ไม่สำเร็จ");
                    // Drain both pipes concurrently so a verbose whisper build cannot deadlock.
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();
                    var stdout = stdoutTask.Result;
                    var stderr = stderrTask.Result;

                    if (!string.IsNullOrWhiteSpace(stderr))
                        Log.Info("whisper stderr: " + SttClient.Truncate(stderr, 500));

                    if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                        throw new InvalidOperationException("whisper.cpp ทำงานไม่สำเร็จ (exit code " + proc.ExitCode + ")");

                    // strip ANSI color codes (ESC[...m) in case whisper outputs color
                    var ansi = (char)27 + "\\[[0-9;]*m";
                    var cleaned = Regex.Replace(stdout, ansi, "");
                    var trimmed = string.Join(" ",
                            cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Where(l => !l.StartsWith("[")))
                        .Trim();
                    return trimmed.Length == 0 ? null : trimmed;
                }
            });
        }
    }
}
