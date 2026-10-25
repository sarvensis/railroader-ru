using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using RailroaderRu;

namespace RuTools
{
    /// <summary>
    /// Достаёт m_text из компонентов TextMeshPro в сценах и ресурсах игры — это надписи,
    /// заданные прямо в редакторе Unity (их нет в коде).
    /// </summary>
    internal static class AssetsExtractor
    {
        public static int Probe(string dataDir)
        {
            var am = new AssetsManager();
            foreach (var path in AssetFiles(dataDir))
            {
                var inst = am.LoadAssetsFile(path, false);
                Console.WriteLine($"{Path.GetFileName(path)}: unity {inst.file.Metadata.UnityVersion}, typetree={inst.file.Metadata.TypeTreeEnabled}, " +
                                  $"objects={inst.file.AssetInfos.Count}");
            }
            return 0;
        }

        public static int Extract(string outPath, string dataDir, string tpkPath)
        {
            var am = new AssetsManager();
            if (tpkPath != null)
                am.LoadClassPackage(tpkPath);
            string managed = Path.Combine(dataDir, "Managed");
            am.MonoTempGenerator = new MonoCecilTempGenerator(managed);

            var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            int errors = 0;
            foreach (var path in AssetFiles(dataDir))
            {
                var inst = am.LoadAssetsFile(path, false);
                if (tpkPath != null)
                    am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

                var scriptNames = new Dictionary<long, string>();
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    try
                    {
                        var scriptName = GetScriptName(am, inst, info);
                        if (scriptName == null || !TextFields.TryGetValue(scriptName, out var fields)) continue;
                        var baseField = am.GetBaseField(inst, info);
                        if (baseField == null) continue;
                        foreach (var fieldName in fields)
                        {
                            var f = baseField[fieldName];
                            if (f == null || f.IsDummy) continue;
                            string text = f.AsString;
                            if (string.IsNullOrWhiteSpace(text) || !Translator.HasLatinLetter(text)) continue;
                            string where = (scriptName.StartsWith("TextMeshPro") ? "" : scriptName + " @ ") + Path.GetFileName(path);
                            if (!found.TryGetValue(text, out var set)) found[text] = set = new SortedSet<string>(StringComparer.Ordinal);
                            set.Add(where);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (errors++ < 5) Console.Error.WriteLine($"{Path.GetFileName(path)} #{info.PathId}: {ex.Message}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// Надписи из сцен и префабов игры (компоненты TextMeshPro).");
            foreach (var group in found.GroupBy(kv => string.Join(", ", kv.Value)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine().AppendLine("// === " + group.Key);
                foreach (var kv in group.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.AppendLine(TranslationFile.Escape(kv.Key) + "=");
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"Найдено надписей: {found.Count} → {outPath}" + (errors > 0 ? $" (ошибок чтения: {errors})" : ""));
            return 0;
        }

        /// <summary>Параметры шрифтов TextMeshPro: режим атласа, есть ли исходный TTF, путь.</summary>
        public static int ProbeFonts(string dataDir, string tpkPath, string outDir = null)
        {
            var am = new AssetsManager();
            am.LoadClassPackage(tpkPath);
            am.MonoTempGenerator = new MonoCecilTempGenerator(Path.Combine(dataDir, "Managed"));
            foreach (var path in AssetFiles(dataDir))
            {
                var inst = am.LoadAssetsFile(path, false);
                am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Font))
                {
                    try
                    {
                        var f = am.GetBaseField(inst, info);
                        var data = f["m_FontData"]["Array"];
                        Console.WriteLine($"{Path.GetFileName(path),-22} FONT #{info.PathId,-5} {f["m_Name"].AsString,-35} data={data.Children.Count} bytes");
                        if (outDir != null && data.Children.Count > 0)
                        {
                            Directory.CreateDirectory(outDir);
                            var bytes = data.Children.Select(c => (byte)c.AsInt).ToArray();
                            File.WriteAllBytes(Path.Combine(outDir, f["m_Name"].AsString + ".ttf"), bytes);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
                }
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    try
                    {
                        if (GetScriptName(am, inst, info) != "TMP_FontAsset") continue;
                        var f = am.GetBaseField(inst, info);
                        var src = f["m_SourceFontFile"];
                        string srcInfo = src.IsDummy ? "?" : $"file={src["m_FileID"].AsInt} path={src["m_PathID"].AsLong}";
                        Console.WriteLine($"{Path.GetFileName(path),-22} {f["m_Name"].AsString,-45} mode={f["m_AtlasPopulationMode"].AsInt} " +
                                          $"src[{srcInfo}] srcPath='{f["m_SourceFontFilePath"].AsString}' fallbacks={f["m_FallbackFontAssetTable"]["Array"].Children.Count}");
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
                }
            }
            return 0;
        }

        /// <summary>Какие строковые поля каких компонентов показываются игроку.</summary>
        private static readonly Dictionary<string, string[]> TextFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["TextMeshProUGUI"] = new[] { "m_text" },
            ["TextMeshPro"] = new[] { "m_text" },
            ["Load"] = new[] { "description" },           // названия грузов
            ["Section"] = new[] { "displayName", "description" },   // этапы (Milestones)
            ["MapFeature"] = new[] { "displayName", "description" },
        };

        private static readonly Dictionary<(string, long), string> ScriptCache = new Dictionary<(string, long), string>();

        private static string GetScriptName(AssetsManager am, AssetsFileInstance inst, AssetFileInfo info)
        {
            // m_Script лежит в начале любого MonoBehaviour: PPtr<GameObject>, m_Enabled, PPtr<MonoScript>.
            var reader = inst.file.Reader;
            reader.Position = info.GetAbsoluteByteOffset(inst.file);
            reader.ReadInt32(); reader.ReadInt64();      // m_GameObject
            reader.ReadByte(); reader.Align();           // m_Enabled
            int fileId = reader.ReadInt32();
            long pathId = reader.ReadInt64();            // m_Script

            var key = (inst.path + "|" + fileId, pathId);
            if (ScriptCache.TryGetValue(key, out var name)) return name;

            name = null;
            var ext = am.GetExtAsset(inst, fileId, pathId, onlyGetInfo: true);
            if (ext.info != null && ext.file != null)
            {
                var r = ext.file.file.Reader;
                r.Position = ext.info.GetAbsoluteByteOffset(ext.file.file);
                name = r.ReadCountStringInt32(); // MonoScript.m_Name
            }
            ScriptCache[key] = name;
            return name;
        }

        private static IEnumerable<string> AssetFiles(string dataDir)
        {
            foreach (var f in Directory.GetFiles(dataDir))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith("level", StringComparison.Ordinal) && !n.EndsWith(".resS", StringComparison.Ordinal)
                    || n.EndsWith(".assets", StringComparison.Ordinal) || n == "globalgamemanagers")
                    yield return f;
            }
        }
    }
}
