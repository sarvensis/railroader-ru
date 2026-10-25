# Русификатор Railroader

Неофициальный русский перевод [Railroader](https://store.steampowered.com/app/1683150/Railroader/):
интерфейс, обучение (туториал) и справочник (F1). Плагин для BepInEx 5, файлы игры не изменяются.

Инструкция для игроков — [docs/INSTALL.ru.md](docs/INSTALL.ru.md) (краткая версия попадает в архив как `README.txt`).

## Как это устроено

| Что | Где берётся текст | Как переводится |
|---|---|---|
| Интерфейс | C#-код игры, сцены Unity, ScriptableObject (грузы, этапы) | Harmony-патчи на `TMP_Text.text`, `SetText(...)`, `OnEnable` подменяют строку по словарю |
| Склонения | `Core.PluralizeExtensions.Pluralize("car")` | патч выдаёт «3 вагона» по записям `p:` |
| Обучение | `StreamingAssets/Tutorial/*.lua` | при чтении Lua заменяются только строковые литералы, найденные в словаре; код не трогается |
| Справочник | `StreamingAssets/Guide/**/*.md` | подменяется файлом из `StreamingAssets/` плагина; якоря ссылок строятся по оригинальным заголовкам |
| Шрифты | динамические шрифты игры; в Montserrat кириллица есть, в LibreBaskerville / Inconsolata — нет | шрифтам без кириллицы первым fallback ставится Montserrat самой игры (TTF плагина — опция `BundledFonts`, экспериментально) |

Если игра обновится, непереведённым станет только изменившийся текст — остальное продолжит работать.
Каждый Harmony-патч применяется отдельно: сломанный патч пишет ошибку в лог, но не отключает остальные.

## Структура

```
src/RailroaderRu/          плагин (net472)
  Translator.cs            поиск перевода: точное совпадение → шаблоны → разбор по строкам и тегам
  TranslationFile.cs       формат словарей
  TextHooks.cs             патчи TextMeshPro и Pluralize
  ContentHooks.cs          туториал (Lua) и справочник (Markdown)
  FontManager.cs           кириллические fallback-шрифты
  LuaStrings.cs            лексер строковых литералов Lua
tools/RuTools/             утилиты переводчика (net6.0)
translation/
  translations/*.txt       словари интерфейса
  translations/tutorial/   словарь туториала
  StreamingAssets/Guide/   переведённые страницы справочника
fonts/                     TTF-шрифты (OFL)
scripts/                   build.ps1, pack-refs.ps1, get-bepinex.ps1, install/uninstall.ps1, README.txt для игроков
docs/                      инструкция для игроков, текст релиза
vendor/                    classdata.tpk (для extract-assets); сюда же скачивается BepInEx
extracted/                 извлечённые из игры исходные строки (рабочие файлы)
```

## Формат словарей

```
// комментарий
Original=Перевод
t:Day {0:n}=День {0}                          шаблон; {0:n} (и {0:N0}, {0:C0}...) — только число
t:{0:n} cars={0} {0|вагон|вагона|вагонов}      выбор формы по числу
r:^(\d+) minutes? ago$={1} {1|минуту|минуты|минут} назад   регулярное выражение
p:car=вагон|вагона|вагонов                    склонение для Pluralize()
<<<
многострочный оригинал
===
многострочный перевод
>>>
```

В однострочных записях `\n` — перевод строки, `\=` — знак «=» в оригинале. Пустой перевод = строка не переведена.
Подставленные в шаблон фрагменты тоже переводятся (например, название груза в «Running low on {0}»).

## Сборка

Нужен .NET SDK 6+ и установленная игра (для ссылок на её сборки). BepInEx скачается сам
(`scripts/get-bepinex.ps1`, с проверкой SHA-256).

```bash
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

Результат — `dist/RailroaderRu-<версия>.zip`. Если игра не в стандартной папке Steam: `-GameDir "D:\Games\Railroader"`.
Версия задаётся в одном месте — `<VersionPrefix>` в [RailroaderRu.csproj](src/RailroaderRu/RailroaderRu.csproj).

## CI (GitHub Actions)

[.github/workflows/build.yml](.github/workflows/build.yml) работает на `ubuntu-latest`:

| Событие | Что происходит |
|---|---|
| pull request, push в ветку | `check` — проверка словарей; `build` — архив `RailroaderRu-<версия>-dev.<N>.zip` в артефактах запуска (30 дней) |
| тег `v1.2.3` | то же с версией `1.2.3` + GitHub Release с архивом и текстом из [docs/RELEASE.md](docs/RELEASE.md) |

**Сборки игры в репозиторий не кладутся.** Компилятору нужны только сигнатуры, поэтому
[scripts/pack-refs.ps1](scripts/pack-refs.ps1) делает из DLL игры урезанные референсные сборки
(тела методов заменены на `throw null`, ресурсы удалены). Они хранятся в приватном репозитории
[railroader-ru-refs](https://github.com/sarvensis/railroader-ru-refs): там же скрипт `update.ps1` и workflow,
который проверяет сборки и публикует релиз `game-refs-<версия игры>`. Отсюда CI скачивает его через
`gh release download`. Подробности — в README того репозитория.

Настройки этого репозитория (Settings → Secrets and variables → Actions):

- Variable `REFS_REPO` = `sarvensis/railroader-ru-refs`
- Secret `REFS_TOKEN` — fine-grained token с доступом только к `railroader-ru-refs`, **Contents: Read-only**

После обновления Railroader: запустите `update.ps1 -Push` в `railroader-ru-refs`, дождитесь его workflow
**publish** и поменяйте `GAME_REFS_VERSION` в `build.yml`.

Выпуск версии:

```bash
git tag v0.2.0
git push origin v0.2.0
```

Релиз появится на вкладке **Releases** с архивом для игроков.

## Обновление после патча игры

```bash
dotnet run --project tools/RuTools -c Release -- extract-dll extracted/code.txt "<игра>/Railroader_Data/Managed/Assembly-CSharp.dll" "<игра>/Railroader_Data/Managed/Definition.dll"
dotnet run --project tools/RuTools -c Release -- extract-assets extracted/scenes.txt "<игра>/Railroader_Data" vendor/classdata.tpk
dotnet run --project tools/RuTools -c Release -- extract-lua extracted/tutorial.txt "<игра>/Railroader_Data/StreamingAssets/Tutorial"
dotnet run --project tools/RuTools -c Release -- update extracted/code.txt translation/translations/50_new.txt
dotnet run --project tools/RuTools -c Release -- update extracted/tutorial.txt translation/translations/tutorial/tutorial.txt
```

`update` дописывает в файл только строки, которых ещё нет ни в каком виде (с пустым переводом).
Справочник: сравните `StreamingAssets/Guide` игры с прошлой версией и поправьте изменившиеся страницы
(число и порядок заголовков `#`/`##`/`###` должны совпадать с оригиналом — по ним строятся ссылки).

Другие команды:

- `check translation/translations` — ошибки: дубликаты, потерянные плейсхолдеры, несовпадающие теги
- `test translation/translations "Day 12" "3 cars"` — показать перевод строки
- `test-lua translation/translations/tutorial "<игра>/.../Tutorial" <папка>` — перевести Lua и проверить синтаксис MoonSharp из игры
- `fill extracted/tutorial.txt <переводы по номерам> <результат>` — подставить переводы туториала по номерам `#N`
- `stats translation/translations` — процент переведённого

В игре: `DumpUntranslated = true` в `BepInEx/config/ru.railroader.localization.cfg` пишет все встреченные
непереведённые строки в `BepInEx/plugins/RailroaderRu/untranslated.txt`; `HotReload` перечитывает словари при сохранении.

## Известные ограничения

- Контекст не учитывается: одно английское слово переводится одинаково везде («Clear» — «Свободно» и у сигнала, и у кнопки).
- Строки, склеенные в коде из нескольких кусков, переводятся только при наличии шаблона; такие куски видны в `99_untranslated.txt`.
- Названия станций, предприятий и таблички на путях намеренно оставлены на английском.
- Если Mono JIT встроит (inline) `Pluralize` в вызывающий код, патч склонения не сработает — тогда выручают шаблоны `t:{0:n} cars=...`.

## Лицензии

BepInEx — LGPL 2.1. Шрифты Montserrat, PT Serif, PT Mono — SIL OFL 1.1 (тексты в `fonts/`).
Railroader © Giraffe Lab LLC.
