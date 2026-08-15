; dsh-tray Windows installer (per-user, no admin/UAC required)
Unicode true
Name "DeepSeek Harness Tray"
OutFile "dsh-tray-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\DeepSeek Harness Tray"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!define APP "DeepSeek Harness Tray"
!define UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\dsh-tray"

Section
  SetOutPath "$INSTDIR"
  File "dsh-tray.exe"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\dsh-tray.exe"
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\dsh-tray.exe"
  WriteRegStr HKCU "${UNINST}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST}" "DisplayVersion" "0.1.0"
  WriteRegStr HKCU "${UNINST}" "Publisher" "dsh-tray"
  WriteRegStr HKCU "${UNINST}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINST}" "DisplayIcon" "$INSTDIR\dsh-tray.exe"
  WriteRegDWORD HKCU "${UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$INSTDIR\dsh-tray.exe"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  DeleteRegKey HKCU "${UNINST}"
SectionEnd
