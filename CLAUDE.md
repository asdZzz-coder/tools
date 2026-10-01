# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

「簡易記帳」— a Windows personal-finance app (WPF, .NET 10, `net10.0-windows`) ported from an earlier Python/Tkinter version. Single project `ZZZ/ZZZ.csproj` (namespace `ZZZ`, but `AssemblyName` is `Ledger`, so the exe is `Ledger.exe`). All UI text and code comments are Traditional Chinese.

## Commands

Run from `ZZZ/` (the project folder):

```bash
dotnet build
dotnet run
```

Publishing (both are single-file and framework-dependent, so the user needs the .NET 10 Desktop Runtime):
- ClickOnce needs full Visual Studio MSBuild, not `dotnet publish`: `msbuild ZZZ\ZZZ.csproj -restore -t:Publish -p:PublishProfile=ClickOnceProfile -p:Configuration=Release`. The profile is `ZZZ/Properties/PublishProfiles/ClickOnceProfile.pubxml`, and the output goes to `ZZZ/bin/Release/net10.0-windows/win-x64/app.publish/`. Locally, MSBuild is at `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe`.
- Portable exe: `dotnet publish ZZZ\ZZZ.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`. Use `-p:SelfContained=false`. With the .NET 10 CLI, `--self-contained false` still bundles the runtime (~140 MB exe). `IncludeNativeLibrariesForSelfExtract` folds SQLite's `e_sqlite3.dll` into the exe.

Releasing: pushing a `vX.Y.Z` tag (`git tag v1.2.0; git push origin v1.2.0`) runs `.github/workflows/release.yml` on the `windows-2025-vs2026` runner. It builds both packages with the version taken from the tag (`-p:Version` and `-p:ApplicationVersion=X.Y.Z.0`) and publishes them to a GitHub Release. ClickOnce auto-update is disabled because Release assets have no stable update URL. Instead, the in-app `Updater` polls the latest Release of `asdZzz-coder/tools`, which must stay public.

There is no test project or linter. To run the app against throwaway data instead of the user's real database, set `LEDGER_DATA_DIR` before launching (PowerShell: `$env:LEDGER_DATA_DIR="C:\some\temp\dir"; dotnet run`). The build fails with a locked-file error if a `Ledger.exe` instance is still running.

## Architecture

- **Data compatibility is a hard constraint.** `Database.cs` uses the same SQLite file location (`%APPDATA%\Ledger\ledger.db`) and schema as the original Python app, so existing user data must keep working. Schema changes go in `Database.Init()` as additive `CREATE TABLE IF NOT EXISTS` / guarded `ALTER TABLE` migrations (see the `wallet_id` upgrade). Don't rename or drop columns. Amounts are `REAL`, dates are `TEXT` in `yyyy-MM-dd`, and record type / debt direction are stored as the Chinese literals in the `Database.Income/Expense/IOwe/TheyOwe` constants.
- **`Database`** is a thin wrapper over one `SqliteConnection`. SQL uses positional parameters `@p0, @p1, …` matched to the `params` args of `Exec/Scalar/Query`. Multi-statement writes use `InTransaction(...)`, which sets the `tx` field so every command it creates joins that transaction (Microsoft.Data.Sqlite requires this). Read models are C# `record`s (`RecordRow`, `DebtRow`, `GoalRow`, …) that hold computed display properties (`AmountText`, `Status`, `Hint`, …), and XAML binds to those directly.
- **No MVVM.** `MainWindow.xaml.cs` is code-behind. Each tab (`RecordsView` / `DebtsView` / `GoalsView`, switched by the `Tab*` radio buttons) has a `Refresh*()` method that re-queries the DB and resets `ItemsSource` and summary text. After any write, call the matching refresh. `ReloadEverything()` reloads all tabs and is used after an import. Savings-goal balances are separate from wallet balances.
- **Import/export** lives in `DataTransfer.cs`:
  - The JSON full backup is a generic dump of every table listed in `Database.Tables`. Importing it replaces all data via `Database.ReplaceAll`, which accepts only known tables/columns, and `AutoBackup` (`VACUUM INTO`) saves the current data first. **A new table must be added to `Database.Tables`** or it won't be backed up.
  - CSV import appends records, auto-creates missing wallets/categories, and falls back to Big5 when the file isn't valid UTF-8.
- **Dialogs**: don't use `MessageBox`. Use the static helpers on `DialogWindow` (`Info/Success/Error/Confirm/Prompt/Choose`). `Prompt` takes `Field[]` and an optional validator that returns an error string, so the dialog stays open on invalid input. While a dialog is open it dims `MainWindow` via `SetDim`.
- **Styling**: everything is in `Themes.xaml` (merged in `App.xaml`). Use its brush keys (`InkBrush`, `IncBrush`/`ExpBrush` for income/expense, `*SoftBrush` backgrounds) and named styles (`AccentButton`, `GhostButton`, `OutlineButton`, `DangerButton`, `IconButton`, `Card`, `Pill`, `Segment`, `TabRadio`, …). The color rule: the chrome is ink/grayscale, and color is reserved for meaning (green = income/achieved, red = expense/danger, orange = overdue). Icons are glyphs from `Segoe Fluent Icons`/`Segoe MDL2 Assets` via the `Icon` TextBlock style. Implicit styles restyle `TextBox` (its `Tag` is placeholder text), `ComboBox`, `DatePicker`, `DataGrid`, and `ScrollBar`.
- **Culture**: `App.OnStartup` sets a zh-TW culture whose `ShortDatePattern` is `yyyy-MM-dd`, so `DatePicker` and parsing use the DB date format. Amounts are parsed with `DialogWindow.TryParseAmount` (invariant culture, commas allowed).
- **Updates**: `Updater.cs` checks GitHub Releases. The current version comes from the assembly version: `<Version>` in the csproj, overridden by CI from the tag.
