; Single-file, per-user installer. The separately licensed neural runtime is
; read from the DLSS Video Player executable selected by the user.
Unicode True
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define PRODUCT "DLSS Neural Mix"
!define VERSION "0.2.5"
!define STAGE "..\dist\setup"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DLSSNeuralMix"

Name "${PRODUCT}"
OutFile "..\dist\DLSS-Neural-Mix-Setup-win64.exe"
InstallDir "$LOCALAPPDATA\DLSSNeuralMix"
RequestExecutionLevel user
ShowInstDetails show
ShowUninstDetails show
SetCompressor /SOLID lzma
VIProductVersion "0.2.5.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileDescription" "${PRODUCT} setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "Reality3D"
VIAddVersionKey "LegalCopyright" "Copyright 2026 Reality3D"

Var PlayerExe
Var PlayerInput
Var BrowseButton

!insertmacro MUI_PAGE_WELCOME
Page custom SelectPlayerPage SelectPlayerLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  StrCpy $PlayerExe "$DESKTOP\DLSSVideoPlayer-v0.26.0-win64\DLSSVideoPlayer.exe"
  ReadRegStr $0 HKCU "Software\DLSSNeuralMix" "PlayerExe"
  StrCmp $0 "" +2
    StrCpy $PlayerExe $0

  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Install.ps1 "${STAGE}\Install.ps1"
  File /oname=Upgrade.ps1 "${STAGE}\Upgrade.ps1"
  File /oname=DLSS-Neural-Mix.ccx "${STAGE}\DLSS-Neural-Mix.ccx"
  SetOutPath "$PLUGINSDIR\payload"
  File "${STAGE}\payload\DLSSPhotoshopNeural.exe"
  File "${STAGE}\payload\NeuralWorker.exe"
  File "${STAGE}\payload\ReShade.ini"
  File "${STAGE}\payload\ReShadePreset.ini"
  File "${STAGE}\payload\runtime-lock.json"
  File "${STAGE}\payload\LICENSE"
  File "${STAGE}\payload\THIRD_PARTY.md"
FunctionEnd

Function SelectPlayerPage
  !insertmacro MUI_HEADER_TEXT "Locate DLSS Video Player" "Choose the existing video player's executable."
  nsDialogs::Create 1018
  Pop $0
  StrCmp $0 error 0 +2
    Abort
  ${NSD_CreateLabel} 0 0 100% 24u "Select DLSSVideoPlayer.exe from an unpacked player folder with neural-runtime, ffmpeg.exe and ffprobe.exe."
  Pop $0
  ${NSD_CreateText} 0 32u 79% 13u "$PlayerExe"
  Pop $PlayerInput
  ${NSD_CreateBrowseButton} 81% 32u 19% 13u "Browse..."
  Pop $BrowseButton
  ${NSD_OnClick} $BrowseButton BrowseForPlayer
  ${NSD_CreateLabel} 0 55u 100% 31u "Save your work and close Photoshop before continuing. Setup then installs the neural runner and plugin for this Windows user."
  Pop $0
  nsDialogs::Show
FunctionEnd

Function BrowseForPlayer
  Pop $0
  ${NSD_GetText} $PlayerInput $PlayerExe
  ${GetParent} "$PlayerExe" $2
  IfFileExists "$2\*.*" +2 0
    StrCpy $2 "$DESKTOP"
  ; An initial directory leaves the filename blank while moving between folders.
  nsDialogs::SelectFileDialog open "$2" "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
  Pop $1
  StrCmp $1 "" done
  StrCpy $PlayerExe $1
  ${NSD_SetText} $PlayerInput "$PlayerExe"
done:
FunctionEnd

Function SelectPlayerLeave
  ${NSD_GetText} $PlayerInput $PlayerExe
  IfFileExists "$PlayerExe" found
    MessageBox MB_ICONSTOP|MB_OK "Select an existing DLSSVideoPlayer.exe."
    Abort
found:
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Install.ps1" -PlayerExePath "$PlayerExe" -ValidateOnly'
  Pop $0
  Pop $1
  StrCmp $0 "0" valid
    MessageBox MB_ICONSTOP|MB_OK "The selected player cannot be used:$\r$\n$1"
    Abort
valid:
FunctionEnd

Section "Install"
  DetailPrint "Installing DLSS Neural Mix from $PlayerExe"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Install.ps1" -PlayerExePath "$PlayerExe"'
  Pop $0
  Pop $1
  StrCmp $0 "0" installed
    MessageBox MB_ICONSTOP|MB_OK "Installation failed:$\r$\n$1"
    Abort
installed:
  DetailPrint "$1"
  SetOutPath "$INSTDIR"
  File "${STAGE}\Uninstall.ps1"
  File "${STAGE}\payload\runtime-lock.json"
  File "${STAGE}\README.md"
  File "${STAGE}\payload\LICENSE"
  File "${STAGE}\payload\THIRD_PARTY.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\DLSSNeuralMix" "PlayerExe" "$PlayerExe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Reality3D"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Renderer\DLSSPhotoshopNeural.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\Uninstall.ps1"'
  Pop $0
  Pop $1
  StrCmp $0 "0" removed
    MessageBox MB_ICONSTOP|MB_OK "Uninstall failed:$\r$\n$1"
    Abort
removed:
  Delete "$INSTDIR\Uninstall.ps1"
  Delete "$INSTDIR\runtime-lock.json"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD_PARTY.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR\Renderer\neural-runtime"
  RMDir "$INSTDIR\Renderer"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\DLSSNeuralMix"
SectionEnd
