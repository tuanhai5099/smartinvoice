#define MyAppName "SmartInvoice"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Smart Tech"
#define MyAppExeName "SmartInvoice.Bootstrapper.exe"
#define MyAppSourceDir "..\\publish\\SmartInvoice"
#define MyAppIcon "..\\assets\\SmartInvoice.ico"

[Setup]
AppId={{4D0E5C7C-6FAD-4F72-9F8F-7B4E9F4B8B31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={pf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir=..\\artifacts
OutputBaseFilename=SmartInvoiceSetup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
SetupIconFile={#MyAppIcon}
LicenseFile="license_vi.txt"

[Languages]
Name: "vietnamese"; MessagesFile: "compiler:Languages\\Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo shortcut ngoài Desktop"; GroupDescription: "Tùy chọn:"; Flags: unchecked

[Files]
Source: "{#MyAppSourceDir}\\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Chạy {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  InvoiceDirPage: TInputDirWizardPage;

procedure InitializeWizard;
var
  defaultInvoiceDir: string;
begin
  // Trang cấu hình thư mục lưu hóa đơn (bước riêng sau khi chọn thư mục cài đặt)
  defaultInvoiceDir := ExpandConstant('{userdocs}\SmartInvoice\Invoices');

  InvoiceDirPage := CreateInputDirPage(
    wpSelectDir,
    'Thư mục lưu hóa đơn',
    'Chọn thư mục để SmartInvoice lưu các file hóa đơn (PDF/XML).',
    'Bạn có thể thay đổi sau trong phần cấu hình của ứng dụng.',
    False,
    '');

  InvoiceDirPage.Add('');
  InvoiceDirPage.Values[0] := defaultInvoiceDir;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  configPath: string;
begin
  // Khi bắt đầu cài đặt, ghi cấu hình thư mục hóa đơn vào file cấu hình đơn giản
  if CurStep = ssInstall then
  begin
    configPath := ExpandConstant('{app}\config\invoice-storage-path.txt');
    ForceDirectories(ExtractFileDir(configPath));
    SaveStringToFile(configPath, InvoiceDirPage.Values[0], False);
  end;
end;

#define MyAppName "SmartInvoice"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Smart Tech"
#define MyAppExeName "SmartInvoice.Bootstrapper.exe"
#define MyAppSourceDir "..\\publish\\SmartInvoice"
#define MyAppIcon "..\\assets\\SmartInvoice.ico"

[Setup]
AppId={{4D0E5C7C-6FAD-4F72-9F8F-7B4E9F4B8B31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={pf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir=..\\artifacts
OutputBaseFilename=SmartInvoiceSetup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
SetupIconFile={#MyAppIcon}

[Languages]
Name: "vietnamese"; MessagesFile: "compiler:Languages\\Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo shortcut ngoài Desktop"; GroupDescription: "Tùy chọn:"; Flags: unchecked

[Files]
Source: "{#MyAppSourceDir}\\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Chạy {#MyAppName}"; Flags: nowait postinstall skipifsilent

