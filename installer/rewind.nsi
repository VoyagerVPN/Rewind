Unicode true
RequestExecutionLevel user
ManifestDPIAware true
CRCCheck force
SetCompressor zlib

!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "TextFunc.nsh"
!include "${CLEANUP}"

Name "${PRODUCT}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT}"
Icon "${ICON}"
UninstallIcon "${ICON}"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}"

LoadLanguageFile "${NSISDIR}\Contrib\Language files\Russian.nlf"
Caption "${PRODUCT} — установка"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=1049 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}.0"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик Rewind"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Rewind contributors"

Var AutoStart
Var DataDirectory
Var ReplaysDirectory

UninstPage uninstConfirm
UninstPage instfiles

Function .onInit
  StrCpy $DataDirectory "$LOCALAPPDATA\Rewind\Data"
  StrCpy $ReplaysDirectory "$VIDEOS\Rewind"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} "$0" "/DATA=" $1
  ${IfNot} ${Errors}
    StrCpy $DataDirectory "$1"
  ${EndIf}
  ClearErrors
  ${GetOptions} "$0" "/REPLAYS=" $1
  ${IfNot} ${Errors}
    StrCpy $ReplaysDirectory "$1"
  ${EndIf}
  ClearErrors
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Для Rewind нужна Windows x64." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  IfSilent backend
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\ui"
  ClearErrors
  !include "${BOOTSTRAP_LIST}"
  FileOpen $0 "$PLUGINSDIR\ui\setup-defaults.txt" w
  IfErrors uiFailed
  FileWriteUTF16LE /BOM $0 "$INSTDIR$\r$\n$DataDirectory$\r$\n$ReplaysDirectory$\r$\n"
  FileClose $0
  IfErrors uiFailed
  ExecWait '"$PLUGINSDIR\ui\Rewind.exe" --setup "$EXEPATH" "$PLUGINSDIR\ui\setup-defaults.txt"' $0
  IfErrors uiFailed
  ${If} $0 == 2
    Goto uiFailed
  ${EndIf}
  SetErrorLevel $0
  Quit
  uiFailed:
    MessageBox MB_ICONSTOP "Не удалось открыть окно установки Rewind."
    SetErrorLevel 2
    Quit

  backend:
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} "$0" "/OPTIONS=" $1
  IfErrors lock
  FileOpen $2 "$1" r
  IfErrors invalidOptions
  FileReadUTF16LE $2 $INSTDIR
  FileReadUTF16LE $2 $DataDirectory
  FileReadUTF16LE $2 $ReplaysDirectory
  FileReadUTF16LE $2 $AutoStart
  FileClose $2
  IfErrors invalidOptions
  ${TrimNewLines} "$INSTDIR" $INSTDIR
  ${TrimNewLines} "$DataDirectory" $DataDirectory
  ${TrimNewLines} "$ReplaysDirectory" $ReplaysDirectory
  ${TrimNewLines} "$AutoStart" $AutoStart
  Goto lock
  invalidOptions:
    SetErrorLevel 3
    Quit

  lock:
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${PRODUCT}.Setup") p .r0 ?e'
  Pop $1
  ${If} $0 == 0
  ${OrIf} $1 == 183
    MessageBox MB_ICONSTOP "Установщик уже запущен или не смог заблокировать каталог." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
FunctionEnd


Function NormalizeFolder
  Exch $0
  Push $1
  Push $2
  StrCpy $1 $0 2 1
  ${If} $1 == ":\"
    System::Call 'kernel32::GetFullPathNameW(w r0, i ${NSIS_MAX_STRLEN}, w .r2, p 0) i .r1'
    ${If} $1 > 0
    ${AndIf} $1 < ${NSIS_MAX_STRLEN}
      StrCpy $0 "$2"
    ${Else}
      StrCpy $0 ""
    ${EndIf}
  ${Else}
    StrCpy $0 ""
  ${EndIf}
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

Function ValidateFolders
  Push $INSTDIR
  Call NormalizeFolder
  Pop $INSTDIR
  Push $DataDirectory
  Call NormalizeFolder
  Pop $DataDirectory
  Push $ReplaysDirectory
  Call NormalizeFolder
  Pop $ReplaysDirectory

  ClearErrors
  ${If} $INSTDIR == ""
  ${OrIf} $DataDirectory == ""
  ${OrIf} $ReplaysDirectory == ""
  ${OrIf} $INSTDIR == $DataDirectory
  ${OrIf} $INSTDIR == $ReplaysDirectory
  ${OrIf} $DataDirectory == $ReplaysDirectory
    SetErrors
  ${EndIf}
FunctionEnd

Section "Rewind"
  Call ValidateFolders
  IfErrors invalidPaths
  ReadRegStr $0 HKCU "${REGKEY}" "InstallLocation"
  ${If} $0 != ""
    Goto occupied
  ${EndIf}
  ClearErrors
  FindFirst $0 $1 "$INSTDIR\*"
  ${IfNot} ${Errors}
    ${Do}
      ${If} $1 != "."
      ${AndIf} $1 != ".."
      ${AndIf} $1 != "Data"
        FindClose $0
        Goto occupied
      ${EndIf}
      FindNext $0 $1
    ${LoopUntil} ${Errors}
    FindClose $0
  ${EndIf}

  ClearErrors
  !include "${COPY_LIST}"
  SetOutPath "$INSTDIR"
  FileOpen $0 "$INSTDIR\install-paths.txt" w
  IfErrors failed
  FileWriteUTF16LE /BOM $0 "$DataDirectory$\r$\n$ReplaysDirectory$\r$\n"
  FileClose $0
  IfErrors failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors failed
  CreateDirectory "$SMPROGRAMS\${PRODUCT}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT}\Rewind.lnk" "$INSTDIR\Rewind.exe"
  ${If} $AutoStart == "1"
    CreateShortCut "$SMSTARTUP\${PRODUCT} (Setup).lnk" "$INSTDIR\Rewind.exe" "--background"
  ${EndIf}
  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\Rewind.exe"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  IfErrors failed
  Goto done
  invalidPaths:
    MessageBox MB_ICONSTOP "Укажите три разные папки на локальных дисках." /SD IDOK
    SetErrorLevel 3
    Quit
  occupied:
    MessageBox MB_ICONSTOP "Rewind уже установлен или папка занята. Перед повторной установкой удалите предыдущую копию через настройки Windows." /SD IDOK
    SetErrorLevel 1
    Quit
  failed:
    !insertmacro RemovePayload
    !insertmacro RemoveRegistration
    MessageBox MB_ICONSTOP "Не удалось установить Rewind. Проверьте доступ к папке и свободное место." /SD IDOK
    SetErrorLevel 2
    Quit
  done:
SectionEnd

Function un.onInit
  ReadRegStr $0 HKCU "${REGKEY}" "InstallLocation"
  ${If} $0 != $INSTDIR
    MessageBox MB_ICONSTOP "Папка не соответствует зарегистрированной установке Rewind." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
FunctionEnd

Section "Uninstall"
  ClearErrors
  !insertmacro RemovePayload
  IfErrors failed
  !insertmacro RemoveRegistration
  Goto done
  failed:
    MessageBox MB_ICONSTOP "Закройте Rewind и повторите удаление." /SD IDOK
    SetErrorLevel 2
  done:
SectionEnd
