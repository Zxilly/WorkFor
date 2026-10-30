Unicode true
RequestExecutionLevel user
SetCompressor /SOLID zlib
SetOverwrite on
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef PAYLOAD
  !error "PAYLOAD must identify a verified portable directory."
!endif
!ifndef OUTFILE
  !error "OUTFILE is required."
!endif
!ifndef VERSION
  !define VERSION "0.1.0"
!endif

Name "WorkFor"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\WorkFor"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "WorkFor"
VIAddVersionKey /LANG=2052 "FileDescription" "WorkFor per-user installer (unsigned)"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "WorkFor Contributors"
ShowInstDetails show
ShowUninstDetails show

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro CheckFixedRoot PREFIX
Function ${PREFIX}CheckFixedRoot
  SetShellVarContext current
  GetFullPathName $0 "$LOCALAPPDATA\Programs\WorkFor"
  GetFullPathName $1 "$INSTDIR"
  StrCmp $0 $1 +3
    MessageBox MB_OK|MB_ICONSTOP "安装路径必须是当前用户的 Programs\WorkFor，未更改文件。"
    Abort
  ; Never follow Programs / WorkFor directory junctions while changing installation.
  System::Call 'kernel32::GetFileAttributesW(w "$LOCALAPPDATA\Programs") i .r2'
  IntCmp $2 -1 skipPrograms
  IntOp $2 $2 & 0x400
  IntCmp $2 0 skipPrograms
    MessageBox MB_OK|MB_ICONSTOP "Programs 目录为重解析点，无法安全更改 WorkFor。"
    Abort
  skipPrograms:
  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i .r2'
  IntCmp $2 -1 doneRootCheck
  IntOp $2 $2 & 0x400
  IntCmp $2 0 doneRootCheck
    MessageBox MB_OK|MB_ICONSTOP "WorkFor 安装目录为重解析点，未更改文件。"
    Abort
  doneRootCheck:
FunctionEnd
!macroend
!insertmacro CheckFixedRoot ""
!insertmacro CheckFixedRoot "un."

Function .onInit
  SetShellVarContext current
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK|MB_ICONSTOP "本版本仅支持 Windows 11 x64；不支持 ARM64 或 32 位系统。"
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegStr $0 HKLM "Software\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  IntCmp $0 26100 supportedBuild unsupportedBuild supportedBuild
  unsupportedBuild:
    MessageBox MB_OK|MB_ICONSTOP "需要 Windows 11 24H2 或更新版本。"
    Abort
  supportedBuild:
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\WorkFor"
  Call CheckFixedRoot
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\WorkFor"
  Call un.CheckFixedRoot
FunctionEnd

Section "WorkFor" InstallApplication
  Call CheckFixedRoot
  IfFileExists "$INSTDIR\WorkFor.exe" existingApplication checkUnexpectedDirectory
  checkUnexpectedDirectory:
    IfFileExists "$INSTDIR\*.*" unexpectedDirectory installPayload
  unexpectedDirectory:
    MessageBox MB_OK|MB_ICONSTOP "目标目录已有文件但不是可验证的 WorkFor 安装。请先保留并移走该目录，再重试。"
    Abort
  existingApplication:
    IfFileExists "$INSTDIR\.workfor-install" +3
      MessageBox MB_OK|MB_ICONSTOP "安装所有权标记不存在，无法安全升级这个目录。"
      Abort
    ClearErrors
    ExecWait '"$INSTDIR\WorkFor.exe" --prepare-uninstall' $0
    IfErrors prepareFailed
    StrCmp $0 "0" preparePassed prepareFailed
    prepareFailed:
      MessageBox MB_OK|MB_ICONSTOP "WorkFor 未能安全退出，或任务栏 DLL 仍驻留。请注销并重新登录后重试。安装器不会结束 Explorer。"
      Abort
    preparePassed:
    ClearErrors
    ExecWait '"$INSTDIR\WorkFor.exe" --uninstall-files' $0
    IfErrors oldRemovalFailed
    StrCmp $0 "0" oldFilesRemoved oldRemovalFailed
    oldRemovalFailed:
      MessageBox MB_OK|MB_ICONSTOP "旧安装文件未能安全移除，已停止升级。请退出登录后重试。"
      Abort
    oldFilesRemoved:
    Delete "$INSTDIR\WorkFor.exe"
    Delete "$INSTDIR\release.json"
  installPayload:
    SetOutPath "$INSTDIR"
    File /r "${PAYLOAD}\*.*"
    ClearErrors
    ExecWait '"$INSTDIR\WorkFor.exe" --verify' $0
    IfErrors payloadVerificationFailed
    StrCmp $0 "0" payloadVerified payloadVerificationFailed
    payloadVerificationFailed:
      MessageBox MB_OK|MB_ICONSTOP "提取后的文件校验失败，安装未完成。请保留数据并重新运行安装程序。"
      Abort
    payloadVerified:
    FileOpen $0 "$INSTDIR\.workfor-install" w
    FileWrite $0 "WorkFor.Installation.v1:78b231eb-09b3-4c9e-a8af-62f4b9abeb84$\r$\n"
    FileClose $0
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\WorkFor"
    CreateShortcut "$SMPROGRAMS\WorkFor\WorkFor.lnk" "$INSTDIR\WorkFor.exe"
    CreateShortcut "$SMPROGRAMS\WorkFor\WorkFor Settings.lnk" "$INSTDIR\WorkFor.exe" "--settings"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "DisplayName" "WorkFor"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "DisplayVersion" "${VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "Publisher" "WorkFor Contributors"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Call un.CheckFixedRoot
  IfFileExists "$INSTDIR\.workfor-install" +3
    MessageBox MB_OK|MB_ICONSTOP "缺少 WorkFor 安装标记，拒绝删除文件。"
    Abort
  ClearErrors
  ExecWait '"$INSTDIR\WorkFor.exe" --prepare-uninstall' $0
  IfErrors uninstallPreparationFailed
  StrCmp $0 "0" uninstallPrepared uninstallPreparationFailed
  uninstallPreparationFailed:
    MessageBox MB_OK|MB_ICONSTOP "任务栏 DLL 可能仍驻留，或 WorkFor 尚未退出。请注销并重新登录后重试卸载。"
    Abort
  uninstallPrepared:
  ClearErrors
  ExecWait '"$INSTDIR\WorkFor.exe" --uninstall-files' $0
  IfErrors uninstallRemovalFailed
  StrCmp $0 "0" uninstallFilesRemoved uninstallRemovalFailed
  uninstallRemovalFailed:
    MessageBox MB_OK|MB_ICONSTOP "安装清单未能安全处理，未继续删除。请保留文件并重试。"
    Abort
  uninstallFilesRemoved:
  Delete "$INSTDIR\WorkFor.exe"
  Delete "$INSTDIR\release.json"
  Delete "$INSTDIR\.workfor-install"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\WorkFor\WorkFor.lnk"
  Delete "$SMPROGRAMS\WorkFor\WorkFor Settings.lnk"
  RMDir "$SMPROGRAMS\WorkFor"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "WorkFor"
  StrCmp $0 '"$INSTDIR\WorkFor.exe" "--background"' removeOwnedStartup 0
  StrCmp $0 '"$INSTDIR\WorkFor.exe" --background' removeOwnedStartup preserveOtherStartup
  removeOwnedStartup:
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "WorkFor"
  preserveOtherStartup:
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkFor"
  ; No recursive delete. Unexpected files stay, and %LOCALAPPDATA%\WorkFor wages are outside this root.
  RMDir "$INSTDIR"
SectionEnd
