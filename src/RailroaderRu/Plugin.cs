using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RailroaderRu
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ru.railroader.localization";
        public const string Name = "Railroader Russian";
        public const string Version = BuildInfo.Version; // из VersionPrefix в .csproj

        internal static ManualLogSource Log;
        internal static string PluginDir;

        /// <summary>Словарь интерфейса (всё, что выводится через TextMeshPro).</summary>
        internal static readonly Translator Ui = new Translator();
        /// <summary>Словарь строк из Lua-скриптов (туториал).</summary>
        internal static readonly Translator Lua = new Translator();

        internal static ConfigEntry<bool> TranslateUi;
        internal static ConfigEntry<bool> TranslateTutorial;
        internal static ConfigEntry<bool> TranslateGuide;
        internal static ConfigEntry<bool> ReplaceFonts;
        internal static ConfigEntry<bool> BundledFonts;
        internal static ConfigEntry<bool> AutoSizeButtons;
        internal static ConfigEntry<bool> DumpUntranslated;
        internal static ConfigEntry<bool> HotReload;

        private static FileSystemWatcher _watcher;
        private static volatile bool _reloadRequested;

        internal static string TranslationsDir => Path.Combine(PluginDir, "translations");
        internal static string LuaTranslationsDir => Path.Combine(TranslationsDir, "tutorial");
        internal static string OverridesDir => Path.Combine(PluginDir, "StreamingAssets");
        internal static string FontsDir => Path.Combine(PluginDir, "fonts");

        private void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            TranslateUi = Config.Bind("Translation", "UI", true, "Переводить интерфейс игры.");
            TranslateTutorial = Config.Bind("Translation", "Tutorial", true, "Переводить обучение (Lua-скрипты).");
            TranslateGuide = Config.Bind("Translation", "Guide", true, "Подменять страницы справочника (Guide) переведёнными.");
            ReplaceFonts = Config.Bind("Fonts", "CyrillicFallback", true, "Добавлять к шрифтам игры запасные шрифты с кириллицей.");
            BundledFonts = Config.Bind("Fonts", "BundledFonts", false,
                "Экспериментально: брать кириллицу из TTF плагина (PT Serif, PT Mono, Montserrat), а не из шрифтов игры.");
            AutoSizeButtons = Config.Bind("Fonts", "AutoSizeButtons", true,
                "Уменьшать шрифт на кнопках, если русский текст шире английского.");
            DumpUntranslated = Config.Bind("Translators", "DumpUntranslated", false,
                "Записывать встреченные непереведённые строки в untranslated.txt рядом с плагином.");
            HotReload = Config.Bind("Translators", "HotReload", true,
                "Перечитывать словари при сохранении файлов в папке translations.");

            TranslationFile.Warn = msg => Log.LogWarning(msg);
            Ui.Warn = Lua.Warn = msg => Log.LogWarning(msg);
            UntranslatedLog.Init(Path.Combine(PluginDir, "untranslated.txt"));
            Ui.OnUntranslated = s => { if (DumpUntranslated.Value) UntranslatedLog.Record(s); };

            LoadTranslations();
            FontManager.Init(FontsDir);

            ApplyPatches();

            if (HotReload.Value)
                StartWatcher();

            Log.LogInfo($"{Name} {Version} загружен: интерфейс — {Ui.ExactCount} строк и {Ui.TemplateCount} шаблонов, " +
                        $"туториал — {Lua.ExactCount} строк.");
        }

        /// <summary>
        /// Патчи применяются по одному: если после обновления игры какой-то метод исчезнет,
        /// отвалится только эта часть (например, справочник), а остальной перевод продолжит работать.
        /// </summary>
        private static void ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    if (!type.GetCustomAttributes(typeof(HarmonyPatch), false).Any()) continue;
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.LogError($"Патч {type.FullName} не применён (возможно, игра обновилась): {ex.GetBaseException().Message}");
                }
            }
            if (failed > 0)
                Log.LogWarning($"Применено патчей: {ok}, с ошибками: {failed}.");
        }

        internal static void LoadTranslations()
        {
            Ui.Clear();
            Lua.Clear();
            LoadDir(Ui, TranslationsDir, SearchOption.TopDirectoryOnly);
            LoadDir(Lua, LuaTranslationsDir, SearchOption.AllDirectories);
        }

        private static void LoadDir(Translator target, string dir, SearchOption option)
        {
            if (!Directory.Exists(dir))
            {
                Log.LogWarning("Папка с переводом не найдена: " + dir);
                return;
            }
            foreach (var file in Directory.GetFiles(dir, "*.txt", option).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    target.Add(TranslationFile.Load(file));
                }
                catch (Exception ex)
                {
                    Log.LogError($"Ошибка чтения {file}: {ex.Message}");
                }
            }
        }

        /// <summary>Вызывается из хуков на главном потоке: перечитывает словари, если файлы менялись.</summary>
        internal static void ReloadIfRequested()
        {
            if (!_reloadRequested) return;
            _reloadRequested = false;
            LoadTranslations();
            Log.LogInfo($"Словари перечитаны: {Ui.ExactCount} строк, {Ui.TemplateCount} шаблонов, туториал — {Lua.ExactCount}.");
        }

        private static void StartWatcher()
        {
            try
            {
                if (!Directory.Exists(TranslationsDir)) return;
                _watcher = new FileSystemWatcher(TranslationsDir, "*.txt")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler handler = (_, __) => _reloadRequested = true;
                _watcher.Changed += handler;
                _watcher.Created += handler;
                _watcher.Deleted += handler;
                _watcher.Renamed += (_, __) => _reloadRequested = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Автоперезагрузка словарей недоступна: " + ex.Message);
            }
        }
    }
}
