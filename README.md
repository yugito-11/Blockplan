# Blockplan (Windows-Programm)

**Herunterladen:** [Blockplan.exe](https://github.com/yugito-11/blockplan/releases/latest/download/Blockplan.exe) (alle Versionen unter [Releases](https://github.com/yugito-11/blockplan/releases)) – speichern und doppelklicken, keine Installation nötig. Windows warnt beim ersten Start, weil die Datei nicht signiert ist: „Weitere Informationen“ → „Trotzdem ausführen“.

Bauplaner für Minecraft als eigenes Programm – kein Browser, keine Webansicht.
C# mit Avalonia 12, 3D über OpenGL, Projekte in einer eigenen SQLite-Datenbank.

## Was die erste Version kann
- Schematics öffnen: `.schem` (WorldEdit/Sponge), `.litematic`, `.nbt` (Strukturblock) – auch per Ziehen ins Fenster
- Ausschnitt wählen, Wasser entfernen, drehen
- 2D-Plan: Draufsicht oder einzelne Schicht, echte Texturen beim Hineinzoomen, Chunk-Linien
- 3D-Ansicht: nur sichtbare Seiten, gleiche Flächen zusammengefasst – auch für sehr große Bauten
- Materialliste mit deutschen Namen, Stacks und Shulkerkisten
- Minecraft-Texturen automatisch aus `%APPDATA%\.minecraft\versions` oder einmalig von Mojang
- Import der Projekte aus dem Blockplan-Artefakt (Export-Knopf dort unter Einstellungen → Projekt)
- **An Bautrupp senden:** Etappen, Baupläne oder Ebenen als Aufgaben in eine [Bautrupp](https://github.com/yugito-11/bautrupp)-Gruppe schicken (gleiches Format wie der Schematic-Import der Bautrupp-Webseite). Mit Baustellen-Koordinaten zählt die Bautrupp-Mod Block für Block nach Plan mit.

## Wo die Daten liegen
`%LOCALAPPDATA%\Blockplan\blockplan.db` (Projekte und Einstellungen), daneben die heruntergeladene Spieldatei.

## Bauen
```
dotnet publish src/Blockplan -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```
Selbsttest ohne Fenster: `Blockplan --selftest <ordner mit titanic.schem, titanic.json, enterprise.json>`
