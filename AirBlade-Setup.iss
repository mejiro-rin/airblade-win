[Setup]
AppName=AirBlade
AppVersion=0.5.0-beta
OutputBaseFilename=AirBlade-v0.5.0-beta-Setup
AppPublisher=AirBlade
ArchitecturesInstallIn64BitMode=x64
DefaultDirName={pf}\AirBlade
DefaultGroupName=AirBlade
OutputDir=D:\Develop\App\AirBlade-win\dist
Compression=lzma
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "D:\Develop\App\AirBlade-win\AirBlade\bin\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AirBlade"; Filename: "{app}\AirBlade.exe"
Name: "{commondesktop}\AirBlade"; Filename: "{app}\AirBlade.exe"

[Run]
Filename: "{app}\AirBlade.exe"; Description: "立即运行 AirBlade"; Flags: nowait postinstall skipifsilent