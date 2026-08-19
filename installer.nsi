Unicode true

Name "Access Overlay"
OutFile "publish\AccessOverlaySetup.exe"
InstallDir "$LOCALAPPDATA\Programs\Access Overlay"
RequestExecutionLevel user

!include "MUI2.nsh"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"
  File "publish\AccessOverlay\AccessOverlay.App.exe"

  CreateDirectory "$SMPROGRAMS\Access Overlay"
  CreateShortcut "$SMPROGRAMS\Access Overlay\Access Overlay.lnk" "$INSTDIR\AccessOverlay.App.exe"
  CreateShortcut "$DESKTOP\Access Overlay.lnk" "$INSTDIR\AccessOverlay.App.exe"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay" "DisplayName" "Access Overlay"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay" "DisplayIcon" "$INSTDIR\AccessOverlay.App.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Access Overlay\Access Overlay.lnk"
  RMDir "$SMPROGRAMS\Access Overlay"
  Delete "$DESKTOP\Access Overlay.lnk"
  Delete "$INSTDIR\AccessOverlay.App.exe"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AccessOverlay"
SectionEnd