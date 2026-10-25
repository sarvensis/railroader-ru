using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using Game.Scripting.Interactive;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using UI.Guide;
using UnityEngine;

namespace RailroaderRu
{
    /// <summary>
    /// Туториал: при чтении .lua-файлов из StreamingAssets заменяем строковые литералы по словарю
    /// translations/tutorial/*.txt. Если игра обновит текст, непереведённым останется только изменённый абзац.
    /// </summary>
    internal static class LuaHooks
    {
        internal static string ReadAndTranslate(string path)
        {
            string source = File.ReadAllText(path);
            if (!Plugin.TranslateTutorial.Value || !ContentPaths.IsInStreamingAssets(path))
                return source;
            try
            {
                Plugin.ReloadIfRequested();
                string result = LuaStrings.Replace(source, Lookup, out int replaced);
                if (replaced > 0)
                    Plugin.Log.LogInfo($"Lua: {Path.GetFileName(path)} — переведено строк: {replaced}");
                return ApplyFixes(path, result);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Lua: ошибка перевода {path}: {ex}");
                return source;
            }
        }

        /// <summary>
        /// Исправления ошибок самого обучения (не связанных с переводом).
        /// page_context.lua: цель «в процентах» засчитывается при value > 0.9999, а окно рисует галочку и 100%
        /// уже при > 0.999. Если концевой кран остановился, например, на 0.9995, игрок видит выполненную цель,
        /// а кнопка «Далее» так и не появляется. Выравниваем порог с окном.
        /// </summary>
        private static string ApplyFixes(string path, string source)
        {
            if (!string.Equals(Path.GetFileName(path), "page_context.lua", StringComparison.OrdinalIgnoreCase))
                return source;
            const string bug = "value > 0.9999 then";
            if (source.IndexOf(bug, StringComparison.Ordinal) < 0)
                return source; // игра обновилась и, видимо, сама это исправила
            Plugin.Log.LogInfo("Lua: page_context.lua — исправлен порог выполнения целей (0.9999 → 0.999)");
            return source.Replace(bug, "value > 0.999 then");
        }

        private static string Lookup(LuaStrings.Literal literal)
        {
            string key = literal.Key;
            if (key.Length == 0 || !Translator.HasLatinLetter(key))
                return null;
            return Plugin.Lua.TryGetExact(key, out var value) ? value : null;
        }

        /// <summary>Главный файл книги читается через File.ReadAllText в TryLoadFromFile.</summary>
        [HarmonyPatch(typeof(InteractiveBookRunner), "TryLoadFromFile")]
        private static class TryLoadFromFilePatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ContentPaths.ReplaceReadAllText(instructions, AccessTools.Method(typeof(LuaHooks), nameof(ReadAndTranslate)), "InteractiveBookRunner.TryLoadFromFile");
            }
        }

        /// <summary>Модули (require) грузятся через FileSystemScriptLoader.</summary>
        [HarmonyPatch(typeof(FileSystemScriptLoader), nameof(FileSystemScriptLoader.LoadFile))]
        private static class LoadFilePatch
        {
            private static bool Prefix(string file, ref object __result)
            {
                if (!Plugin.TranslateTutorial.Value || !ContentPaths.IsInStreamingAssets(file) || !File.Exists(file))
                    return true;
                __result = ReadAndTranslate(file);
                return false;
            }
        }
    }

    /// <summary>
    /// Справочник (Guide): если в BepInEx/plugins/RailroaderRu/StreamingAssets/Guide/... есть файл с тем же
    /// именем — читаем его вместо оригинала. Якоря ссылок строятся из английских заголовков,
    /// поэтому сопоставляем русские заголовки с оригинальными по порядку.
    /// </summary>
    internal static class GuideHooks
    {
        private static readonly Regex Header = new Regex(@"^#{1,3}[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline);
        private static readonly Dictionary<string, string> HeaderToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static string ReadOverride(string path)
        {
            string original = File.ReadAllText(path);
            if (!Plugin.TranslateGuide.Value)
                return original;
            try
            {
                string overridePath = ContentPaths.GetOverridePath(path);
                if (overridePath == null || !File.Exists(overridePath))
                    return original;

                string translated = File.ReadAllText(overridePath, Encoding.UTF8);
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    MapHeaders(original, translated, overridePath);
                return translated;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Guide: ошибка чтения перевода для {path}: {ex}");
                return original;
            }
        }

        private static void MapHeaders(string original, string translated, string overridePath)
        {
            var en = Header.Matches(original).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
            var ru = Header.Matches(translated).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
            if (en.Count != ru.Count)
                Plugin.Log.LogWarning($"Guide: в {Path.GetFileName(overridePath)} {ru.Count} заголовков, а в оригинале {en.Count} — " +
                                      "ссылки на разделы могут не работать.");
            for (int i = 0; i < Math.Min(en.Count, ru.Count); i++)
                HeaderToOriginal[ru[i]] = en[i];
        }

        [HarmonyPatch(typeof(GuideWindow), "Populate")]
        private static class PopulatePatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ContentPaths.ReplaceReadAllText(instructions, AccessTools.Method(typeof(GuideHooks), nameof(ReadOverride)), "GuideWindow.Populate");
            }
        }

        /// <summary>TextToSlug(«Типы грузов») → TextToSlug(«Freight Types») → «freight-types».</summary>
        [HarmonyPatch(typeof(Markroader.TMPMarkupRenderer), "TextToSlug")]
        private static class TextToSlugPatch
        {
            private static void Prefix(ref string text)
            {
                if (text != null && HeaderToOriginal.TryGetValue(text.Trim(), out var original))
                    text = original;
            }
        }
    }

    internal static class ContentPaths
    {
        private static string _streamingAssets;

        private static string StreamingAssets =>
            _streamingAssets ?? (_streamingAssets = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar);

        public static bool IsInStreamingAssets(string path)
        {
            try { return Path.GetFullPath(path).StartsWith(StreamingAssets, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>StreamingAssets/Guide/Documentation/x.md → plugins/RailroaderRu/StreamingAssets/Guide/Documentation/x.md</summary>
        public static string GetOverridePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(StreamingAssets, StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.Combine(Plugin.OverridesDir, full.Substring(StreamingAssets.Length));
        }

        /// <summary>Заменяет вызовы File.ReadAllText(string) в методе на наш метод с той же сигнатурой.</summary>
        public static IEnumerable<CodeInstruction> ReplaceReadAllText(IEnumerable<CodeInstruction> instructions, MethodInfo replacement, string where)
        {
            var target = AccessTools.Method(typeof(File), nameof(File.ReadAllText), new[] { typeof(string) });
            int count = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodInfo mi && mi == target)
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = replacement;
                    count++;
                }
                yield return ins;
            }
            if (count == 0)
                Plugin.Log.LogWarning($"{where}: не найден вызов File.ReadAllText — возможно, игра обновилась.");
        }
    }
}
