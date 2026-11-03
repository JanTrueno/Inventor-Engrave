# Inventor Engrave

Small Autodesk Inventor add-in that engraves the part number, optionally after the value of an iProperty (such as an
order number), into parts. It walks through the parts of an assembly, the sheets of a drawing, or works on the open
part. For sheet metal it shows which side is the top. The text is placed as a sketch where you click. You drag or turn
it in Inventor's sketch environment, and it is then engraved with Inventor's Emboss (Engrave from Face). The panel is
built in Inventor's own style and follows your theme, like the Tab and Slot add-in.

It replaces the "Graveren" iLogic rules (assembly, part and drawing versions) and uses the same feature and sketch
names (`Graveer`, `GraveerSketch`), so engravings made with those rules are found, listed and replaced too.

## Features

- **Assembly**: every visible part that can be changed is listed once, also when it is used several times (the
  engraving is in the part file). Sheet metal parts come first. Hidden and suppressed parts are left out, and so are
  read-only library and Content Center parts. Pick any part in the list, or **Skip** to the next one. A checkmark
  shows which parts are engraved: bright for this run, dim for earlier.
- **Drawing**: every sheet is one row, for the part in its first view. That part opens in its own window to pick the
  face, and the text is `part number - sheet number`. The windows are closed again at the end.
- **Top side found for you** (sheet metal): the side with the inside of most bends ("bends up") is shown in green and
  only those faces can be picked. If the sides cannot be told apart, every face can be picked. A setting turns this
  around if your CAM software sees the other side as the top.
- **Tubes and round faces**: on a round face the sketch goes on a work plane touching the face where you click, and
  the text is wrapped onto the face. Flat faces of any part work as well.
- **Your own text**: the part number, cut off after a number of characters you set (**Name length**, 20 by default).
  Tick **Put an iProperty in front of the name** and pick it with the **▾** button (Summary, Project, Status, or
  Custom with the custom iProperties of the open document and their values), or type any name, to put its value in
  front (for example `ordernummer`). A **Placeholder** value in the settings (for example `160100`) means "not filled in yet" and is refused.
  You can still change the text per part before you click a face.
- **Place it yourself**: the text starts where you click, along the longest straight edge and readable from your view.
  Drag it or turn it (Sketch → Rotate), then **Finish Sketch** in Inventor or in the panel. **Cancel** while
  sketching drops that text only.
- **Existing engravings**: listed with their text. **Edit** opens the text sketch to move or change the text,
  **Delete** removes one engraving, **Delete All** removes them all.
- **Cancel**: closes the panel and undoes everything Engrave did since you started it.
- **Safe depth**: 0.5 mm by default, never more than a quarter of the thickness for sheet metal.
- Nothing is saved for you.

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

1. Click **Engrave**: on the **Assemble** tab of an assembly, the **Sheet Metal** or **3D Model** tab of a part, or
   the **Place Views** tab of a drawing.
2. Optionally tick **Put an iProperty in front of the name** and pick the iProperty with **▾**. It is remembered.
3. The first part is highlighted in orange (for sheet metal, the top side in green). Click the face for the text.
4. Drag the text into place, turn it if needed, and click **Finish Sketch**. It is engraved and the next part is up.
5. Click **Done** when you are finished, and save. **Cancel** undoes the whole run.

The settings are kept in `%APPDATA%\Engrave\settings.json`. A log is written to `%LOCALAPPDATA%\Engrave\log.txt`.

## License

GPL-3.0, see [LICENSE](LICENSE). The section chevrons, list icons and the ▾ button come from Autodesk Weave (Apache-2.0), see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
