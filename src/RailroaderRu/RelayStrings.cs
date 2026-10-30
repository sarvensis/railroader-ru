using System;
using System.Collections.Generic;
using System.Text;

namespace RailroaderRu
{
    /// <summary>
    /// Минимальный лексер языка Relay (скрипты .rl, Railroader 2026+): находит строковые литералы
    /// "...", '...', """...""" и '''...''', пропуская комментарии //.
    /// Интерполяция \(выражение) остаётся в значении как есть — её должен сохранить и перевод.
    /// </summary>
    public static class RelayStrings
    {
        public static List<LuaStrings.Literal> Scan(string src)
        {
            var result = new List<LuaStrings.Literal>();
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int start = i;
                    bool multiline = i + 2 < n && src[i + 1] == c && src[i + 2] == c;
                    string value = multiline ? ReadMultiline(src, ref i, c) : ReadSingle(src, ref i, c);
                    result.Add(new LuaStrings.Literal { Start = start, Length = i - start, IsLong = multiline, Value = value });
                    continue;
                }
                i++;
            }
            return result;
        }

        /// <summary>Заменяет литералы, для которых <paramref name="lookup"/> вернул перевод.</summary>
        public static string Replace(string src, Func<LuaStrings.Literal, string> lookup, out int replaced)
        {
            replaced = 0;
            var sb = new StringBuilder(src.Length + 1024);
            int pos = 0;
            foreach (var lit in Scan(src))
            {
                string translated = lookup(lit);
                if (translated == null) continue;
                sb.Append(src, pos, lit.Start - pos);
                sb.Append(lit.IsLong ? EncodeMultiline(translated) : EncodeSingle(translated));
                pos = lit.Start + lit.Length;
                replaced++;
            }
            sb.Append(src, pos, src.Length - pos);
            return sb.ToString();
        }

        private static string ReadSingle(string src, ref int i, char quote)
        {
            i++;
            var sb = new StringBuilder();
            while (i < src.Length && src[i] != quote)
                ReadChar(src, ref i, sb);
            i++; // закрывающая кавычка
            return sb.ToString();
        }

        private static string ReadMultiline(string src, ref int i, char quote)
        {
            i += 3;
            if (i < src.Length && src[i] == '\r') i++;
            if (i < src.Length && src[i] == '\n') i++;
            var sb = new StringBuilder();
            while (i < src.Length)
            {
                if (src[i] == quote && i + 2 < src.Length && src[i + 1] == quote && src[i + 2] == quote)
                {
                    i += 3;
                    return sb.ToString();
                }
                ReadChar(src, ref i, sb);
            }
            return sb.ToString();
        }

        private static void ReadChar(string src, ref int i, StringBuilder sb)
        {
            char c = src[i++];
            if (c != '\\' || i >= src.Length)
            {
                sb.Append(c);
                return;
            }
            char e = src[i];
            if (e == '(')
            {
                int start = i - 1;
                i++;
                SkipInterpolation(src, ref i);
                sb.Append(src, start, i - start);
                return;
            }
            i++;
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\n': break; // продолжение строки в многострочном литерале
                default: sb.Append(e); break;
            }
        }

        /// <summary>Пропускает выражение \( ... ) со вложенными скобками и строками.</summary>
        private static void SkipInterpolation(string src, ref int i)
        {
            int depth = 1;
            while (i < src.Length && depth > 0)
            {
                char c = src[i++];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '"' || c == '\'')
                {
                    while (i < src.Length && src[i] != c)
                    {
                        if (src[i] == '\\') i++;
                        i++;
                    }
                    i++;
                }
            }
        }

        private static string EncodeSingle(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            Encode(value, sb, multiline: false);
            return sb.Append('"').ToString();
        }

        /// <summary>Закрывающие кавычки в начале строки: отступ снимать не с чего, текст остаётся как есть.</summary>
        private static string EncodeMultiline(string value)
        {
            var sb = new StringBuilder(value.Length + 8).Append("\"\"\"\n");
            Encode(value, sb, multiline: true);
            return sb.Append("\n\"\"\"").ToString();
        }

        private static void Encode(string value, StringBuilder sb, bool multiline)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length && value[i + 1] == '(')
                {
                    int start = i;
                    i += 2;
                    SkipInterpolation(value, ref i);
                    sb.Append(value, start, i - start);
                    i--;
                    continue;
                }
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append(multiline ? "\n" : "\\n"); break;
                    default: sb.Append(c); break;
                }
            }
        }
    }
}
