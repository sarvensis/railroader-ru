using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using RailroaderRu;

namespace RuTools
{
    /// <summary>
    /// Утилиты для переводчиков:
    ///   extract-dll  out.txt  Assembly-CSharp.dll [...]   — строки интерфейса из кода игры
    ///   extract-lua  out.txt  StreamingAssets/Tutorial    — тексты туториала
    ///   update       extracted.txt  translations/x.txt   — дописать в словарь новые строки после обновления игры
    ///   check        translations                         — проверить словари на ошибки
    ///   test         translations  "текст"                — показать, как будет переведена строка
    ///   stats        translations                         — сколько строк переведено
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            TranslationFile.Warn = m => Console.Error.WriteLine("ПРЕДУПРЕЖДЕНИЕ: " + m);
            if (args.Length == 0) return Usage();
            try
            {
                switch (args[0])
                {
                    case "extract-dll" when args.Length >= 3: return ExtractDll(args[1], args.Skip(2).ToArray());
                    case "extract-lua" when args.Length == 3: return ExtractLua(args[1], args[2]);
                    case "probe-assets" when args.Length == 2: return AssetsExtractor.Probe(args[1]);
                    case "probe-fonts" when args.Length >= 3: return AssetsExtractor.ProbeFonts(args[1], args[2], args.Length > 3 ? args[3] : null);
                    case "extract-assets" when args.Length >= 3: return AssetsExtractor.Extract(args[1], args[2], args.Length > 3 ? args[3] : null);
                    case "update" when args.Length == 3: return Update(args[1], args[2]);
                    case "fill" when args.Length == 4: return Fill(args[1], args[2], args[3]);
                    case "check" when args.Length == 2: return Check(args[1]);
                    case "test" when args.Length >= 3: return Test(args[1], args.Skip(2).ToArray());
                    case "stats" when args.Length == 2: return Stats(args[1]);
                    case "test-lua" when args.Length == 4: return TestLua(args[1], args[2], args[3]);
                    case "make-refs" when args.Length >= 4: return MakeRefs(args[1], args[2], args.Skip(3).ToArray());
                    default: return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int Usage()
        {
            Console.WriteLine(@"RuTools — утилиты русификатора Railroader

  extract-dll <out.txt> <dll>...         извлечь строки интерфейса из сборок игры
  extract-lua <out.txt> <папка Tutorial> извлечь тексты туториала
  update <извлечённое.txt> <словарь.txt> дописать в словарь отсутствующие строки
  check <папка translations>             проверить словари
  test <папка translations> <текст>...   перевести строку так же, как это сделает плагин
  stats <папка translations>             статистика перевода");
            return 2;
        }

        // ---------------------------------------------------------------- extract-dll

        private static readonly HashSet<string> SkipCallees = new HashSet<string>(StringComparer.Ordinal)
        {
            // Логи и отладка
            "Serilog.Log", "Serilog.ILogger", "UnityEngine.Debug", "Logging.Log",
            // Ресурсы, сцены, ввод, анимация, шейдеры
            "UnityEngine.Resources", "UnityEngine.Shader", "UnityEngine.Material", "UnityEngine.Animator",
            "UnityEngine.PlayerPrefs", "UnityEngine.GameObject", "UnityEngine.Transform", "UnityEngine.Input",
            "UnityEngine.SceneManagement.SceneManager", "UnityEngine.AssetBundle", "UnityEngine.Application",
            "UnityEngine.InputSystem.InputAction", "UnityEngine.InputSystem.InputActionMap", "UnityEngine.InputSystem.InputActionAsset",
            "UnityEngine.Profiling.Profiler", "Unity.Profiling.ProfilerMarker",
            // Сериализация / сеть / файлы
            "Newtonsoft.Json.Linq.JObject", "Newtonsoft.Json.Linq.JToken", "System.IO.Path", "System.IO.File", "System.IO.Directory",
            "System.Text.RegularExpressions.Regex", "System.Type", "System.Reflection.Assembly",
            "KeyValue.Runtime.IKeyValueObject", "Game.State.IKeyValueObject",
        };

        private static readonly HashSet<string> SkipMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "op_Equality", "op_Inequality", "Equals", "StartsWith", "EndsWith", "Contains", "IndexOf", "Replace", "Split",
            "GetComponent", "Find", "FindChild", "Load", "PropertyToID", "SetTrigger", "SetBool", "SetFloat", "SetInteger",
            "GetInt", "GetFloat", "GetString", "SetInt", "SetString", "HasKey", "DeleteKey", "Observe", "Get", "Set",
            "TryGetValue", "ContainsKey", "Add", "Remove", "get_Item", "set_Item", "LogInformation", "LogWarning", "LogError",
            "Information", "Warning", "Error", "Debug", "Verbose", "Fatal", "ForContext", "Exception",
            "Parse", "TryParse", "ToString", "Format" /* Format обрабатываем отдельно */, "AddComponent", "PlayOneShot",
            "BeginSample", "EndSample", "Assert", "IsTrue", "IsFalse", "AreEqual", "Throw", "SetGlobalFloat", "SetGlobalTexture",
            "SetTexture", "SetColor", "SetVector", "EnableKeyword", "DisableKeyword", "HasProperty", "GetProperty",
            "RegisterCommand", "SendMessage", "Invoke", "InvokeRepeating", "CancelInvoke", "StartCoroutine",
        };

        private static readonly Regex LooksLikeIdentifier = new Regex(@"^[a-z0-9_\-\.\/:#@\[\]\(\)]+$|^[A-Za-z0-9]+[_\.\/:][A-Za-z0-9_\.\/:\-]*$|^[A-Z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex FormatPlaceholder = new Regex(@"\{\d+(?::[^{}]*)?\}", RegexOptions.Compiled);

        private sealed class Found
        {
            public string Text;
            public bool IsTemplate;
            public string Where;
        }

        private static int ExtractDll(string outPath, string[] dlls)
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (var d in dlls.Select(Path.GetDirectoryName).Distinct())
                resolver.AddSearchDirectory(d);

            var found = new Dictionary<string, Found>(StringComparer.Ordinal);
            foreach (var dll in dlls)
            {
                using var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver });
                foreach (var type in asm.MainModule.GetTypes())
                {
                    if (IsNoiseType(type)) continue;
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        var ins = method.Body.Instructions;
                        for (int i = 0; i < ins.Count; i++)
                        {
                            if (ins[i].OpCode != OpCodes.Ldstr) continue;
                            string s = (string)ins[i].Operand;
                            var (consumer, viaFormat) = FindConsumer(ins, i);
                            if (!IsCandidate(s, consumer, viaFormat)) continue;
                            bool template = FormatPlaceholder.IsMatch(s);
                            string key = (template ? "t:" : "") + s;
                            if (!found.ContainsKey(key))
                                found[key] = new Found { Text = s, IsTemplate = template, Where = OwnerName(type) };
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// Строки, извлечённые из кода игры. Формат: Оригинал=Перевод (пустой перевод = не переведено).");
            sb.AppendLine("// Строки с {0}, {1} — шаблоны (t:), числа и имена подставляются автоматически.");
            foreach (var group in found.Values.GroupBy(f => f.Where).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("// === " + group.Key);
                foreach (var f in group.OrderBy(f => f.Text, StringComparer.Ordinal))
                    sb.AppendLine((f.IsTemplate ? "t:" : "") + TranslationFile.Escape(f.Text) + "=");
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"Найдено строк: {found.Count} → {outPath}");
            return 0;
        }

        private static bool IsNoiseType(TypeDefinition type)
        {
            string ns = RootType(type).Namespace ?? "";
            string[] noise =
            {
                "InfinityCode", "WorldStreamer2", "UnityStandardAssets", "VisualDesignCafe", "GalaSoft", "Analytics",
                "Network.Messages", "Game.Messages", "CSML", "Audio", "Game.Scripting.Testing", "Core", "Map.Runtime",
                "Helpers", "Character", "Avatar", "AutoTrestle", "AssetPack", "Effects", "TelegraphPoles", "TerrainStitch",
                "Track.Search", "Model.Physics", "Model.ComponentBuilders", "Model.Definition.Data", "Network.Buffers",
                "BezierCurveMesh", "Enviro", "TrainPhysics", "Logging", "UI.CarEditor", "RollingStock.PipeGenerator",
                "RollingStock.LoadModels", "Model.Database",
            };
            if (ns.Length == 0)
            {
                // Глобальное пространство имён: оставляем только классы, связанные с интерфейсом.
                string[] keep = { "ConsistPlacer", "PauseMenu", "TrainController", "SwitchStandClick", "ObjectPicker", "TooltipInfo", "TextStyles" };
                if (!keep.Contains(RootType(type).Name)) return true;
            }
            if (noise.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal))) return true;
            string name = RootType(type).Name;
            return name.StartsWith("DebugGUI", StringComparison.Ordinal) || name.StartsWith("GraphIt", StringComparison.Ordinal)
                   || name.Contains("Test") || name.Contains("Demo") || name.StartsWith("<", StringComparison.Ordinal);
        }

        private static TypeDefinition RootType(TypeDefinition t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t;
        }

        private static string OwnerName(TypeDefinition type)
        {
            var root = RootType(type);
            return string.IsNullOrEmpty(root.Namespace) ? root.Name : root.Namespace + "." + root.Name;
        }

        /// <summary>Ищет вызов, которому передаётся строка (первый call/newobj после ldstr).</summary>
        private static (MethodReference consumer, bool viaFormat) FindConsumer(Mono.Collections.Generic.Collection<Instruction> ins, int index)
        {
            for (int j = index + 1; j < ins.Count && j < index + 40; j++)
            {
                var op = ins[j].OpCode;
                if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                {
                    var mr = (MethodReference)ins[j].Operand;
                    // string.Format(fmt, ...) — шаблон; смотрим, куда уходит результат (лог/исключение → не UI).
                    if (mr.DeclaringType.FullName == "System.String" && mr.Name == "Format")
                    {
                        var (next, _) = FindConsumer(ins, j);
                        return (next != null && !IsUiSafe(next) ? next : mr, true);
                    }
                    if (mr.DeclaringType.FullName == "System.String" && mr.Name == "Concat")
                        return (mr, false);
                    // Пропускаем вызовы, вычисляющие следующие аргументы (ToString, get_X и т.п.).
                    if (mr.Name.StartsWith("get_", StringComparison.Ordinal) || mr.Name == "ToString" || mr.Name == "op_Implicit")
                        continue;
                    if (mr.DeclaringType.FullName == "System.String" && (mr.Name == "Join"))
                        return (mr, false);
                    return (mr, false);
                }
                if (op == OpCodes.Ret || op == OpCodes.Stloc || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2
                    || op == OpCodes.Stloc_3 || op == OpCodes.Stloc_S || op == OpCodes.Stfld || op == OpCodes.Stsfld
                    || op == OpCodes.Switch || op.FlowControl == FlowControl.Cond_Branch)
                    return (null, false);
            }
            return (null, false);
        }

        /// <summary>false — вызов явно не про интерфейс (лог, исключение, ассерт).</summary>
        private static bool IsUiSafe(MethodReference mr)
        {
            string decl = mr.DeclaringType.FullName;
            if (SkipCallees.Contains(decl) || decl.StartsWith("Serilog", StringComparison.Ordinal) || decl.EndsWith("Exception", StringComparison.Ordinal))
                return false;
            return mr.Name != "Assert" && mr.Name != "Log" && mr.Name != "LogWarning" && mr.Name != "LogError";
        }

        private static bool IsCandidate(string s, MethodReference consumer, bool viaFormat)
        {
            if (s.Length < 2 || !Translator.HasLatinLetter(s)) return false;
            if (s.Length > 3000) return false;
            if (consumer != null)
            {
                string decl = consumer.DeclaringType.FullName;
                if (SkipCallees.Contains(decl)) return false;
                if (decl.StartsWith("Serilog", StringComparison.Ordinal)) return false;
                if (decl.EndsWith("Exception", StringComparison.Ordinal)) return false;
                if (!viaFormat && SkipMethodNames.Contains(consumer.Name) && decl != "UI.Builder.UIPanelBuilder") return false;
            }
            string t = s.Trim();
            if (t.Length < 2) return false;
            // Куски конкатенации (" cars", "Error: ") — неполные фразы, их ловит дамп в игре.
            if (s != t && !viaFormat && consumer?.DeclaringType.FullName == "System.String") return false;
            if (t.Contains(" ")) return !LooksLikeCode(t);
            // Одно слово: оставляем только «Слово» с заглавной буквы (кнопки, заголовки).
            if (LooksLikeIdentifier.IsMatch(t)) return false;
            if (!char.IsUpper(t[0])) return false;
            if (t.Any(char.IsDigit) && t.Length < 6) return false;
            // CamelCase из нескольких слов без пробелов — скорее всего идентификатор.
            if (Regex.IsMatch(t, @"^[A-Z][a-z]+[A-Z]")) return false;
            return true;
        }

        private static bool LooksLikeCode(string s)
        {
            if (s.StartsWith("{\"", StringComparison.Ordinal) || s.StartsWith("<?xml", StringComparison.Ordinal)) return true;
            if (Regex.IsMatch(s, @"^(SELECT|INSERT|UPDATE|DELETE|CREATE)\s", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(s, @"[;{}]\s*$") && !FormatPlaceholder.IsMatch(s)) return true;
            return false;
        }

        // ---------------------------------------------------------------- extract-lua

        private static int ExtractLua(string outPath, string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Тексты туториала (Lua). Для длинных текстов используется блок:");
            sb.AppendLine("// <<<  оригинал  ===  перевод  >>>   (отступы строк значения не имеют)");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;
            foreach (var file in Directory.GetFiles(dir, "*.lua").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string src = File.ReadAllText(file);
                var lits = LuaStrings.Scan(src).Where(l => IsLuaText(src, l)).ToList();
                if (lits.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine("// === " + Path.GetFileName(file));
                foreach (var lit in lits)
                {
                    string key = lit.Key;
                    if (!seen.Add(key)) continue;
                    total++;
                    sb.AppendLine("// #" + total);
                    if (key.Contains('\n'))
                    {
                        sb.AppendLine("<<<").AppendLine(key).AppendLine("===").AppendLine().AppendLine(">>>");
                    }
                    else
                    {
                        sb.AppendLine(TranslationFile.Escape(key) + "=");
                    }
                }
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"Найдено текстов: {total} → {outPath}");
            return 0;
        }

        private static bool IsLuaText(string src, LuaStrings.Literal lit)
        {
            string key = lit.Key;
            if (key.Length < 2 || !Translator.HasLatinLetter(key)) return false;
            // require("module"), идентификаторы, локации вида "S2qd|a|93.6"
            int lineStart = src.LastIndexOf('\n', Math.Max(0, lit.Start - 1)) + 1;
            string before = src.Substring(lineStart, lit.Start - lineStart);
            if (Regex.IsMatch(before, @"(require|Location\.new|print|error|assert|get_\w+|condition_\w+|is_\w+|find_\w+|log)\s*\(\s*$")) return false;
            if (Regex.IsMatch(before, @"\b(id|extension_type|style|car_type|type|key|name)\s*=\s*$")) return false;
            if (Regex.IsMatch(before, @"==\s*$|~=\s*$|\[\s*$")) return false;
            if (Regex.IsMatch(before, @"builder:page\(\s*$")) return false;
            if (!key.Contains(' ') && !char.IsUpper(key[0])) return false;
            if (Regex.IsMatch(key, @"^[\w\-\.|:]+$") && !Regex.IsMatch(key, @"^[A-Z][a-z]+$")) return false;
            return true;
        }

        // ---------------------------------------------------------------- fill

        /// <summary>
        /// Подставляет переводы в извлечённый файл по номерам «// #N».
        /// Файл переводов: строка «#N», затем текст перевода до следующего «#N».
        /// </summary>
        private static int Fill(string extractedPath, string numberedPath, string outPath)
        {
            var translations = new Dictionary<int, string>();
            int current = -1;
            var buf = new List<string>();
            void Flush()
            {
                if (current >= 0) translations[current] = string.Join("\n", buf).Trim('\n', ' ');
                buf.Clear();
            }
            foreach (var raw in File.ReadAllText(numberedPath).Replace("\r\n", "\n").Split('\n'))
            {
                var m = Regex.Match(raw, @"^#(\d+)\s*$");
                if (m.Success) { Flush(); current = int.Parse(m.Groups[1].Value); continue; }
                if (current >= 0) buf.Add(raw.TrimEnd());
            }
            Flush();

            var lines = File.ReadAllText(extractedPath).Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            int filled = 0, number = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var m = Regex.Match(line, @"^// #(\d+)$");
                if (m.Success) { number = int.Parse(m.Groups[1].Value); sb.Append(line).Append('\n'); continue; }
                translations.TryGetValue(number, out var tr);
                if (line == "<<<")
                {
                    // Копируем оригинал до "===", пропускаем старый перевод до ">>>".
                    sb.Append(line).Append('\n');
                    for (i++; i < lines.Length && lines[i] != "==="; i++) sb.Append(lines[i]).Append('\n');
                    sb.Append("===\n");
                    var old = new List<string>();
                    for (i++; i < lines.Length && lines[i] != ">>>"; i++) old.Add(lines[i]);
                    string value = !string.IsNullOrEmpty(tr) ? tr : string.Join("\n", old).Trim('\n');
                    sb.Append(value).Append("\n>>>\n");
                    if (!string.IsNullOrEmpty(tr)) filled++;
                    number = -1;
                    continue;
                }
                if (number >= 0 && !line.StartsWith("//") && line.EndsWith("=") && !string.IsNullOrEmpty(tr))
                {
                    if (tr.Contains('\n'))
                        Console.Error.WriteLine($"#{number}: однострочный оригинал, а перевод многострочный — переводы строк заменены на \\n");
                    sb.Append(line).Append(TranslationFile.Escape(tr).Replace("\\=", "=")).Append('\n');
                    filled++;
                    number = -1;
                    continue;
                }
                sb.Append(line).Append(i < lines.Length - 1 ? "\n" : "");
                if (!line.StartsWith("//")) number = -1;
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            int missing = translations.Count - filled;
            Console.WriteLine($"Подставлено переводов: {filled}" + (missing > 0 ? $", не найдено номеров: {missing}" : "") + $" → {outPath}");
            return 0;
        }

        // ---------------------------------------------------------------- update

        private static int Update(string extractedPath, string targetPath)
        {
            var existing = File.Exists(targetPath) ? File.ReadAllText(targetPath) : "";
            var known = new HashSet<string>(ReadAllKeys(existing), StringComparer.Ordinal);
            var extracted = File.ReadAllText(extractedPath);
            var add = new StringBuilder();
            int count = 0;
            foreach (var (key, raw) in ReadRawEntries(extracted))
            {
                if (known.Contains(key)) continue;
                known.Add(key);
                add.AppendLine(raw);
                count++;
            }
            if (count == 0)
            {
                Console.WriteLine("Новых строк нет.");
                return 0;
            }
            var sb = new StringBuilder(existing.TrimEnd());
            sb.AppendLine().AppendLine().AppendLine($"// === Новые строки ({DateTime.Now:yyyy-MM-dd})").Append(add);
            File.WriteAllText(targetPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"Добавлено новых строк: {count} → {targetPath}");
            return 0;
        }

        /// <summary>Все ключи файла, включая записи без перевода.</summary>
        private static IEnumerable<string> ReadAllKeys(string content) => ReadRawEntries(content).Select(e => e.key);

        private static IEnumerable<(string key, string raw)> ReadRawEntries(string content)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart('﻿');
                if (line.TrimEnd() == "<<<")
                {
                    var raw = new StringBuilder(line).Append('\n');
                    var key = new List<string>();
                    bool inValue = false;
                    for (i++; i < lines.Length; i++)
                    {
                        raw.Append(lines[i]);
                        string t = lines[i].TrimEnd();
                        if (t == ">>>") break;
                        raw.Append('\n');
                        if (t == "===") { inValue = true; continue; }
                        if (!inValue) key.Add(t);
                    }
                    yield return ("block:" + string.Join("\n", key).Trim('\n'), raw.ToString());
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                int sep = -1;
                for (int k = 0; k < line.Length; k++)
                {
                    if (line[k] == '\\') { k++; continue; }
                    if (line[k] == '=') { sep = k; break; }
                }
                if (sep <= 0) continue;
                string prefix = line.StartsWith("t:") ? "t:" : line.StartsWith("r:") ? "r:" : "";
                string k2 = line.Substring(prefix.Length, sep - prefix.Length);
                // Ключи блоков и однострочных записей сравниваем по тексту, чтобы не дублировать.
                // {0}, {0:n}, {0:N0} — один и тот же плейсхолдер.
                string unesc = Regex.Replace(TranslationFile.Unescape(k2), @"\{(\d+)(?::[^{}]*)?\}", "{$1}");
                yield return (unesc.Contains('\n') ? "block:" + unesc : prefix + unesc, line);
            }
        }

        // ---------------------------------------------------------------- check / test / stats

        // Только теги TextMeshPro: <car> в подсказках консоли тегом не считается.
        private static readonly Regex Tag = new Regex(
            @"</?(?:b|i|u|s|sub|sup|size|color|style|sprite|link|mspace|cspace|alpha|noparse|rotate|voffset|indent|line-height|margin|mark|pos|width|font|material|lowercase|uppercase|smallcaps|align|nobr|page|space|br|gradient)\b[^<>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ValuePlaceholder = new Regex(@"\{(\d+)(?:\|[^{}]*)?\}", RegexOptions.Compiled);

        private static int Check(string dir)
        {
            int problems = 0;
            var all = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f))
            {
                foreach (var e in TranslationFile.Load(file))
                {
                    string where = $"{Path.GetRelativePath(dir, file)}:{e.Line}";
                    string id = Path.GetDirectoryName(Path.GetRelativePath(dir, file)) + "|" + e.Kind + "|" + e.Key;
                    if (all.TryGetValue(id, out var prev) && prev.Value != e.Value)
                    {
                        Console.WriteLine($"{where}: дубликат (уже есть в {prev.Source}:{prev.Line}) с другим переводом: «{Short(e.Key)}»");
                        problems++;
                    }
                    all[id] = e;

                    var keyTags = Tag.Matches(e.Key).Select(m => m.Value).OrderBy(x => x).ToList();
                    var valTags = Tag.Matches(e.Value).Select(m => m.Value).OrderBy(x => x).ToList();
                    if (e.Kind == EntryKind.Exact && !keyTags.SequenceEqual(valTags))
                    {
                        Console.WriteLine($"{where}: теги в переводе не совпадают с оригиналом: «{Short(e.Key)}»");
                        problems++;
                    }
                    if (e.Kind == EntryKind.Template)
                    {
                        var keyPh = Regex.Matches(e.Key, @"\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
                        var valPh = ValuePlaceholder.Matches(e.Value).Select(m => m.Groups[1].Value).ToHashSet();
                        if (!valPh.IsSubsetOf(keyPh))
                        {
                            Console.WriteLine($"{where}: в переводе есть плейсхолдер, которого нет в оригинале: «{Short(e.Key)}»");
                            problems++;
                        }
                        if (!keyPh.IsSubsetOf(valPh))
                        {
                            Console.WriteLine($"{where}: в переводе потерян плейсхолдер: «{Short(e.Key)}»");
                            problems++;
                        }
                    }
                    if (e.Kind == EntryKind.Regex)
                    {
                        try { _ = new Regex(e.Key); }
                        catch (Exception ex) { Console.WriteLine($"{where}: ошибка в регулярке: {ex.Message}"); problems++; }
                    }
                    int keyNl = e.Key.Count(c => c == '\n'), valNl = e.Value.Count(c => c == '\n');
                    if (e.Kind != EntryKind.Regex && keyNl > 0 && keyNl <= 3 && valNl != keyNl && !e.Source.Contains("tutorial"))
                    {
                        Console.WriteLine($"{where}: разное число строк в оригинале ({keyNl + 1}) и переводе ({valNl + 1}): «{Short(e.Key)}»");
                    }
                }
            }
            Console.WriteLine(problems == 0 ? "Ошибок не найдено." : $"Найдено проблем: {problems}");
            return problems == 0 ? 0 : 1;
        }

        private static int Test(string dir, string[] texts)
        {
            var tr = LoadTranslator(dir, out _);
            tr.OnUntranslated = s => Console.WriteLine("  [нет перевода] " + s);
            foreach (var t in texts)
            {
                string input = t.Replace("\\n", "\n");
                Console.WriteLine("EN: " + input);
                Console.WriteLine("RU: " + tr.Translate(input));
            }
            return 0;
        }

        private static int Stats(string dir)
        {
            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var entries = ReadRawEntries(File.ReadAllText(file)).ToList();
                int translated = TranslationFile.Load(file).Count;
                double pct = entries.Count == 0 ? 0 : 100.0 * translated / entries.Count;
                Console.WriteLine($"{Path.GetRelativePath(dir, file),-40} {translated,6} / {entries.Count,-6} {pct,5:0.0}%");
            }
            return 0;
        }

        /// <summary>
        /// Переводит все .lua из папки так же, как плагин, сохраняет результат и проверяет синтаксис
        /// интерпретатором MoonSharp из папки игры (Managed/MoonSharp.Interpreter.dll).
        /// </summary>
        private static int TestLua(string tutorialDictDir, string luaDir, string outDir)
        {
            var tr = new Translator();
            foreach (var f in Directory.GetFiles(tutorialDictDir, "*.txt", SearchOption.AllDirectories))
                tr.Add(TranslationFile.Load(f));

            string managed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(luaDir).TrimEnd('\\', '/')), "..", "Managed");
            System.Reflection.Assembly moon = null;
            try { moon = System.Reflection.Assembly.LoadFrom(Path.Combine(managed, "MoonSharp.Interpreter.dll")); }
            catch (Exception ex) { Console.WriteLine("MoonSharp не загружен, синтаксис не проверяется: " + ex.Message); }

