# Compila Tesela.exe con el compilador Roslyn de Visual Studio Build Tools
# contra .NET Framework 4.8 (incluido en Windows 10/11). No necesita el SDK de .NET.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio", "$env:ProgramFiles\Microsoft Visual Studio" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\Roslyn\csc.exe' } | Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw 'No se encontro csc.exe (Roslyn). Instala Visual Studio Build Tools.' }

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$winmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata" -Recurse -Filter Windows.winmd |
    Where-Object { $_.FullName -notlike '*\Facade\*' } |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

$refs = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Drawing.dll',
    'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Web.Extensions.dll',
    'System.Net.Http.dll', 'System.Management.dll', 'System.Runtime.dll', 'System.IO.dll', 'System.Threading.Tasks.dll',
    'System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll',
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll'
) | ForEach-Object { "-r:$fw\$_" }
$refs += "-r:$winmd"
# Sensores integrados: LibreHardwareMonitorLib (solo la usa el proceso auxiliar; se carga de bin\sensors)
$refs += "-r:$root\lib\sensors\LibreHardwareMonitorLib.dll"

New-Item -ItemType Directory -Force "$root\bin" | Out-Null
$src = Get-ChildItem "$root\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }

& $csc -nologo -noconfig -nostdlib -target:winexe -langversion:latest -optimize+ -platform:anycpu `
    -win32manifest:"$root\src\app.manifest" -win32icon:"$root\settings\Assets\Tesela.ico" -out:"$root\bin\Tesela.exe" -nowarn:1701,1702 @refs @src
if ($LASTEXITCODE -ne 0) { throw "Error de compilacion ($LASTEXITCODE)" }

# Iconos de macOS (icons\*.ico -> bin\icons)
if (Test-Path "$root\icons") {
    New-Item -ItemType Directory -Force "$root\bin\icons" | Out-Null
    Copy-Item "$root\icons\*.ico" "$root\bin\icons\" -Force
}

# Tipografía Figtree, SIL OFL 1.1 (se carga desde bin\fonts; si falta, se usa Segoe UI)
if (Test-Path "$root\fonts") {
    New-Item -ItemType Directory -Force "$root\bin\fonts" | Out-Null
    # Restos de la tipografía anterior
    Get-ChildItem "$root\bin\fonts\*.otf" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    foreach ($f in Get-ChildItem "$root\fonts\*" -Include *.ttf, OFL.txt) {
        $dst = "$root\bin\fonts\$($f.Name)"
        # Si Tesela está abierto tiene las fuentes en uso: solo se copian si cambian.
        if (-not (Test-Path $dst) -or (Get-Item $dst).Length -ne $f.Length) { Copy-Item $f.FullName $dst -Force }
    }
}
# Librerías de los sensores integrados (lib\sensors -> bin\sensors). Si el auxiliar está en marcha las
# tiene en uso: solo se copian si cambian.
if (Test-Path "$root\lib\sensors") {
    New-Item -ItemType Directory -Force "$root\bin\sensors" | Out-Null
    foreach ($f in Get-ChildItem "$root\lib\sensors\*") {
        $dst = "$root\bin\sensors\$($f.Name)"
        if (-not (Test-Path $dst) -or (Get-Item $dst).Length -ne $f.Length) { Copy-Item $f.FullName $dst -Force }
    }
}
# Ventana de ajustes (WinUI 3, .NET 8): settings\ -> bin\settings
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
if ((Test-Path "$root\settings\TeselaSettings.csproj") -and (Test-Path $dotnet)) {
    & $dotnet build "$root\settings\TeselaSettings.csproj" -c Release --nologo -v q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Error de compilacion de la ventana de ajustes ($LASTEXITCODE)" }
    $out = "$root\settings\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
    robocopy $out "$root\bin\settings" /MIR /R:1 /W:1 /NJH /NJS /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "No se pudo copiar la ventana de ajustes (robocopy $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}
Write-Host "OK -> $root\bin\Tesela.exe"
