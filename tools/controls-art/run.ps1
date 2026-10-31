# Regenerates assets/vita-controls.png (640x340) and assets/manual-002.png (960x544) from Draw.cs. Windows PowerShell 5.1, System.Drawing.
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = [IO.Path]::GetFullPath((Join-Path $d "..\..\assets"))
$src = [IO.File]::ReadAllText("$d\Draw.cs")
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing -Language CSharp
[ControlsArt]::Run((Join-Path $assets "vita-controls.png"), $null)
[ControlsArt]::RunManual((Join-Path $assets "manual-002.png"))
"ok"