            Directory.CreateDirectory(outDir);
            int errors = 0;
            foreach (var file in Directory.GetFiles(luaDir, "*.lua"))
            {
                string src = File.ReadAllText(file);
                string result = LuaStrings.Replace(src, lit =>
                    lit.Key.Length > 0 && Translator.HasLatinLetter(lit.Key) && tr.TryGetExact(lit.Key, out var v) ? v : null, out int n);
                File.WriteAllText(Path.Combine(outDir, Path.GetFileName(file)), result, new UTF8Encoding(false));
                string status = "ok";
                if (moon != null)
                {
                    try
                    {
                        var scriptType = moon.GetType("MoonSharp.Interpreter.Script");
                        var script = Activator.CreateInstance(scriptType);
                        var load = scriptType.GetMethods().First(m => m.Name == "LoadString" && m.GetParameters().Length == 3);
                        load.Invoke(script, new object[] { result, null, Path.GetFileName(file) });
                    }
                    catch (System.Reflection.TargetInvocationException ex)
                    {
                        status = "ОШИБКА: " + ex.InnerException?.Message;
                        errors++;
                    }
                }
                Console.WriteLine($"{Path.GetFileName(file),-28} заменено строк: {n,4}   синтаксис: {status}");
            }
            return errors == 0 ? 0 : 1;
        }

        /// <summary>
        /// Делает из сборок игры референсные: остаются типы и сигнатуры, тела методов заменяются на «throw null»,
        /// ресурсы удаляются. Этого достаточно, чтобы скомпилировать плагин в CI, не храня код игры.
        /// Подписанные сборки (netstandard.dll) копируются как есть.
        /// </summary>
        private static int MakeRefs(string managedDir, string outDir, string[] names)
        {
            Directory.CreateDirectory(outDir);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            long before = 0, after = 0;
            foreach (var raw in names)
            {
                string name = raw.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".dll";
                string src = Path.Combine(managedDir, name), dst = Path.Combine(outDir, name);
                before += new FileInfo(src).Length;
                using (var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
                {
                    if (asm.Name.HasPublicKey && (asm.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0)
                    {
                        File.Copy(src, dst, true);
                    }
                    else
                    {
                        foreach (var type in asm.MainModule.GetTypes())
                        {
                            foreach (var method in type.Methods)
                            {
                                if (!method.HasBody) continue;
                                var body = method.Body;
                                body.Instructions.Clear();
                                body.ExceptionHandlers.Clear();
                                body.Variables.Clear();
                                body.InitLocals = false;
                                var il = body.GetILProcessor();
                                il.Append(il.Create(OpCodes.Ldnull));
                                il.Append(il.Create(OpCodes.Throw));
                            }
                        }
                        asm.MainModule.Resources.Clear();
                        asm.Write(dst);
                    }
                }
                after += new FileInfo(dst).Length;
                Console.WriteLine($"{name,-45} {new FileInfo(src).Length / 1024,7} КБ → {new FileInfo(dst).Length / 1024,7} КБ");
            }
            Console.WriteLine($"Итого: {before / 1024} КБ → {after / 1024} КБ → {outDir}");
            return 0;
        }

        private static Translator LoadTranslator(string dir, out int files)
        {
            var tr = new Translator { Warn = m => Console.Error.WriteLine("ПРЕДУПРЕЖДЕНИЕ: " + m) };
            files = 0;
            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                tr.Add(TranslationFile.Load(file));
                files++;
            }
            return tr;
        }

        private static string Short(string s)
        {
            s = s.Replace("\n", "⏎");
            return s.Length > 70 ? s.Substring(0, 67) + "..." : s;
        }
    }
}
