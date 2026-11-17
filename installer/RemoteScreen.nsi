Unicode true
!include "MUI2.nsh"

Name "Remote Screen"
OutFile "RemoteScreen-${APP_VERSION}-win-x64-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Remote Screen"
RequestExecutionLevel user
VIProductVersion "${APP_VERSION}"

!define MUI_FINISHPAGE_RUN "$INSTDIR\app\RemoteScreen.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "Japanese"

Section "Remote Screen"
  SetOutPath "$INSTDIR\app"
  File /r "${SOURCE_DIR}\*"
  CreateShortcut "$SMPROGRAMS\Remote Screen.lnk" "$INSTDIR\app\RemoteScreen.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "DisplayName" "Remote Screen"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "DisplayIcon" "$INSTDIR\app\RemoteScreen.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Remote Screen.lnk"
  RMDir /r "$INSTDIR\app"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteScreen"
SectionEnd
