; Inno Setup script for TapQueue_client_<version>.exe, the Windows client installer.
; Built on Windows by CI (see .github/workflows): iscc /DAppVersion=0.3.0 /DExeSource=path\to\TapQueueClient.exe tapqueue-client.iss
;
; Installs for the whole PC:
;   C:\Program Files\TapQueue\TapQueueClient.exe
;   C:\ProgramData\TapQueue\client.toml      (server address; only admins can change it)
;   the "TapQueue" service (TapQueueClient.exe --service, LocalSystem): adds the printers, installs updates
;   the tray app, started for every user at sign-in
;
; Silent install for many PCs:
;   TapQueue_client_0.3.0.exe /VERYSILENT /SERVER=http://tapqueue-server:8631
; Without /SERVER, a first install uses the one TapQueue server it finds on the network (and fails if
; it finds none, or more than one). The wizard lists the servers it finds (TapQueueClient.exe --discover).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef ExeSource
  #define ExeSource "..\..\out\client\TapQueueClient.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{86E6FC72-160C-468B-B137-4E390BC4DFC2}
AppName=TapQueue
AppVersion={#AppVersion}
AppVerName=TapQueue {#AppVersion}
AppPublisher=TapQueue contributors
AppPublisherURL=https://github.com/jthy10/TapQueue
AppSupportURL=https://github.com/jthy10/TapQueue/issues
DefaultDirName={autopf}\TapQueue
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TapQueue_client_{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\TapQueueClient.exe
UninstallDisplayName=TapQueue
; Tray apps of every signed-in user are closed for an upgrade, and start again at next sign-in
; (or when setup finishes, for the user running it).
CloseApplications=force
RestartApplications=no

[Files]
Source: "{#ExeSource}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Left over from an update the service installed.
Type: files; Name: "{app}\TapQueueClient.exe.old"
Type: files; Name: "{app}\TapQueueClient.exe.new"

[Registry]
; Start the tray app for every user at sign-in.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TapQueue"; \
    ValueData: """{app}\TapQueueClient.exe"""; Flags: uninsdeletevalue

[Run]
; The service runs as SYSTEM and installs whatever client build the server in client.toml offers,
; so only SYSTEM and Administrators may change that folder. Users can read it.
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\TapQueue"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX"; \
    Flags: runhidden; StatusMsg: "Securing settings..."
; "create" fails harmlessly on upgrade, when the service already exists; "config" then updates it.
Filename: "{sys}\sc.exe"; Parameters: "create TapQueue binPath= ""\""{app}\TapQueueClient.exe\"" --service"" start= auto DisplayName= ""TapQueue"""; \
    Flags: runhidden; StatusMsg: "Installing the TapQueue service..."
Filename: "{sys}\sc.exe"; Parameters: "config TapQueue binPath= ""\""{app}\TapQueueClient.exe\"" --service"" start= auto"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description TapQueue ""Adds the TapQueue printers and installs TapQueue updates from the server."""; Flags: runhidden
; Restart after an update (the service stops itself with exit code 1) or a crash.
Filename: "{sys}\sc.exe"; Parameters: "failure TapQueue reset= 86400 actions= restart/2000/restart/5000/restart/30000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failureflag TapQueue 1"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start TapQueue"; Flags: runhidden; StatusMsg: "Starting the TapQueue service..."
Filename: "{app}\TapQueueClient.exe"; Description: "Start TapQueue now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\TapQueueClient.exe"; Parameters: "--remove-printers"; Flags: runhidden; RunOnceId: "RemovePrinters"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Stop-Service TapQueue -Force -ErrorAction SilentlyContinue"""; \
    Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete TapQueue"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM TapQueueClient.exe"; Flags: runhidden; RunOnceId: "StopTrayApps"

[UninstallDelete]
Type: files; Name: "{app}\TapQueueClient.exe.old"
Type: files; Name: "{app}\TapQueueClient.exe.new"
; client.toml is kept, so reinstalling keeps the server address.

[Code]
var
  ServerPage: TInputQueryWizardPage;
  FoundLabel: TNewStaticText;
  FoundList: TNewListBox;
  SearchButton: TNewButton;
  FoundUrls: TArrayOfString;
  Searched: Boolean;
  { What a silent install found, when no /SERVER was given. }
  DiscoveredServerUrl: String;

function ConfigPath: String;
begin
  Result := ExpandConstant('{commonappdata}\TapQueue\client.toml');
end;

{ The server_url from an existing client.toml, or ''. }
function ExistingServerUrl: String;
var
  Lines: TArrayOfString;
  I, Q1, Q2: Integer;
  Line: String;
begin
  Result := '';
  if not LoadStringsFromFile(ConfigPath, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    if Pos('server_url', Line) = 1 then
    begin
      Q1 := Pos('"', Line);
      Line := Copy(Line, Q1 + 1, Length(Line));
      Q2 := Pos('"', Line);
      if (Q1 > 0) and (Q2 > 0) then
        Result := Copy(Line, 1, Q2 - 1);
      Exit;
    end;
  end;
end;

{ Runs TapQueueClient.exe --discover, which writes one "url|name|version" line per server it finds.
  Fills Urls and, for the list, Labels. Returns how many were found. }
function DiscoverServers(var Urls, Labels: TArrayOfString): Integer;
var
  Exe, OutFile, Line, Name, Version: String;
  Lines: TArrayOfString;
  I, N, P, ResultCode: Integer;
begin
  SetArrayLength(Urls, 0);
  SetArrayLength(Labels, 0);
  Result := 0;
  Exe := ExpandConstant('{tmp}\TapQueueClient.exe');
  OutFile := ExpandConstant('{tmp}\servers.txt');
  try
    if not FileExists(Exe) then
      ExtractTemporaryFile('TapQueueClient.exe');
  except
    Log('Could not unpack TapQueueClient.exe to look for servers: ' + GetExceptionMessage);
    Exit;
  end;
  DeleteFile(OutFile);
  if not Exec(Exe, '--discover "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Log('Looking for TapQueue servers failed (exit code ' + IntToStr(ResultCode) + ').');
    Exit;
  end;
  if not LoadStringsFromFile(OutFile, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    P := Pos('|', Line);
    if P = 0 then
      Continue;
    N := GetArrayLength(Urls);
    SetArrayLength(Urls, N + 1);
    SetArrayLength(Labels, N + 1);
    Urls[N] := Copy(Line, 1, P - 1);
    Line := Copy(Line, P + 1, Length(Line));
    P := Pos('|', Line);
    Name := Copy(Line, 1, P - 1);
    Version := Copy(Line, P + 1, Length(Line));
    Labels[N] := Urls[N] + '    (' + Name + ', TapQueue ' + Version + ')';
    Log('Found TapQueue server ' + Labels[N]);
  end;
  Result := GetArrayLength(Urls);
end;

function AddressIsBlank: Boolean;
begin
  Result := (Trim(ServerPage.Values[0]) = '') or (Lowercase(Trim(ServerPage.Values[0])) = 'http://');
end;

procedure SearchForServers;
var
  Labels: TArrayOfString;
  I, Count: Integer;
begin
  FoundList.Items.Clear;
  FoundLabel.Caption := 'Searching this network for TapQueue servers...';
  SearchButton.Enabled := False;
  WizardForm.NextButton.Enabled := False;
  Count := DiscoverServers(FoundUrls, Labels);
  for I := 0 to Count - 1 do
    FoundList.Items.Add(Labels[I]);
  if Count = 0 then
    FoundLabel.Caption := 'No TapQueue servers found on this network. Type the server''s address above.'
  else
    FoundLabel.Caption := 'TapQueue servers on this network (click one to use it):';
  { Fill in the address when it's blank, or when it's the only server there is. }
  if (Count > 0) and (AddressIsBlank or (Count = 1)) then
  begin
    FoundList.ItemIndex := 0;
    ServerPage.Values[0] := FoundUrls[0];
  end;
  SearchButton.Enabled := True;
  WizardForm.NextButton.Enabled := True;
end;

procedure FoundListClick(Sender: TObject);
begin
  if FoundList.ItemIndex >= 0 then
    ServerPage.Values[0] := FoundUrls[FoundList.ItemIndex];
end;

procedure SearchButtonClick(Sender: TObject);
begin
  SearchForServers;
end;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpWelcome,
    'TapQueue server', 'Which TapQueue server should this PC use?',
    'Pick a server found on this network, or enter its address including the port, for example http://tapqueue-server:8631');
  ServerPage.Add('Server address:', False);
  ServerPage.Values[0] := ExpandConstant('{param:SERVER|}');
  if ServerPage.Values[0] = '' then
    ServerPage.Values[0] := ExistingServerUrl;
  if ServerPage.Values[0] = '' then
    ServerPage.Values[0] := 'http://';

  FoundLabel := TNewStaticText.Create(ServerPage);
  FoundLabel.Parent := ServerPage.Surface;
  FoundLabel.Top := ServerPage.Edits[0].Top + ServerPage.Edits[0].Height + ScaleY(16);
  FoundLabel.Width := ServerPage.SurfaceWidth;
  FoundLabel.AutoSize := False;
  FoundLabel.Height := ScaleY(16);

  FoundList := TNewListBox.Create(ServerPage);
  FoundList.Parent := ServerPage.Surface;
  FoundList.Top := FoundLabel.Top + FoundLabel.Height + ScaleY(4);
  FoundList.Width := ServerPage.SurfaceWidth;
  FoundList.Height := ScaleY(80);
  FoundList.OnClick := @FoundListClick;

  SearchButton := TNewButton.Create(ServerPage);
  SearchButton.Parent := ServerPage.Surface;
  SearchButton.Caption := 'Search again';
  SearchButton.Top := FoundList.Top + FoundList.Height + ScaleY(8);
  SearchButton.Width := ScaleX(100);
  SearchButton.Height := WizardForm.NextButton.Height;
  SearchButton.OnClick := @SearchButtonClick;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Search the first time the page is shown, not before: it takes a few seconds. }
  if (CurPageID = ServerPage.ID) and not Searched then
  begin
    Searched := True;
    SearchForServers;
  end;
end;

function IsValidServerUrl(Url: String): Boolean;
begin
  Url := Lowercase(Trim(Url));
  Result := ((Pos('http://', Url) = 1) and (Length(Url) > 7)) or ((Pos('https://', Url) = 1) and (Length(Url) > 8));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ServerPage.ID) and not IsValidServerUrl(ServerPage.Values[0]) then
  begin
    MsgBox('Enter an address starting with http:// or https://, for example http://tapqueue-server:8631', mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeSetup: Boolean;
var
  Urls, Labels: TArrayOfString;
begin
  Result := True;
  if WizardSilent and (ExpandConstant('{param:SERVER|}') = '') and (ExistingServerUrl = '') then
  begin
    { First install with no server given: use the TapQueue server on this network, if there's exactly one. }
    case DiscoverServers(Urls, Labels) of
      0: Log('No TapQueue server found on this network. Run setup with /SERVER=http://your-server:8631');
      1: DiscoveredServerUrl := Urls[0];
    else
      Log('More than one TapQueue server on this network. Pick one with /SERVER=http://your-server:8631');
    end;
    Result := DiscoveredServerUrl <> '';
  end;
end;

{ Stop the service before its exe is replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Stop-Service TapQueue -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function Line(S: String): String;
begin
  Result := S + #13#10;
end;

{ Write client.toml, or change server_url in an existing one and keep everything else. }
procedure WriteConfig(Url: String);
var
  Lines: TArrayOfString;
  I: Integer;
  Found: Boolean;
begin
  ForceDirectories(ExtractFileDir(ConfigPath));
  Url := Trim(Url);
  if LoadStringsFromFile(ConfigPath, Lines) then
  begin
    Found := False;
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Pos('server_url', Trim(Lines[I])) = 1 then
      begin
        Lines[I] := 'server_url = "' + Url + '"';
        Found := True;
      end;
    if not Found then
    begin
      SetArrayLength(Lines, GetArrayLength(Lines) + 1);
      Lines[GetArrayLength(Lines) - 1] := 'server_url = "' + Url + '"';
    end;
    SaveStringsToUTF8FileWithoutBOM(ConfigPath, Lines, False);
  end
  else
  begin
    SaveStringToFile(ConfigPath,
      Line('# TapQueue client settings for this PC, written by setup.') +
      Line('# See https://github.com/jthy10/TapQueue/blob/main/docs/windows-client.md') +
      Line('') +
      Line('# The TapQueue server.') +
      Line('server_url = "' + Url + '"') +
      Line('') +
      Line('# TapQueue username. Empty = the Windows username of whoever is signed in.') +
      Line('username = ""') +
      Line('') +
      Line('# Client token from `tapqueue-admin users add`. Not needed when the server runs with auth.mode = "dev".') +
      Line('token = ""') +
      Line('') +
      Line('# Add the server''s print queues to this PC as printers.') +
      Line('install_printers = true'), False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Before [Run] starts the service, which needs the config. }
  if CurStep = ssInstall then
  begin
    if WizardSilent then
    begin
      if ExpandConstant('{param:SERVER|}') <> '' then
        WriteConfig(ExpandConstant('{param:SERVER|}'))
      else if DiscoveredServerUrl <> '' then
        WriteConfig(DiscoveredServerUrl);
    end
    else
      WriteConfig(ServerPage.Values[0]);
  end;
end;
