using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;

namespace RailroaderRu
{
    /// <summary>
    /// Перехватывает весь текст, который попадает в TextMeshPro: сеттер text, SetText(...)
    /// и OnEnable (для надписей, заданных прямо в сценах/префабах).
    /// </summary>
    internal static class TextHooks
    {
        private static readonly Dictionary<int, bool> SkipCache = new Dictionary<int, bool>();
        private static bool _inHook;

        public static string Process(TMP_Text instance, string value)
        {
            if (_inHook || string.IsNullOrEmpty(value) || !Plugin.TranslateUi.Value)
                return value;

            _inHook = true;
            try
            {
                Plugin.ReloadIfRequested();
                if (instance != null)
                {
                    FontManager.Ensure(instance.font);
                    if (IsUserInput(instance))
                        return value;
                }
                string result = Plugin.Ui.Translate(value);
                if (instance != null && Plugin.AutoSizeButtons.Value)
                    FitButtonLabel(instance, value, result);
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ошибка перевода: " + ex);
                return value;
            }
            finally
            {
                _inHook = false;
            }
        }

        private const float MinButtonScale = 0.65f;
        private static readonly Dictionary<int, bool> IsButtonLabel = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> BaseFontSize = new Dictionary<int, float>();

        /// <summary>
        /// Русский текст обычно длиннее английского. Ширину кнопок игра часто считает по ширине надписи,
        /// и кнопки начинают наезжать друг на друга (автоподбор TMP тут не помогает — он не влияет на layout).
        /// Поэтому уменьшаем шрифт так, чтобы перевод занимал примерно ту же ширину, что и оригинал
        /// (но не меньше 65% исходного размера).
        /// </summary>
        private static void FitButtonLabel(TMP_Text text, string original, string translated)
        {
            int id = text.GetInstanceID();
            if (!IsButtonLabel.TryGetValue(id, out bool isButton))
            {
                var selectable = text.GetComponentInParent<UnityEngine.UI.Selectable>(true);
                isButton = !text.enableAutoSizing && (selectable is UnityEngine.UI.Button || selectable is TMP_Dropdown);
                if (IsButtonLabel.Count > 100000) { IsButtonLabel.Clear(); BaseFontSize.Clear(); }
                IsButtonLabel[id] = isButton;
            }
            if (!isButton) return;

            bool known = BaseFontSize.TryGetValue(id, out float baseSize);
            if (ReferenceEquals(original, translated))
            {
                if (known && text.fontSize != baseSize) text.fontSize = baseSize; // надпись сменилась на непереведённую
                return;
            }
            if (!known) BaseFontSize[id] = baseSize = text.fontSize;

            text.fontSize = baseSize;
            float en = text.GetPreferredValues(original).x;
            float ru = text.GetPreferredValues(translated).x;
            float scale = en > 0f && ru > en ? Math.Max(MinButtonScale, en / ru) : 1f;
            text.fontSize = baseSize * scale;
        }

        /// <summary>Текст, который вводит игрок (имена, поля ввода), переводить нельзя.</summary>
        private static bool IsUserInput(TMP_Text text)
        {
            int id = text.GetInstanceID();
            if (SkipCache.TryGetValue(id, out bool skip))
                return skip;
            var input = text.GetComponentInParent<TMP_InputField>(true);
            skip = input != null && input.textComponent == text;
            if (SkipCache.Count > 100000) SkipCache.Clear();
            SkipCache[id] = skip;
            return skip;
        }

        [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
        private static class TextSetter
        {
            private static void Prefix(TMP_Text __instance, ref string value)
            {
                value = Process(__instance, value);
            }
        }

        [HarmonyPatch]
        private static class SetTextOverloads
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var m in typeof(TMP_Text).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != nameof(TMP_Text.SetText)) continue;
                    var p = m.GetParameters();
                    // SetText(string), SetText(string, bool), SetText(string, float, ...) — формат тоже переводим.
                    if (p.Length > 0 && p[0].ParameterType == typeof(string) && p[0].Name == "sourceText")
                        yield return m;
                }
            }

            private static void Prefix(TMP_Text __instance, ref string sourceText)
            {
                sourceText = Process(__instance, sourceText);
            }
        }

        [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.font), MethodType.Setter)]
        private static class FontSetter
        {
            private static void Postfix(TMP_Text __instance)
            {
                try { FontManager.Ensure(__instance.font); }
                catch (Exception ex) { Plugin.Log.LogWarning(ex.Message); }
            }
        }

        /// <summary>Игра склоняет слова сама: 3.Pluralize("car") → «3 cars». Подменяем на «3 вагона» (записи p: в словаре).</summary>
        [HarmonyPatch(typeof(Core.PluralizeExtensions), nameof(Core.PluralizeExtensions.Pluralize), new[] { typeof(int), typeof(string) })]
        private static class PluralizeNumber
        {
            private static bool Prefix(int number, string noun, ref string __result)
            {
                if (!Plugin.TranslateUi.Value) return true;
                string form = Plugin.Ui.Plural(noun, number);
                if (form == null) return true;
                __result = number + " " + form;
                return false;
            }
        }

        [HarmonyPatch(typeof(Core.PluralizeExtensions), nameof(Core.PluralizeExtensions.Pluralize), new[] { typeof(string), typeof(int) })]
        private static class PluralizeWord
        {
            private static bool Prefix(string word, int number, ref string __result)
            {
                if (!Plugin.TranslateUi.Value) return true;
                string form = Plugin.Ui.Plural(word, number);
                if (form == null) return true;
                __result = form;
                return false;
            }
        }

        /// <summary>Тексты из сцен/префабов не проходят через сеттер — переводим их при включении.</summary>
        [HarmonyPatch]
        private static class OnEnablePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable");
                yield return AccessTools.Method(typeof(TextMeshPro), "OnEnable");
            }

            private static void Prefix(TMP_Text __instance)
            {
                try
                {
                    string current = __instance.text;
                    string translated = Process(__instance, current);
                    if (!ReferenceEquals(current, translated) && translated != current)
                        __instance.text = translated;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("OnEnable: " + ex.Message);
                }
            }
        }
    }
}
