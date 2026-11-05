# Altium Library Manager

A Windows app for the Access databases behind Altium DbLib libraries. Browse the tables, see each component's symbol, footprint and 3D model, and change the rows, without opening Access or Altium.

## Features

- Preview of symbols, footprints and 3D models (STEP) for the selected row
- Live search across all fields or chosen columns, and sorting by several columns
- Editing with Save, Undo and Redo: nothing reaches the database until you save
- Spreadsheet-like cells: multi-cell selection, copy and paste with Excel, fill, fill down
- **Add elements**: many similar parts at once from E series or value lists, with 27 element types
- Table templates and new tables, with `.json` import and export
- Deleted tables are kept as copies and can be restored
- Light and dark themes, layouts, and built-in help (F1)

## Install

Download `AltiumLibraryManager-x.y.z-Setup.exe` from [Releases](../../releases) and run it. It needs no administrator rights and no .NET installation.

The app needs:

- Windows 10 or 11, 64-bit
- The **64-bit Microsoft Access Database Engine**. It comes with 64-bit Microsoft Access; otherwise install *Microsoft Access Database Engine 2016 Redistributable* (`accessdatabaseengine_X64.exe`) from Microsoft.
- Microsoft Edge WebView2 for the 3D view (part of Windows 11)

The installer isn't code-signed yet, so Windows SmartScreen may warn about it: choose **More info**, then **Run anyway**.

## Build

You need the .NET 10 SDK.

```
dotnet build "Altium Library Manager.slnx"
```

`build-release.ps1 -Version 1.0.0` makes the self-contained program and, with [Inno Setup 6](https://jrsoftware.org/isinfo.php), the installer, in `installer/output`.

## Tests

`Altium Library Manager.Tests` is a console program that runs the tests against temporary databases, so it needs the Access Database Engine too. It writes nothing to your own databases or settings.

```
dotnet run --project "Altium Library Manager.Tests"
```

## Releases

Pushing a tag such as `v1.0.0` builds the installer and publishes a GitHub release (`.github/workflows/release.yml`).

## Credits

Built on the libraries listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). This project isn't affiliated with or endorsed by Altium. Altium is a trademark of its owner.

## License

[MIT](LICENSE). The third-party components have their own licenses.
