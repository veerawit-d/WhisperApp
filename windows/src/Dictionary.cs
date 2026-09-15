using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace WhisperWin
{
    /// User-maintained find-to-replace rules for recurring STT mistakes.
    /// Rules are read lazily and reloaded only when dictionary.txt changes.
    public static class CorrectionDictionary
    {
        private sealed class Rule
        {
            public readonly string From;
            public readonly string To;
            public readonly bool IsAscii;

            public Rule(string from, string to)
            {
                From = from;
                To = to;
                IsAscii = IsAsciiText(from);
            }
        }

        private static readonly object Gate = new object();
        private static readonly List<Rule> Rules = new List<Rule>();
        private static DateTime LastWriteUtc = DateTime.MinValue;
        private static bool Loaded;

        /// Full path to the user dictionary file.
        public static string FilePath
        {
            get { return Path.Combine(AppConfig.Dir, "dictionary.txt"); }
        }

        /// Applies rules in file order. ASCII terms use case-insensitive word boundaries;
        /// non-ASCII terms use exact substring matching so Thai remains predictable.
        /// Applies all configured replacements in file order.
        public static string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var active = Snapshot();
            var result = text;
            foreach (var rule in active)
            {
                if (rule.IsAscii)
                {
                    var pattern = "\\b" + Regex.Escape(rule.From) + "\\b";
                    result = Regex.Replace(result, pattern,
                        delegate(Match match) { return rule.To; },
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                else
                {
                    result = result.Replace(rule.From, rule.To);
                }
            }
            return result;
        }

        /// Returns the active rules as a compact hint for the LLM correction prompt.
        /// Returns the active rules formatted for an LLM system prompt.
        public static string HintForPrompt
        {
            get
            {
                var active = Snapshot();
                var lines = new List<string>();
                foreach (var rule in active)
                    lines.Add("- " + rule.From + " → " + rule.To);
                return string.Join(Environment.NewLine, lines.ToArray());
            }
        }

        private static List<Rule> Snapshot()
        {
            lock (Gate)
            {
                ReloadIfChanged();
                return new List<Rule>(Rules);
            }
        }

        private static void ReloadIfChanged()
        {
            DateTime mtime = File.Exists(FilePath)
                ? File.GetLastWriteTimeUtc(FilePath)
                : DateTime.MinValue;
            if (Loaded && mtime == LastWriteUtc) return;

            LastWriteUtc = mtime;
            Loaded = true;
            Rules.Clear();

            try
            {
                if (!File.Exists(FilePath)) return;
                var raw = File.ReadAllText(FilePath);
                foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                    var arrow = trimmed.IndexOf("->", StringComparison.Ordinal);
                    if (arrow < 0) continue;

                    var from = trimmed.Substring(0, arrow).Trim();
                    var to = trimmed.Substring(arrow + 2).Trim();
                    if (from.Length > 0 && to.Length > 0)
                        Rules.Add(new Rule(from, to));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Dictionary load: " + ex.Message);
            }
        }

        private static bool IsAsciiText(string value)
        {
            foreach (var c in value)
                if (c > 127) return false;
            return true;
        }
    }
}
