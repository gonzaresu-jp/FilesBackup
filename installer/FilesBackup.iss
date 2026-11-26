; FilesBackup のインストーラー（Inno Setup 6）
; ビルド: ISCC.exe /DAppVersion=2.3.0 /DSourceDir=<publish フォルダ> installer\FilesBackup.iss
; 出力:   installer\Output\FilesBackup-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
; アプリの識別子（変えると別のアプリとして扱われるので変更しない）
AppId={{775E0EFD-AC7F-4597-9A1E-864EF75D0A25}
AppName=FilesBackup
AppVersion={#AppVersion}
AppVerName=FilesBackup {#AppVersion}
AppPublisher=gonzaresu-jp
AppPublisherURL=https://github.com/gonzaresu-jp/FilesBackup
AppSupportURL=https://github.com/gonzaresu-jp/FilesBackup
AppUpdatesURL=https://github.com/gonzaresu-jp/FilesBackup/releases
VersionInfoVersion={#AppVersion}

; 既定は全ユーザー（Program Files、管理者権限が必要）。
; 最初の画面で「自分だけ」を選ぶと %LocalAppData%\Programs に入り、管理者権限なしで入れられる（以後のアップデートも UAC なし）
DefaultDirName={autopf}\FilesBackup
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DefaultGroupName=FilesBackup

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 起動中なら終了してもらってから入れ替える（動作確認用のビルドでは /DNoAppMutex で外せる）
#ifndef NoAppMutex
AppMutex=FilesBackupSingletonMutex
#endif
CloseApplications=yes
RestartApplications=no

OutputDir=Output
OutputBaseFilename=FilesBackup-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\FilesBackup.exe
UninstallDisplayName=FilesBackup
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FilesBackup"; Filename: "{app}\FilesBackup.exe"
Name: "{autodesktop}\FilesBackup"; Filename: "{app}\FilesBackup.exe"; Tasks: desktopicon

[Run]
; インストール後に起動する（管理者として入れた場合も、普段のユーザーの権限で起動する）
Filename: "{app}\FilesBackup.exe"; Description: "{cm:LaunchProgram,FilesBackup}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; 自動起動の登録とタスク スケジューラのタスクを消す（設定・ログ・履歴は残す）
Filename: "{app}\FilesBackup.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "FilesBackupCleanup"

[Code]
// .NET 8 デスクトップ ランタイムが入っているか
function IsDesktopRuntime8Installed(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntime8Installed() then
  begin
    if not WizardSilent() then
    begin
      if MsgBox('FilesBackup の実行には「.NET 8 デスクトップ ランタイム」が必要です。' + #13#10 +
                'ダウンロードページを開きますか？' + #13#10#13#10 +
                'ランタイムを入れてから、もう一度このインストーラーを実行してください。',
                mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;
