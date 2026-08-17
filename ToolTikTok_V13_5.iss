#define MyAppName "Tool TikTok"
#ifndef MyAppVersion
  #error "MyAppVersion chua duoc truyen vao. Hay chay TAO_SETUP_V13_5_AUTO_FIND_INNO.bat."
#endif
#define MyAppPublisher "Tool TikTok"
#define MyAppExeName "ToolTikTokManagerV13.exe"

[Setup]
AppId={{7E1A7BC3-1DF6-4E99-A93F-7F1775CBA135}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} V{#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=ToolTikTokManagerV13\ToolTikTok.ico

; Cài theo user để Tool có quyền ghi profile/cấu hình mà không cần UAC.
DefaultDirName={localappdata}\ToolTikTok\V13.5
DefaultGroupName=Tool TikTok V13.5
UsePreviousAppDir=yes
PrivilegesRequired=lowest

OutputDir=SETUP_OUTPUT
OutputBaseFilename=ToolTikTok_V13.5_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
Uninstallable=yes
UninstallDisplayName=Tool TikTok V13.5
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài Desktop"; GroupDescription: "Tùy chọn:"; Flags: checkedonce

[Files]
; Lấy toàn bộ bản publish V13.5 nhưng KHÔNG đụng dữ liệu runtime/profile hiện có.
Source: "publish_v13_5_vm\*"; DestDir: "{app}"; \
  Excludes: "\profiles\*,\TikTokProfiles\*,\logs\*,\manager_default_config\*,\default_config_backups\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tool TikTok V13.5"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Tool TikTok V13.5"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Mở Tool TikTok V13.5"; WorkingDir: "{app}"; \
  Flags: nowait postinstall skipifsilent