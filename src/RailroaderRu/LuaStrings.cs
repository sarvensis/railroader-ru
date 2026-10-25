using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RailroaderRu
{
    /// <summary>
    /// Минимальный лексер Lua: находит строковые литералы ("...", '...', [[...]], [==[...]==]),
    /// пропуская комментарии. Нужен, чтобы переводить тексты туториала, не трогая код.
    /// </summary>
    public static class LuaStrings
    {
        public struct Literal
        {
            public int Start;     // позиция первого символа литерала (кавычки/скобки)
            public int Length;    // длина литерала вместе с ограничителями
            public bool IsLong;   // [[...]]
            public string Value;  // значение строки (как его увидит Lua)

            /// <summary>Ключ для словаря: у длинных строк убраны отступы, как это делает игра при выводе.</summary>
            public string Key => IsLong ? NormalizeLong(Value) : Value;
        }

        public static List<Literal> Scan(string src)
        {
            var result = new List<Literal>();
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    i += 2;
                    int level = LongBracketLevel(src, i);
                    if (level >= 0)
                    {
                        i = SkipLong(src, i, level, out _);
                    }
                    else
                    {
                        while (i < n && src[i] != '\n') i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int start = i;
                    string value = ReadQuoted(src, ref i);
                    result.Add(new Literal { Start = start, Length = i - start, IsLong = false, Value = value });
                    continue;
                }
                if (c == '[')
                {
                    int level = LongBracketLevel(src, i);
                    if (level >= 0)
                    {
                        int start = i;
                        i = SkipLong(src, i, level, out string value);
                        result.Add(new Literal { Start = start, Length = i - start, IsLong = true, Value = value });
                        continue;
                    }
                }
                i++;
            }
            return result;
        }

        /// <summary>Заменяет литералы, для которых <paramref name="lookup"/> вернул перевод.</summary>
        public static string Replace(string src, Func<Literal, string> lookup, out int replaced)
        {
            replaced = 0;
            var literals = Scan(src);
            var sb = new StringBuilder(src.Length + 1024);
            int pos = 0;
            foreach (var lit in literals)
            {
                string translated = lookup(lit);
                if (translated == null) continue;
                sb.Append(src, pos, lit.Start - pos);
                sb.Append(lit.IsLong ? EncodeLong(translated) : EncodeQuoted(translated));
                pos = lit.Start + lit.Length;
                replaced++;
            }
            sb.Append(src, pos, src.Length - pos);
            return sb.ToString();
        }

        public static string NormalizeLong(string value)
        {
            var lines = value.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                lines[i] = lines[i].Trim();
            return string.Join("\n", lines).Trim('\n');
        }

        private static int LongBracketLevel(string src, int i)
        {
            if (i >= src.Length || src[i] != '[') return -1;
            int j = i + 1, level = 0;
            while (j < src.Length && src[j] == '=') { level++; j++; }
            return j < src.Length && src[j] == '[' ? level : -1;
        }

        private static int SkipLong(string src, int i, int level, out string value)
        {
            int contentStart = i + level + 2;
            // Lua пропускает перевод строки сразу после открывающей скобки.
            if (contentStart < src.Length && src[contentStart] == '\r') contentStart++;
            if (contentStart < src.Length && src[contentStart] == '\n') contentStart++;
            string close = "]" + new string('=', level) + "]";
            int end = src.IndexOf(close, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                value = src.Substring(contentStart);
                return src.Length;
            }
            value = src.Substring(contentStart, end - contentStart);
            return end + close.Length;
        }

        private static string ReadQuoted(string src, ref int i)
        {
            char quote = src[i++];
            var sb = new StringBuilder();
            while (i < src.Length)
            {
                char c = src[i++];
                if (c == quote) break;
                if (c == '\n') break; // незакрытая строка — Lua выдаст ошибку сам
                if (c != '\\' || i >= src.Length) { sb.Append(c); continue; }
                char e = src[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\n': sb.Append('\n'); break;
                    case 'z':
                        while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
                        break;
                    case 'x':
                        if (i + 1 < src.Length && int.TryParse(src.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
                        {
                            sb.Append((char)hex);
                            i += 2;
                        }
                        break;
                    default:
                        if (char.IsDigit(e))
                        {
                            int code = e - '0', k = 1;
                            while (k < 3 && i < src.Length && char.IsDigit(src[i])) { code = code * 10 + (src[i++] - '0'); k++; }
                            sb.Append((char)code);
                        }
                        else sb.Append(e);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string EncodeQuoted(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string EncodeLong(string value)
        {
            int level = 1;
            while (value.Contains("]" + new string('=', level) + "]")) level++;
            string eq = new string('=', level);
            // Перевод строки после "[==[" Lua съест, а количество строк в файле не важно.
            return "[" + eq + "[\n" + value + "]" + eq + "]";
        }
    }
}
