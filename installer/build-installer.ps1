# Genera el instalador de Tesela (installer\Output\tesela-setup-v<versión>.exe).
#
# 1. Compila Tesela y la ventana de ajustes (build.ps1).
# 2. Prepara installer\staging con lo que se instala:
#    - Tesela.exe, la tipografía Figtree, el icono de la bandeja y los sensores (bin\);
#    - la ventana de ajustes publicada autocontenida (con .NET 8 y Windows App SDK dentro, para que funcione
#      en cualquier Windows 10/11 sin instalar nada más), sin los componentes de Windows App SDK que no usa;
#    - la configuración inicial (defaults.json) y los avisos de licencias.
#    Los iconos de macOS de icons\ NO se incluyen (son de Apple): Tesela usa entonces sus iconos propios.
# 3. Compila installer\tesela.iss con Inno Setup 6.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$staging = "$PSScriptRoot\staging"

# Tesela tiene que estar cerrado. El proceso de sensores (con permisos) no se puede cerrar desde aquí,
# pero se cierra solo unos segundos después que Tesela.
foreach ($p in Get-Process Tesela, TeselaSettings -ErrorAction SilentlyContinue) {
    try { $p | Stop-Process -Force -ErrorAction Stop } catch { }
}
$t = 0
while ((Get-Process Tesela -ErrorAction SilentlyContinue) -and $t -lt 30) { Start-Sleep 1; $t++ }
& powershell -ExecutionPolicy Bypass -File "$root\build.ps1"
if ($LASTEXITCODE -ne 0) { throw "Error al compilar" }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force "$staging\icons", "$staging\fonts", "$staging\sensors" | Out-Null
Copy-Item "$root\bin\Tesela.exe" $staging
Copy-Item "$root\bin\fonts\*" "$staging\fonts\"
Copy-Item "$root\bin\icons\tesela-tray.ico" "$staging\icons\"
Copy-Item "$root\bin\sensors\*" "$staging\sensors\"
Copy-Item "$PSScriptRoot\defaults.json" $staging
Copy-Item "$root\THIRD-PARTY-NOTICES.md" $staging

# Ventana de ajustes autocontenida
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
& $dotnet publish "$root\settings\TeselaSettings.csproj" -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -o "$staging\settings" --nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "Error al publicar la ventana de ajustes" }
# Fuera lo que trae Windows App SDK y la ventana no usa (IA, ML, visión, WebView2, widgets del sistema, OAuth)
$unused = 'DirectML.dll', 'onnxruntime*.dll', 'Microsoft.Windows.AI.*', 'Microsoft.Windows.Internal.Vision.winmd',
    'Microsoft.Windows.Vision*.winmd', 'Microsoft.Graphics.Imaging*', 'Microsoft.Graphics.ImagingInternal*',
    'Microsoft.Graphics.Internal.Imaging.winmd', 'Microsoft.Web.WebView2.*', 'WebView2Loader.dll',
    'Microsoft.Windows.Widgets.*', 'Microsoft.Security.Authentication.OAuth.*'
foreach ($p in $unused) { Get-ChildItem "$staging\settings" -Filter $p -File | Remove-Item -Force }
Remove-Item "$staging\settings\NpuDetect" -Recurse -Force -ErrorAction SilentlyContinue
# Textos de los controles de WinUI solo en español e inglés (los idiomas de Tesela)
Get-ChildItem "$staging\settings" -Directory | Where-Object {
    $_.Name -notin 'Assets', 'Fonts', 'Microsoft.UI.Xaml', 'es-ES', 'es-MX', 'en-US', 'en-us' -and
    (Get-ChildItem $_.FullName -Filter *.mui -File -ErrorAction SilentlyContinue)
} | Remove-Item -Recurse -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "No se encontró Inno Setup 6 (ISCC.exe)" }
& $iscc /Q "$PSScriptRoot\tesela.iss"
if ($LASTEXITCODE -ne 0) { throw "Error al compilar el instalador" }
Get-Item "$PSScriptRoot\Output\tesela-setup-v*.exe" | ForEach-Object { "OK -> $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
