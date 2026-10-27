# Third‑party notices

Tesela includes or uses the following third‑party components. Each one keeps its own license; the
source code of the MPL 2.0 components is available at the links below, and they are shipped unmodified.

## Fonts

**Figtree** — © The Figtree Project Authors (https://github.com/erikdkennedy/figtree)
Licensed under the SIL Open Font License, Version 1.1 (`fonts/OFL.txt`). Tesela ships static instances
(Regular, Medium, SemiBold, Bold) generated from the variable font, with autohinting for Windows.

SF Pro and Apple's icons are **not** distributed with Tesela. If SF Pro Display is installed on the PC,
Tesela can use it and builds local copies with Apple's letter spacing in `%LOCALAPPDATA%\Tesela`; they
never leave that PC.

## Hardware sensors (`sensors/`)

| Component | License | Source |
|---|---|---|
| LibreHardwareMonitorLib | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | Mozilla Public License 2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit | Mozilla Public License 2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core | Mozilla Public License 2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp | Apache License 2.0 | https://www.zer7.com/software/hidsharp |
| System.Memory, System.Buffers, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe | MIT | https://github.com/dotnet/runtime |

MPL 2.0: https://mozilla.org/MPL/2.0/ · Apache 2.0: https://www.apache.org/licenses/LICENSE-2.0

## Settings window (`settings/`)

| Component | License | Source |
|---|---|---|
| Windows App SDK, WinUI 3 | MIT | https://github.com/microsoft/WindowsAppSDK |
| .NET runtime | MIT | https://github.com/dotnet/runtime |
| Figtree (variable) | SIL Open Font License 1.1 | `settings/Fonts/OFL.txt` |

## Data and services

- **Open‑Meteo** — weather data under CC BY 4.0 (https://open-meteo.com).
- **AEMET OpenData** — official forecast for Spain, © AEMET (https://www.aemet.es). Each user needs their
  own free key.
- **iTunes Search API** — artwork lookup when the playing app doesn't provide good artwork.
