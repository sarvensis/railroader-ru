using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RailroaderRu
{
    /// <summary>
    /// Ищет перевод строки: точное совпадение → совпадение без краевых пробелов → шаблоны →
    /// разбор по строкам и rich-text тегам TextMeshPro (каждый кусок переводится отдельно).
    /// Не зависит от Unity, поэтому его можно проверять вне игры.
    /// </summary>
    public sealed class Translator
    {
        private sealed class Template
        {
            public Regex Regex;
            public string Value;
            public int[] GroupForPlaceholder; // индекс плейсхолдера {N} → номер группы в регулярке
            public string Origin;
            public string Literal;            // самый длинный кусок текста шаблона — быстрая проверка до регулярки
        }

        private static readonly Regex PlaceholderInKey = new Regex(@"\{(\d+)(?::([^{}]*))?\}", RegexOptions.Compiled);

        // Число в любом виде: 12, -3.5, 1,234, $1,234, 45%, (1 234,00 ₽)
        // Любой текст, но тег <...> целиком: плейсхолдер не может захватить половину <sprite ...>.
        private const string AnyTextPattern = @"((?:<[^<>]*>|[^<])+?)";

        private const string NumberPattern = @"([-+(]?[$€₽]?\d[\d.,   ]*(?<=\d)\)?%?(?:\s?[$€₽])?)";
        private static readonly Regex PlaceholderInValue = new Regex(@"\{(\d+)(?:\|([^{}]*))?\}", RegexOptions.Compiled);
        private static readonly Regex RichTextTag = new Regex(@"<[^<>]*>", RegexOptions.Compiled);

        private const int MaxCacheSize = 50000;
        private const int MaxDepth = 4;

        private readonly Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<Template> _templates = new List<Template>();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _plurals = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>Русская форма слова для числа (car, 3 → «вагона») или null, если слова нет в словаре.</summary>
        public string Plural(string noun, long number)
        {
            if (noun == null || !_plurals.TryGetValue(noun, out var forms)) return null;
            return SelectPluralForm(number.ToString(CultureInfo.InvariantCulture), forms);
        }

        /// <summary>Вызывается для каждого «атомарного» куска текста, для которого не нашлось перевода.</summary>
        public Action<string> OnUntranslated;
        public Action<string> Warn = _ => { };

        public int ExactCount => _exact.Count;
        public int TemplateCount => _templates.Count;

        public void Add(IEnumerable<TranslationEntry> entries)
        {
            foreach (var e in entries)
            {
                try
                {
                    switch (e.Kind)
                    {
                        case EntryKind.Exact:
                            _exact[e.Key] = e.Value;
                            break;
                        case EntryKind.Template:
                            _templates.Add(CompileTemplate(e));
                            break;
                        case EntryKind.Regex:
                            _templates.Add(CompileRegex(e));
                            break;
                        case EntryKind.Plural:
                            var forms = e.Value.Split('|');
                            if (forms.Length != 3)
                                throw new FormatException("нужно три формы через |, например: вагон|вагона|вагонов");
                            _plurals[e.Key] = forms;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Warn($"{e.Source}:{e.Line}: ошибка в записи «{e.Key}»: {ex.Message}");
                }
            }
            _cache.Clear();
        }

        public void Clear()
        {
            _exact.Clear();
            _templates.Clear();
            _plurals.Clear();
            _cache.Clear();
        }

        public bool TryGetExact(string key, out string value) => _exact.TryGetValue(key, out value);

        /// <summary>Возвращает перевод или исходную строку, если перевода нет.</summary>
        public string Translate(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasLatinLetter(text))
                return text;

            if (_cache.TryGetValue(text, out var cached))
                return cached ?? text;

            string result = TranslateCore(text, 0, report: true);

            if (_cache.Count >= MaxCacheSize)
                _cache.Clear();
            _cache[text] = result;
            return result ?? text;
        }

        /// <summary>Возвращает null, если ничего не удалось перевести.</summary>
        private string TranslateCore(string text, int depth, bool report)
        {
            if (_exact.TryGetValue(text, out var exact))
                return exact;

            // Краевые пробелы и переводы строк сохраняем, ищем по «ядру».
            int start = 0, end = text.Length;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (start == end)
                return null;
            bool trimmed = start > 0 || end < text.Length;
            string core = trimmed ? text.Substring(start, end - start) : text;
            if (trimmed && _exact.TryGetValue(core, out exact))
                return text.Substring(0, start) + exact + text.Substring(end);

            string viaTemplate = ApplyTemplates(core, depth);
            if (viaTemplate != null)
                return trimmed ? text.Substring(0, start) + viaTemplate + text.Substring(end) : viaTemplate;

            if (depth < MaxDepth)
            {
                // Многострочный текст: переводим каждую строку.
                if (core.IndexOf('\n') >= 0)
                {
                    string r = TranslateParts(core, core.Split('\n'), "\n", depth, report);
                    if (r != null) return trimmed ? text.Substring(0, start) + r + text.Substring(end) : r;
                    return null;
                }

                // Rich-text: переводим текст между тегами, сами теги не трогаем.
                if (core.IndexOf('<') >= 0 && RichTextTag.IsMatch(core))
                {
                    string r = TranslateBetweenTags(core, depth, report);
                    if (r != null) return trimmed ? text.Substring(0, start) + r + text.Substring(end) : r;
                    return null;
                }
            }

            if (report && HasLatinLetter(core))
                OnUntranslated?.Invoke(core);
            return null;
        }

        private string TranslateParts(string original, string[] parts, string separator, int depth, bool report)
        {
            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || !HasLatinLetter(parts[i])) continue;
                string t = TranslateCore(parts[i], depth + 1, report);
                if (t != null) { parts[i] = t; any = true; }
            }
            return any ? string.Join(separator, parts) : null;
        }

        private string TranslateBetweenTags(string text, int depth, bool report)
        {
            var sb = new StringBuilder(text.Length + 16);
            bool any = false;
            int pos = 0;
            foreach (Match m in RichTextTag.Matches(text))
            {
                any |= AppendTranslatedSegment(sb, text.Substring(pos, m.Index - pos), depth, report);
                sb.Append(m.Value);
                pos = m.Index + m.Length;
            }
            any |= AppendTranslatedSegment(sb, text.Substring(pos), depth, report);
            return any ? sb.ToString() : null;
        }

        private bool AppendTranslatedSegment(StringBuilder sb, string segment, int depth, bool report)
        {
            if (segment.Length == 0 || !HasLatinLetter(segment))
            {
                sb.Append(segment);
                return false;
            }
            string t = TranslateCore(segment, depth + 1, report);
            sb.Append(t ?? segment);
            return t != null;
        }

        private string ApplyTemplates(string text, int depth)
        {
            for (int i = 0; i < _templates.Count; i++)
            {
                var tpl = _templates[i];
                if (tpl.Literal != null && text.IndexOf(tpl.Literal, StringComparison.Ordinal) < 0)
                    continue;
                var m = tpl.Regex.Match(text);
                if (!m.Success) continue;
                return PlaceholderInValue.Replace(tpl.Value, pm =>
                {
                    int index = int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
                    int group = index < tpl.GroupForPlaceholder.Length ? tpl.GroupForPlaceholder[index] : -1;
                    if (group < 0 || group >= m.Groups.Count)
                        return pm.Value;
                    string captured = m.Groups[group].Value;
                    if (pm.Groups[2].Success)
                        return SelectPluralForm(captured, pm.Groups[2].Value.Split('|'));
                    // Подставленный фрагмент тоже пробуем перевести (например, название груза).
                    if (depth < MaxDepth && HasLatinLetter(captured))
                        return TranslateCore(captured, depth + 1, report: false) ?? captured;
                    return captured;
                });
            }
            return null;
        }

        private static Template CompileTemplate(TranslationEntry e)
        {
            var pattern = new StringBuilder("^");
            var groupMap = new List<int>();
            int pos = 0, groupNumber = 0;
            string longest = "";
            foreach (Match m in PlaceholderInKey.Matches(e.Key))
            {
                string literal = e.Key.Substring(pos, m.Index - pos);
                if (literal.Length > longest.Length) longest = literal;
                pattern.Append(Regex.Escape(literal));
                int index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                while (groupMap.Count <= index) groupMap.Add(-1);
                if (groupMap[index] >= 0)
                {
                    pattern.Append("\\").Append(groupMap[index]); // повтор того же плейсхолдера
                }
                else
                {
                    groupMap[index] = ++groupNumber;
                    pattern.Append(IsNumericFormat(m.Groups[2].Value) ? NumberPattern : AnyTextPattern);
                }
                pos = m.Index + m.Length;
            }
            if (groupNumber == 0)
                throw new FormatException("в шаблоне нет плейсхолдеров {0}, используйте обычную запись");
            string tail = e.Key.Substring(pos);
            if (tail.Length > longest.Length) longest = tail;
            pattern.Append(Regex.Escape(tail)).Append('$');
            return new Template
            {
                Regex = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant),
                Value = e.Value,
                GroupForPlaceholder = groupMap.ToArray(),
                Origin = $"{e.Source}:{e.Line}",
                Literal = longest.Length >= 2 ? longest : null,
            };
        }

        /// <summary>{0:n}, {0:N0}, {1:F1}, {0:C0}, {0:P1}, {0:0.0}, {0:D3} — плейсхолдер совпадает только с числом.</summary>
        private static bool IsNumericFormat(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            char c = char.ToUpperInvariant(spec[0]);
            return c == 'N' || c == 'F' || c == 'D' || c == 'P' || c == 'C' || c == 'G' || c == 'E' || c == '0' || c == '#';
        }

        private static Template CompileRegex(TranslationEntry e)
        {
            var regex = new Regex(e.Key, RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var map = new int[regex.GetGroupNumbers().Length];
            for (int i = 0; i < map.Length; i++) map[i] = i;
            return new Template { Regex = regex, Value = e.Value, GroupForPlaceholder = map, Origin = $"{e.Source}:{e.Line}" };
        }

        /// <summary>Формы: [1 вагон, 2 вагона, 5 вагонов]. Для дробных — вторая форма («1,5 вагона»).</summary>
        public static string SelectPluralForm(string number, string[] forms)
        {
            if (forms.Length == 0) return "";
            if (forms.Length < 3) return forms[0];
            string digits = number.Trim().Replace(",", "").Replace(" ", "").Replace(" ", "");
            if (digits.IndexOf('.') >= 0)
            {
                if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != Math.Floor(d))
                    return forms[1];
                digits = digits.Substring(0, digits.IndexOf('.'));
            }
            if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return forms[2];
            n = Math.Abs(n);
            long mod10 = n % 10, mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return forms[0];
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return forms[1];
            return forms[2];
        }

        public static bool HasLatinLetter(string s)
        {
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    return true;
            }
            return false;
        }
    }
}
