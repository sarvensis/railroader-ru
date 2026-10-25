using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace RailroaderRu
{
    /// <summary>
    /// Шрифты игры динамические, и в Montserrat/LiberationSans кириллица уже есть — их не трогаем.
    /// В LibreBaskerville (заголовки), Inconsolata (консоль) и других её нет: к ним первым в список
    /// fallback ставим подходящий по насыщенности Montserrat самой игры.
    /// Режим BundledFonts вместо этого создаёт шрифты из TTF плагина (экспериментально: в Unity 2022 /
    /// TMP этой игры шрифт, созданный из файла, может рисовать «мусор»).
    /// </summary>
    internal static class FontManager
    {
        private static string _fontsDir;
        private static readonly Dictionary<string, TMP_FontAsset> Created = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> Processed = new HashSet<int>();
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TMP_FontAsset> GameFonts = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private static float _nextGameFontScan;

        public static void Init(string fontsDir)
        {
            _fontsDir = fontsDir;
        }

        private static bool SupportsCyrillic(TMP_FontAsset font) =>
            font.HasCharacter('Ж', false, true) && font.HasCharacter('я', false, true);

        private static readonly List<TMP_FontAsset> Pending = new List<TMP_FontAsset>();
        private static float _nextPendingRetry;
        private static bool _styleSheetFontsLoaded;
        private static readonly System.Text.RegularExpressions.Regex FontTag =
            new System.Text.RegularExpressions.Regex("<font=\"([^\"]+)\"");

        /// <summary>Подключает кириллический fallback к шрифту (один раз на шрифт).</summary>
        public static void Ensure(TMP_FontAsset font)
        {
            if (!Plugin.ReplaceFonts.Value) return;
            if (!_styleSheetFontsLoaded) PreloadStyleSheetFonts();
            if (Pending.Count > 0 && Time.realtimeSinceStartup >= _nextPendingRetry) RetryPending();
            if (font == null) return;
            int id = font.GetInstanceID();
            if (Processed.Contains(id)) return;
            if (font.name.StartsWith("RailroaderRu", StringComparison.Ordinal)) { Processed.Add(id); return; }

            try
            {
                if (SupportsCyrillic(font)) { Processed.Add(id); return; }

                var fallback = Plugin.BundledFonts.Value ? GetOrCreate(PickFile(font.name)) : FindGameFallback(font.name);
                if (fallback == null)
                {
                    // Нужный шрифт игры ещё не загружен — попробуем позже (не все шрифты проходят через Ensure повторно).
                    if (!Pending.Contains(font)) Pending.Add(font);
                    return;
                }
                Processed.Add(id);
                Pending.Remove(font);
                if (fallback == font) return;

                var table = font.fallbackFontAssetTable;
                if (table == null)
                {
                    table = new List<TMP_FontAsset>();
                    font.fallbackFontAssetTable = table;
                }
                if (!table.Contains(fallback))
                {
                    table.Insert(0, fallback);
                    Plugin.Log.LogInfo($"Кириллица для шрифта {font.name}: {fallback.name}");
                    LogAtlasPages(font);
                }
            }
            catch (Exception ex)
            {
                Processed.Add(id);
                Plugin.Log.LogWarning($"Не удалось добавить кириллицу к шрифту {font.name}: {ex.Message}");
            }
        }

        private static void RetryPending()
        {
            _nextPendingRetry = Time.realtimeSinceStartup + 2f;
            foreach (var f in Pending.ToArray())
                if (f == null) Pending.Remove(f);
                else Ensure(f);
        }

        /// <summary>
        /// Заголовки и моноширинный текст задаются стилями: &lt;style="serif"&gt; → &lt;font="LibreBaskerville/…"&gt;.
        /// Такой шрифт TMP грузит по тегу прямо при разборе текста — ни через основной шрифт надписи,
        /// ни через свойство font он к нам не попадает. Загружаем все шрифты из таблицы стилей заранее.
        /// </summary>
        private static void PreloadStyleSheetFonts()
        {
            _styleSheetFontsLoaded = true;
            try
            {
                var sheet = TMP_Settings.defaultStyleSheet;
                if (sheet == null) return;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var styles = HarmonyLib.Traverse.Create(sheet).Field("m_StyleList").GetValue<List<TMP_Style>>() ?? new List<TMP_Style>();
                foreach (var style in styles)
                    foreach (System.Text.RegularExpressions.Match m in FontTag.Matches(style.styleOpeningDefinition ?? ""))
                        names.Add(m.Groups[1].Value);
                foreach (var name in names)
                {
                    TMP_FontAsset font;
                    if (!MaterialReferenceManager.TryGetFontAsset(TMP_TextUtilities.GetSimpleHashCode(name), out font) || font == null)
                        font = Resources.Load<TMP_FontAsset>(TMP_Settings.defaultFontAssetPath + name);
                    if (font == null) { Plugin.Log.LogWarning($"Шрифт из стилей не найден: {name}"); continue; }
                    Plugin.Log.LogInfo($"Шрифт из стилей: {name} → {font.name}");
                    Ensure(font);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Не удалось обработать шрифты из стилей: " + ex.Message);
            }
        }

        /// <summary>Шрифт, загруженный по тегу &lt;font="..."&gt;, регистрируется здесь — подключаем к нему кириллицу.</summary>
        [HarmonyLib.HarmonyPatch(typeof(MaterialReferenceManager), nameof(MaterialReferenceManager.AddFontAsset))]
        private static class AddFontAssetPatch
        {
            private static void Postfix(TMP_FontAsset fontAsset)
            {
                try { Ensure(fontAsset); }
                catch (Exception ex) { Plugin.Log.LogWarning(ex.Message); }
            }
        }

        /// <summary>Montserrat игры с той же насыщенностью, что и исходный шрифт.</summary>
        private static TMP_FontAsset FindGameFallback(string fontName)
        {
            string n = (fontName ?? "").ToLowerInvariant();
            string[] candidates =
                n.Contains("bold") || n.Contains("black") ? new[] { "Montserrat-SemiBold SDF", "Montserrat-Bold SDF", "Montserrat-Medium SDF", "Montserrat-Regular SDF" } :
                n.Contains("medium") ? new[] { "Montserrat-Medium SDF", "Montserrat-Regular SDF" } :
                n.Contains("light") ? new[] { "Montserrat-Light SDF", "Montserrat-Regular SDF" } :
                new[] { "Montserrat-Regular SDF", "Montserrat-Medium SDF" };

            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var name in candidates)
                    if (GameFonts.TryGetValue(name, out var f) && f != null)
                        return CloneForFallback(f);
                if (pass == 0 && !ScanGameFonts()) break;
            }
            foreach (var f in GameFonts.Values)
                if (f != null && f.name.StartsWith("LiberationSans", StringComparison.Ordinal))
                    return CloneForFallback(f);
            return null;
        }

        /// <summary>
        /// Шрифт игры нельзя ставить fallback'ом напрямую. TMP меняет материал (атлас) для символа из fallback
        /// только если этот fallback ≠ основному шрифту надписи (TMP_Text: «m_currentFontAsset != m_fontAsset»).
        /// Заголовки — это &lt;font="LibreBaskerville…"&gt; внутри надписи с основным Montserrat: кириллица
        /// находилась в том же Montserrat, материал не переключался, и буквы брались из атласа Baskerville.
        /// Поэтому создаём отдельную копию из того же TTF игры — у неё свой атлас и свой instanceID.
        /// </summary>
        private static TMP_FontAsset CloneForFallback(TMP_FontAsset gameFont)
        {
            string key = "clone:" + gameFont.name;
            if (Created.TryGetValue(key, out var existing) && existing != null)
                return existing;
            if (gameFont.sourceFontFile == null)
                return null;

            // Один атлас 2048×2048 без второй страницы: глифы со страницы 1+ TMP для fallback-шрифта
            // рисует из первой текстуры (часть букв пропадала). 64 pt с запасом вмещают весь алфавит.
            var clone = TMP_FontAsset.CreateFontAsset(gameFont.sourceFontFile, 64, 6, GlyphRenderMode.SDFAA, 2048, 2048,
                AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: false);
            if (clone == null)
                return null;
            clone.name = "RailroaderRu " + gameFont.name;
            clone.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (clone.material != null)
                clone.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (!clone.TryAddCharacters(PrebakedCharacters, out string missing) && !string.IsNullOrEmpty(missing))
                Plugin.Log.LogWarning($"{clone.name}: в шрифте нет символов «{missing}»");

            Created[key] = clone;
            Plugin.Log.LogInfo($"Создан шрифт с кириллицей: {clone.name} (символов: {clone.characterTable.Count}, " +
                               $"страниц атласа: {clone.atlasTextures?.Length ?? 0})");
            LogAtlasPages(gameFont);
            return clone;
        }

        /// <summary>Для диагностики: сколько страниц атласа у шрифта игры, к которому подключаем fallback.</summary>
        private static void LogAtlasPages(TMP_FontAsset font)
        {
            Plugin.Log.LogInfo($"  шрифт игры {font.name}: символов {font.characterTable.Count}, " +
                               $"страниц атласа {font.atlasTextures?.Length ?? 0}, multiAtlas={font.isMultiAtlasTexturesEnabled}");
        }

        /// <summary>Ищет загруженные шрифты игры с кириллицей (не чаще раза в 2 секунды).</summary>
        private static bool ScanGameFonts()
        {
            if (Time.realtimeSinceStartup < _nextGameFontScan) return false;
            _nextGameFontScan = Time.realtimeSinceStartup + 2f;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null || GameFonts.ContainsKey(f.name) || f.name.StartsWith("RailroaderRu", StringComparison.Ordinal)) continue;
                if (!f.name.StartsWith("Montserrat", StringComparison.Ordinal) && !f.name.StartsWith("LiberationSans", StringComparison.Ordinal)) continue;
                if (SupportsCyrillic(f)) GameFonts[f.name] = f;
            }
            return true;
        }

        /// <summary>Подбирает TTF по имени шрифта игры (Montserrat-SemiBold SDF → Montserrat-SemiBold.ttf).</summary>
        private static string PickFile(string fontName)
        {
            string n = (fontName ?? "").ToLowerInvariant().Replace(" ", "").Replace("-", "");

            if (n.Contains("baskerville"))
                return n.Contains("bold") ? "PTSerif-Bold.ttf" : "PTSerif-Regular.ttf";

            if (n.Contains("inconsolata") || n.Contains("mono"))
                return "PTMono-Regular.ttf";

            // Montserrat и всё остальное (LiberationSans и т.п.).
            bool italic = n.Contains("italic");
            string weight =
                n.Contains("semibold") ? "SemiBold" :
                n.Contains("bold") ? "Bold" :
                n.Contains("medium") ? "Medium" :
                n.Contains("light") ? "Light" :
                "Regular";

            if (italic)
            {
                switch (weight)
                {
                    case "Regular": return "Montserrat-Italic.ttf";
                    case "Medium": return "Montserrat-MediumItalic.ttf";
                    case "Bold":
                    case "SemiBold": return "Montserrat-BoldItalic.ttf";
                    default: return "Montserrat-Italic.ttf";
                }
            }
            return "Montserrat-" + weight + ".ttf";
        }

        private static TMP_FontAsset GetOrCreate(string fileName)
        {
            if (Created.TryGetValue(fileName, out var existing) && existing != null)
                return existing;

            string path = Path.Combine(_fontsDir, fileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning("Нет файла шрифта " + path + ", использую Montserrat-Regular.ttf");
                path = Path.Combine(_fontsDir, "Montserrat-Regular.ttf");
            }
            if (!File.Exists(path))
            {
                // Последний шанс — системный Arial.
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            }
            if (Failed.Contains(path) || !File.Exists(path))
                return null;

            // 90 pt / padding 9 — стандартные параметры TMP для динамических SDF-шрифтов.
            var asset = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024);
            if (asset == null)
            {
                Failed.Add(path);
                Plugin.Log.LogWarning("TextMeshPro не смог загрузить шрифт " + path);
                return null;
            }
            asset.name = "RailroaderRu " + Path.GetFileNameWithoutExtension(path);
            asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (asset.material != null)
                asset.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            SourcePaths[asset.GetInstanceID()] = path;

            // Сразу рисуем в атлас весь русский алфавит, пока загружен именно этот TTF.
            if (!asset.TryAddCharacters(PrebakedCharacters, out string missing) && !string.IsNullOrEmpty(missing))
                Plugin.Log.LogWarning($"{asset.name}: в шрифте нет символов «{missing}»");

            Created[fileName] = asset;
            Plugin.Log.LogInfo("Создан шрифт с кириллицей: " + asset.name);
            return asset;
        }

        private const string PrebakedCharacters =
            "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя«»—–…№×";

        /// <summary>Шрифт → TTF, из которого он создан (для патча LoadFontFace).</summary>
        private static readonly Dictionary<int, string> SourcePaths = new Dictionary<int, string>();

        public static bool TryGetSourcePath(TMP_FontAsset font, out string path)
        {
            path = null;
            return font != null && SourcePaths.TryGetValue(font.GetInstanceID(), out path);
        }

        /// <summary>
        /// TMP для динамического шрифта сначала вызывает FontEngine.LoadFontFace(m_SourceFontFile = null, ...).
        /// Если движок считает это успехом, загруженным остаётся предыдущий шрифт, и глифы рисуются из него
        /// (у разных начертаний Montserrat номера глифов совпадают, а у PT Serif — нет, отсюда «мусор»).
        /// Для наших шрифтов всегда загружаем TTF по пути.
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(TMP_FontAsset), "LoadFontFace")]
        private static class LoadFontFacePatch
        {
            private static readonly HarmonyLib.AccessTools.FieldRef<TMP_FontAsset, string> SourcePathField =
                HarmonyLib.AccessTools.FieldRefAccess<TMP_FontAsset, string>("m_SourceFontFilePath");

            private static bool Prefix(TMP_FontAsset __instance, ref FontEngineError __result)
            {
                if (!TryGetSourcePath(__instance, out var path))
                {
                    // Шрифты игры (LibreBaskerville и др.) — динамические, но TTF из билда вырезан.
                    // «Загрузка» пустого шрифта проходит успешно, и TMP рисует кириллицу из чужого шрифта
                    // в их атлас — на экране куски других букв. Запрещаем: пусть идёт в наш fallback.
                    if (Plugin.ReplaceFonts.Value
                        && __instance.atlasPopulationMode == AtlasPopulationMode.Dynamic
                        && __instance.sourceFontFile == null
                        && string.IsNullOrEmpty(SourcePathField(__instance)))
                    {
                        __result = FontEngineError.Invalid_File;
                        return false;
                    }
                    return true;
                }
                var face = __instance.faceInfo;
                __result = FontEngine.LoadFontFace(path, (int)face.pointSize, 0); // наши шрифты всегда создаются с faceIndex = 0
                return false;
            }
        }
    }
}
