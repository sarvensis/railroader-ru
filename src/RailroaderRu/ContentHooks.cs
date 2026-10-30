using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

using HarmonyLib;

using UnityEngine;

namespace RailroaderRu
{
    /// <summary>Классы игры ищем по имени: в 2025.1 они в Assembly-CSharp, в 2026.1 — в Railroader и Relay.</summary>
    internal static class GameTypes
    {
        private static readonly string[] Assemblies = { "Railroader", "Assembly-CSharp", "Relay", "MoonSharp.Interpreter" };

        public static Type Find(params string[] names)
        {
            foreach (var assemblyName in Assemblies)
            {
                Assembly assembly;
                try { assembly = Assembly.Load(assemblyName); }
                catch (Exception) { continue; }
                foreach (var name in names)
                {
                    var type = assembly.GetType(name, false);
                    if (type != null) return type;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Туториал: при чтении скриптов (.rl в 2026.1, .lua в 2025.1) из StreamingAssets заменяем строковые
    /// литералы по словарю translations/tutorial/*.txt. Если игра обновит текст, непереведённым останется
    /// только изменённый абзац.
    /// </summary>
    internal static class LuaHooks
    {
        internal static string ReadAndTranslate(string path)
        {
            string source = File.ReadAllText(path);
            if (!Plugin.TranslateTutorial.Value || !ContentPaths.IsInStreamingAssets(path))
                return source;
            return Translate(path, source);
        }

        private static string Translate(string path, string source)
        {
            try
            {
                Plugin.ReloadIfRequested();
                bool relay = path.EndsWith(".rl", StringComparison.OrdinalIgnoreCase);
                string result = relay
                    ? RelayStrings.Replace(source, Lookup, out int replaced)
                    : LuaStrings.Replace(source, Lookup, out replaced);
                if (replaced > 0)
                    Plugin.Log.LogInfo($"Обучение: {Path.GetFileName(path)} — переведено строк: {replaced}");
                return ApplyFixes(path, result);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Обучение: ошибка перевода {path}: {ex}");
                return source;
            }
        }

        /// <summary>
        /// Исправления ошибок самого обучения (не связанных с переводом).
        /// Цель «в процентах» засчитывается при value > 0.9999, а окно рисует галочку и 100% уже при > 0.999.
        /// Если концевой кран остановился, например, на 0.9995, игрок видит выполненную цель,
        /// а кнопка «Далее» так и не появляется. Выравниваем порог с окном.
        /// </summary>
        private static string ApplyFixes(string path, string source)
        {
            string file = Path.GetFileName(path);
            bool isPageContext = string.Equals(file, "page_context.lua", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(file, "pagecontext.rl", StringComparison.OrdinalIgnoreCase);
            if (!isPageContext || source.IndexOf("value > 0.9999", StringComparison.Ordinal) < 0)
                return source; // игра обновилась и, видимо, сама это исправила
            Plugin.Log.LogInfo($"Обучение: {file} — исправлен порог выполнения целей (0.9999 → 0.999)");
            return source.Replace("value > 0.9999", "value > 0.999");
        }

        private static string Lookup(LuaStrings.Literal literal)
        {
            string key = literal.Key;
            if (key.Length == 0 || !Translator.HasLatinLetter(key))
                return null;
            return Plugin.Lua.TryGetExact(key, out var value) ? value : null;
        }

        /// <summary>Главный файл книги читается через File.ReadAllText в TryLoadFromFile.</summary>
        [HarmonyPatch]
        private static class TryLoadFromFilePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(
                GameTypes.Find("Game.Scripting.Interactive.RelayBookRunner", "Game.Scripting.Interactive.InteractiveBookRunner"),
                "TryLoadFromFile");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ContentPaths.ReplaceReadAllText(instructions, AccessTools.Method(typeof(LuaHooks), nameof(ReadAndTranslate)), "TryLoadFromFile");
            }
        }

        /// <summary>
        /// Модули Relay (from x import y), 2026.1. Подменяем уже прочитанный текст перед компиляцией.
        /// Переписывать FileSystemModuleLoader.LoadModuleSource нельзя: после такого патча он возвращал null,
        /// и обучение падало с «Module 'bookbuilder' not found». displayName здесь — полный путь к файлу.
        /// </summary>
        [HarmonyPatch]
        private static class RelayModulePatch
        {
            private static bool Prepare() => GameTypes.Find("Relay.Interpreter") != null;

            private static MethodBase TargetMethod() => AccessTools.Method(GameTypes.Find("Relay.Interpreter"), "CompileSourceModuleFunction");

            private static void Prefix(ref string sourceCode, string displayName)
            {
                if (Plugin.TranslateTutorial.Value && displayName != null
                    && displayName.EndsWith(".rl", StringComparison.OrdinalIgnoreCase)
                    && ContentPaths.IsInStreamingAssets(displayName))
                    sourceCode = Translate(displayName, sourceCode);
            }
        }

        /// <summary>Модули Lua (require), 2025.1.</summary>
        [HarmonyPatch]
        private static class LuaModulePatch
        {
            private static bool Prepare() => GameTypes.Find("MoonSharp.Interpreter.Loaders.FileSystemScriptLoader") != null;

            private static MethodBase TargetMethod() => AccessTools.Method(GameTypes.Find("MoonSharp.Interpreter.Loaders.FileSystemScriptLoader"), "LoadFile");

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

        [HarmonyPatch]
        private static class PopulatePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(GameTypes.Find("UI.Guide.GuideWindow"), "Populate");

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
