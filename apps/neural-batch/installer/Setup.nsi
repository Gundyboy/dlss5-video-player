Unicode True
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!define PRODUCT "DLSS Neural Batch"
!define VERSION "0.2.0"
!define STAGE "..\dist\portable"
!define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DLSSNeuralBatch"
Name "${PRODUCT}"
OutFile "..\dist\DLSS-Neural-Batch-Setup-win64.exe"
InstallDir "$LOCALAPPDATA\DLSSNeuralBatch"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
VIProductVersion "0.2.0.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${PRODUCT} setup"
VIAddVersionKey "CompanyName" "Reality3D"
VIAddVersionKey "LegalCopyright" "Copyright 2026 Reality3D"
Var PlayerExe
Var PlayerInput
Var BrowseButton
Var Report
!insertmacro MUI_PAGE_WELCOME
Page custom SelectPlayer SelectPlayerLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\App\DLSSNeuralBatch.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
 StrCpy $INSTDIR "$LOCALAPPDATA\DLSSNeuralBatch"
 StrCpy $PlayerExe "$DESKTOP\DLSSVideoPlayer-v0.26.0-win64\DLSSVideoPlayer.exe"
 ReadRegStr $0 HKCU "Software\DLSSNeuralBatch" "PlayerExe"
 StrCmp $0 "" +2
  StrCpy $PlayerExe $0
 InitPluginsDir
 SetOutPath "$PLUGINSDIR\App"
 File "${STAGE}\DLSSNeuralBatch.exe"
 SetOutPath "$PLUGINSDIR\App\RuntimeFiles"
 File /r "${STAGE}\RuntimeFiles\*.*"
 StrCpy $Report "$PLUGINSDIR\report.txt"
FunctionEnd

Function SelectPlayer
 !insertmacro MUI_HEADER_TEXT "Locate the neural renderer" "Choose the existing unpacked video player's executable."
 nsDialogs::Create 1018
 Pop $0
 ${NSD_CreateLabel} 0 0 100% 27u "Select DLSSVideoPlayer.exe from the unpacked player folder. Setup verifies and copies the neural runtime and media tools for this app."
 Pop $0
 ${NSD_CreateText} 0 35u 79% 13u "$PlayerExe"
 Pop $PlayerInput
 ${NSD_CreateBrowseButton} 81% 35u 19% 13u "Browse..."
 Pop $BrowseButton
 ${NSD_OnClick} $BrowseButton BrowsePlayer
 ${NSD_CreateLabel} 0 59u 100% 30u "Photoshop and a separate .NET installation are not required. Close DLSS Neural Batch before installing or upgrading."
 Pop $0
 nsDialogs::Show
FunctionEnd
Function BrowsePlayer
 Pop $0
 ${NSD_GetText} $PlayerInput $PlayerExe
 ${GetParent} "$PlayerExe" $2
 IfFileExists "$2\*.*" +2 0
  StrCpy $2 "$DESKTOP"
 nsDialogs::SelectFileDialog open "$2" "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
 Pop $1
 StrCmp $1 "" done
 StrCpy $PlayerExe $1
 ${NSD_SetText} $PlayerInput "$PlayerExe"
done:
FunctionEnd
Function ReadReport
 StrCpy $1 "The renderer could not be prepared. Choose the verified player folder."
 IfFileExists "$Report" 0 done
 FileOpen $2 "$Report" r
 FileRead $2 $1
 FileClose $2
done:
FunctionEnd
Function SelectPlayerLeave
 ${NSD_GetText} $PlayerInput $PlayerExe
 Delete "$Report"
 nsExec::ExecToStack '"$PLUGINSDIR\App\DLSSNeuralBatch.exe" --validate-runtime "$PlayerExe" --report "$Report"'
 Pop $0
 Pop $1
 StrCmp $0 "0" valid
 Call ReadReport
 MessageBox MB_ICONSTOP|MB_OK "$1"
 Abort
valid:
FunctionEnd

Section "Install"
 DetailPrint "Preparing the verified local renderer..."
 Delete "$Report"
 nsExec::ExecToStack '"$PLUGINSDIR\App\DLSSNeuralBatch.exe" --configure-runtime "$PlayerExe" --report "$Report"'
 Pop $0
 Pop $1
 StrCmp $0 "0" prepared
 Call ReadReport
 MessageBox MB_ICONSTOP|MB_OK "Installation failed:$\r$\n$1"
 Abort
prepared:
 SetOutPath "$INSTDIR\App\RuntimeFiles"
 File /r "${STAGE}\RuntimeFiles\*.*"
 SetOutPath "$INSTDIR\App"
 File "${STAGE}\DLSSNeuralBatch.exe"
 File "${STAGE}\README.md"
 File "${STAGE}\LICENSE"
 File "${STAGE}\THIRD_PARTY.md"
 File "${STAGE}\DOTNET-LICENSE.txt"
 File "${STAGE}\DOTNET-ThirdPartyNotices.txt"
 File "${STAGE}\BUNDLED-RUNTIMES.md"
 CreateShortcut "$SMPROGRAMS\DLSS Neural Batch.lnk" "$INSTDIR\App\DLSSNeuralBatch.exe"
 CreateShortcut "$DESKTOP\DLSS Neural Batch.lnk" "$INSTDIR\App\DLSSNeuralBatch.exe"
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 WriteRegStr HKCU "Software\DLSSNeuralBatch" "PlayerExe" "$PlayerExe"
 WriteRegStr HKCU "${KEY}" "DisplayName" "${PRODUCT}"
 WriteRegStr HKCU "${KEY}" "DisplayVersion" "${VERSION}"
 WriteRegStr HKCU "${KEY}" "Publisher" "Reality3D"
 WriteRegStr HKCU "${KEY}" "DisplayIcon" "$INSTDIR\App\DLSSNeuralBatch.exe"
 WriteRegStr HKCU "${KEY}" "InstallLocation" "$INSTDIR"
 WriteRegStr HKCU "${KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
 WriteRegDWORD HKCU "${KEY}" "NoModify" 1
 WriteRegDWORD HKCU "${KEY}" "NoRepair" 1
SectionEnd

Function un.onInit
 ; All recursive removals are restricted to this product's exact per-user root.
 StrCmp $INSTDIR "$LOCALAPPDATA\DLSSNeuralBatch" valid
 MessageBox MB_ICONSTOP|MB_OK "The application folder could not be verified. Uninstall cancelled."
 Abort
valid:
 InitPluginsDir
 StrCpy $Report "$PLUGINSDIR\idle.txt"
 nsExec::ExecToStack '"$INSTDIR\App\DLSSNeuralBatch.exe" --check-idle --report "$Report"'
 Pop $0
 Pop $1
 StrCmp $0 "0" idle
 MessageBox MB_ICONSTOP|MB_OK "Close DLSS Neural Batch before uninstalling."
 Abort
idle:
FunctionEnd
Section "Uninstall"
 RMDir /r "$INSTDIR\App"
 RMDir /r "$INSTDIR\Renderer"
 RMDir /r "$INSTDIR\Work"
 Delete "$DESKTOP\DLSS Neural Batch.lnk"
 Delete "$SMPROGRAMS\DLSS Neural Batch.lnk"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
 DeleteRegKey HKCU "${KEY}"
 DeleteRegKey HKCU "Software\DLSSNeuralBatch"
SectionEnd
