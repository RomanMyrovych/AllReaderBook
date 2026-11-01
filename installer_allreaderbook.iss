; ============================================================================
; Конфігураційний скрипт Inno Setup для автономного рідера AllReaderBook v0.9.02-beta
; Забезпечує:
;   - Автономне встановлення легкого рідера без каталогізатора та важкої БД
;   - Реєстрацію в меню «Відкрити за допомогою...» (Open With) як AllReaderBook
;   - Опціональне призначення програмою за замовчуванням для .pdf, .epub, .fb2, .mobi, .docx, .cbz, .djvu
;   - Створення окремого ярлика AllReaderBook на Робочому столі та в меню Пуск
; ============================================================================

#define MyAppDisplayName "AllReaderBook (Public Beta)"
#define MyAppShortcutName "AllReaderBook"
#define MyAppVersion "0.9.02-beta"
#define MyAppNumericVersion "0.9.2.0"
#define MyAppPublisher "AllReaderBook Open Source Project (Roman Myrovych)"
#define MyAppURL "https://github.com/RomanMyrovych/AllReaderBook"
#define MyAppExeName "AllReaderBook.exe"
#define SourcePublishDir "Publish\AllReaderBook_Portable_v0.9.02-beta"

[Setup]
AppId={{C94E812A-5B3D-4F18-9E27-6A1D3840F9B2}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppNumericVersion}
AppVerName={#MyAppDisplayName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
LicenseFile=LICENSE
InfoBeforeFile=THIRD_PARTY_LICENSES.txt
DefaultDirName={autopf}\AllReaderBook
DisableDirPage=no
DefaultGroupName={#MyAppShortcutName}
DisableProgramGroupPage=no
AllowNoIcons=yes
ChangesAssociations=yes
SetupIconFile=AllReaderBook.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppDisplayName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
OutputDir=Publish\Installer
OutputBaseFilename=AllReaderBook_Setup_v0.9.02-beta_x64

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_pdf"; Description: "Асоціювати файли .pdf з AllReaderBook"; GroupDescription: "Асоціація форматів книг і документів:"
Name: "assoc_epub"; Description: "Асоціювати файли .epub з AllReaderBook"; GroupDescription: "Асоціація форматів книг і документів:"
Name: "assoc_fb2"; Description: "Асоціювати файли .fb2 з AllReaderBook"; GroupDescription: "Асоціація форматів книг і документів:"
Name: "assoc_mobi"; Description: "Асоціювати файли .mobi / .azw / .azw3 з AllReaderBook"; GroupDescription: "Асоціація форматів книг і документів:"
Name: "assoc_cbz"; Description: "Асоціювати файли .cbz та .djvu з AllReaderBook"; GroupDescription: "Асоціація форматів книг і документів:"

[Files]
Source: "{#SourcePublishDir}\*"; DestDir: "{app}"; Excludes: "allreaderbook_settings.json,reader_settings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePublishDir}\allreaderbook_settings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Ліцензія (MIT)"; Filename: "{app}\LICENSE"
Name: "{group}\Сторонні компоненти (Third-Party Notices)"; Filename: "{app}\THIRD_PARTY_LICENSES.txt"
Name: "{group}\Політика приватності (PRIVACY)"; Filename: "{app}\PRIVACY.md"
Name: "{group}\Видалити {#MyAppShortcutName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register AllReaderBook.exe in Windows "Open With..." as AllReaderBook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppShortcutName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".epub"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".fb2"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mobi"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".azw"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".azw3"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".docx"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".cbz"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".djvu"; ValueData: ""

; ProgID definition for AllReaderBook.Document
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document"; ValueType: string; ValueName: ""; ValueData: "Документ або книга AllReaderBook"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; Register in OpenWithProgids so Windows always shows AllReaderBook in "Open With..."
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.fb2\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mobi\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.azw\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.azw3\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.docx\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.cbz\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.djvu\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue

; Register Windows Default Apps Capabilities
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppShortcutName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Ultra-fast lightweight document and book reader (PDF, EPUB, FB2, MOBI, DOCX, CBZ, DJVU)"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".epub"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".fb2"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mobi"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".azw"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".azw3"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".docx"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".cbz"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\AllReaderBook\Capabilities\FileAssociations"; ValueType: string; ValueName: ".djvu"; ValueData: "AllReaderBook.Document"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "AllReaderBook"; ValueData: "Software\AllReaderBook\Capabilities"; Flags: uninsdeletevalue

; Optional default handler assignment when user selects association tasks
Root: HKCU; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_pdf
Root: HKCU; Subkey: "Software\Classes\.epub"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_epub
Root: HKCU; Subkey: "Software\Classes\.fb2"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_fb2
Root: HKCU; Subkey: "Software\Classes\.mobi"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_mobi
Root: HKCU; Subkey: "Software\Classes\.azw"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_mobi
Root: HKCU; Subkey: "Software\Classes\.azw3"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_mobi
Root: HKCU; Subkey: "Software\Classes\.cbz"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_cbz
Root: HKCU; Subkey: "Software\Classes\.djvu"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_cbz

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppShortcutName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
