using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RailroaderRu
{
    /// <summary>
    /// Формат файлов перевода (UTF-8):
    ///
    ///   // комментарий
    ///   Original=Перевод
    ///   t:Loaded {0} of {1}=Загружено {0} из {1}        шаблон с подстановками
    ///   t:{0} cars={0} {0|вагон|вагона|вагонов}          русские формы множественного числа
    ///   r:^Day (\d+)$=День {1}                            регулярное выражение
    ///   p:car=вагон|вагона|вагонов                        склонение слов для Pluralize() игры (1 / 2 / 5)
    ///   {0:n} в шаблоне совпадает только с числом (так же {0:N0}, {0:F1}, {0:C0} и т.п.)
    ///   &lt;&lt;&lt;
    ///   многострочный
    ///   оригинал
    ///   ===
    ///   многострочный
    ///   перевод
    ///   &gt;&gt;&gt;
    ///
    /// В однострочных записях: \n — перевод строки, \t — табуляция, \= — знак равенства, \\ — обратный слэш.
    /// Записи с пустым переводом игнорируются (так выглядят ещё не переведённые строки).
    /// </summary>
    public sealed class TranslationEntry
    {
        public string Key;
        public string Value;
        public EntryKind Kind;
        public string Source;
        public int Line;
    }

    public enum EntryKind
    {
        Exact,
        Template,
        Regex,
        Plural,
    }

    public static class TranslationFile
    {
        public static Action<string> Warn = _ => { };

        public static List<TranslationEntry> Load(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path));
        }

        public static List<TranslationEntry> Parse(string content, string source)
        {
            var result = new List<TranslationEntry>();
            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i == 0 && line.Length > 0 && line[0] == '﻿')
                    line = line.Substring(1);

                if (line.TrimEnd() == "<<<")
                {
                    int startLine = i + 1;
                    var key = new List<string>();
                    var value = new List<string>();
                    bool inValue = false, closed = false;
                    for (i++; i < lines.Length; i++)
                    {
                        string t = lines[i].TrimEnd();
                        if (t == ">>>") { closed = true; break; }
                        if (t == "===" && !inValue) { inValue = true; continue; }
                        (inValue ? value : key).Add(t);
                    }
                    if (!closed)
                    {
                        Warn($"{source}:{startLine}: блок <<< не закрыт >>>");
                        break;
                    }
                    string k = string.Join("\n", key).Trim('\n');
                    string v = string.Join("\n", value).Trim('\n');
                    if (k.Length > 0 && v.Length > 0)
                        result.Add(new TranslationEntry { Key = k, Value = v, Kind = EntryKind.Exact, Source = source, Line = startLine });
                    continue;
                }

                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                var kind = EntryKind.Exact;
                if (line.StartsWith("t:", StringComparison.Ordinal)) { kind = EntryKind.Template; line = line.Substring(2); }
                else if (line.StartsWith("r:", StringComparison.Ordinal)) { kind = EntryKind.Regex; line = line.Substring(2); }
                else if (line.StartsWith("p:", StringComparison.Ordinal)) { kind = EntryKind.Plural; line = line.Substring(2); }

                int sep = FindSeparator(line);
                if (sep <= 0)
                {
                    if (line.Trim().Length > 0)
                        Warn($"{source}:{i + 1}: нет разделителя '=' — строка пропущена");
                    continue;
                }

                string rawKey = line.Substring(0, sep);
                string rawValue = line.Substring(sep + 1);
                if (rawValue.Length == 0)
                    continue;

                // В регулярках экранирование оставляем как есть (кроме \=), чтобы не ломать \d, \s и т.п.
                string keyText = kind == EntryKind.Regex ? rawKey.Replace("\\=", "=") : Unescape(rawKey);
                result.Add(new TranslationEntry
                {
                    Key = keyText,
                    Value = Unescape(rawValue),
                    Kind = kind,
                    Source = source,
                    Line = i + 1,
                });
            }
            return result;
        }

        private static int FindSeparator(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '=') return i;
            }
            return -1;
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '=': sb.Append('='); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append('\\').Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '=': sb.Append("\\="); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
