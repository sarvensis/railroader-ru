using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RailroaderRu
{
    /// <summary>
    /// Пишет встреченные в игре непереведённые строки в файл в формате словаря («Original=»),
    /// чтобы их можно было сразу дописать в translations/*.txt.
    /// Строки, отличающиеся только числами, записываются один раз.
    /// </summary>
    internal static class UntranslatedLog
    {
        private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
        private static string _path;
        private static StreamWriter _writer;
        private static bool _failed;

        public static void Init(string path)
        {
            _path = path;
        }

        public static void Record(string text)
        {
            if (_failed || text.Length > 4000) return;
            try
            {
                if (_writer == null)
                {
                    if (File.Exists(_path))
                    {
                        // Ключи уже записанных строк (с переводом или без).
                        foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                        {
                            int sep = line.Length;
                            for (int i = 0; i < line.Length; i++)
                            {
                                if (line[i] == '\\') { i++; continue; }
                                if (line[i] == '=') { sep = i; break; }
                            }
                            Seen.Add(Digits.Replace(TranslationFile.Unescape(line.Substring(0, sep)), "#"));
                        }
                    }
                    _writer = new StreamWriter(_path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                if (!Seen.Add(Digits.Replace(text, "#"))) return;
                _writer.WriteLine(TranslationFile.Escape(text) + "=");
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogWarning("Не удалось записать untranslated.txt: " + ex.Message);
            }
        }
    }
}
