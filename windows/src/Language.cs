using System.Collections.Generic;

namespace WhisperWin
{
    /// A language supported by cloud STT, local whisper.cpp and LLM correction.
    public sealed class Language
    {
        public readonly string Code;
        public readonly string Name;
        public readonly string Iso3;

        /// Creates a language entry with ISO 639-1 and ISO 639-3 codes.
        public Language(string code, string name, string iso3)
        {
            Code = code;
            Name = name;
            Iso3 = iso3;
        }

        public override string ToString() { return Name; }
    }

    /// Language registry kept in sync with the macOS version.
    public static class Languages
    {
        public static readonly Language Auto = new Language("auto", "ตรวจอัตโนมัติ", "");

        public static readonly List<Language> All = new List<Language>
        {
            new Language("en", "English", "eng"),
            new Language("th", "ไทย", "tha"),
            new Language("zh", "Chinese", "zho"),
            new Language("ja", "Japanese", "jpn"),
            new Language("ko", "Korean", "kor"),
            new Language("vi", "Vietnamese", "vie"),
            new Language("id", "Indonesian", "ind"),
            new Language("ms", "Malay", "msa"),
            new Language("es", "Spanish", "spa"),
            new Language("fr", "French", "fra"),
            new Language("de", "German", "deu"),
            new Language("it", "Italian", "ita"),
            new Language("pt", "Portuguese", "por"),
            new Language("ru", "Russian", "rus"),
            new Language("ar", "Arabic", "ara"),
            new Language("hi", "Hindi", "hin"),
        };

        /// Finds a language by its application code, including the auto-detect entry.
        public static Language Find(string code)
        {
            if (code == "auto") return Auto;
            foreach (var language in All)
                if (language.Code == code) return language;
            return null;
        }

        /// Returns a display name, or the original code when it is unknown.
        public static string NameOf(string code)
        {
            var language = Find(code);
            return language == null ? code : language.Name;
        }
    }
}
