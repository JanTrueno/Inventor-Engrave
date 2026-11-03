# Inventor Engrave

Small Autodesk Inventor add-in that engraves the part number, and optionally the order number, into sheet metal
parts. It walks through all sheet metal parts of an assembly (or works on the open part), shows which side is the top,
puts the text as a sketch where you click, lets you drag or turn it in Inventor's sketch environment and then cuts it
in shallowly. The panel is built in Inventor's own style and follows your theme, like the Tab and Slot add-in.

It replaces the "Graveren" iLogic rule and uses the same feature and sketch names (`Graveer`, `GraveerSketch`), so
engravings made with the rule are found, listed and replaced too.

## Features

- **Whole assembly in one go**: every visible sheet metal part is listed once, also when it is used several times
  (the engraving is in the part file). Hidden and suppressed parts are left out. Pick any part in the list, or
  **Skip** to the next one.
- **Top side found for you**: the side with the inside of most bends ("bends up") is shown in green and only those
  faces can be picked. If the sides cannot be told apart, every flat face can be picked. A setting turns this around
  if your CAM software sees the other side as the top.
- **The right text**: `<last 4 digits of the order number> <part number>`, with the part number cut off after 20
  characters (the same as the STEP export). The order number comes from the custom iProperty `ordernummer`;
  `160100` means it is not filled in yet and is refused. You can change the text per part before you click a face.
- **Place it yourself**: the text starts where you click, along the longest straight edge and readable from your
  view. Drag it or turn it (Sketch → Rotate), then **Finish Sketch** in Inventor or in the panel. **Cancel** leaves
  the part as it was, including any engraving it had.
- **Existing engravings**: listed with their text. **Edit** opens the text sketch to move or change the text,
  **Delete** removes one engraving, **Delete All** removes them from every part in the assembly.
- **Safe depth**: 0.5 mm by default, never more than a quarter of the sheet thickness.
- **Undo**: every engraving, edit or delete is one undo step. Nothing is saved for you.

## Requirements

- Windows with Autodesk Inventor 2025 (the add-in runs on .NET 8)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build it

## Building

The project references `Autodesk.Inventor.Interop.dll` from the Inventor installation at
`C:\Program Files\Autodesk\Inventor 2025\Bin\Public Assemblies`. If Inventor is installed somewhere else, change the
`HintPath` in `Engrave.csproj` to match.

```powershell
dotnet build Engrave.csproj -c Release
```

The output goes to `bin\Release\net8.0-windows\`: `Engrave.dll`, `.pdb` and `.deps.json`.

## Install

Close Inventor (it locks the add-in DLL while running), then run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

This builds the add-in and installs it as an app bundle in `%APPDATA%\Autodesk\ApplicationPlugins\Engrave.bundle`.

- `install.ps1 -SkipBuild` installs the last build without building again.
- `install.ps1 -Remove` uninstalls it.

## Usage

1. Open an assembly, or a sheet metal part, and click **Engrave** on the **Assemble** tab (assembly) or the
   **Sheet Metal** tab (part).
2. Check the order number, or untick **Include in the text**.
3. The first part is highlighted in orange, its top side in green. Click a green face where the text should go.
4. Drag the text into place, turn it if needed, and click **Finish Sketch**. The cut is made and the next part is up.
5. Click **Done** when you are finished, and save.

The settings are kept in `%APPDATA%\Engrave\settings.json`. A log is written to `%LOCALAPPDATA%\Engrave\log.txt`.

## License

GPL-3.0, see [LICENSE](LICENSE). The section chevrons and list icons come from Autodesk Weave (Apache-2.0), see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
