Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
Name "盘镜 DiskLens ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\DiskLens"
BrandingText "盘镜 DiskLens · 本地优先"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "盘镜 DiskLens"
VIAddVersionKey /LANG=2052 "FileDescription" "盘镜 DiskLens 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!define PRODUCT_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskLens"
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "安装盘镜 DiskLens 到当前 Windows 用户的应用目录。无需管理员权限，也不需要另装 .NET。$\r$\n$\r$\n升级前请关闭正在运行的盘镜。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\DiskLens.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动盘镜 DiskLens"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; Check the actual installation, not a global window title: portable copies may be open.
; Opening the existing executable for write (without writing) fails while its image is mapped.
!macro RequireInactiveCopy
    ${If} ${FileExists} "$INSTDIR\DiskLens.exe"
        System::Call 'kernel32::CreateFileW(w "$INSTDIR\DiskLens.exe", i 0x40000000, i 7, p 0, i 3, i 0x00200000, p 0) p.r0'
        ${If} $0 == -1
            MessageBox MB_OK|MB_ICONSTOP "此目录中的盘镜正在使用或不可写。请关闭该实例并检查权限后重试。" /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
        System::Call 'kernel32::CloseHandle(p r0)'
    ${EndIf}
!macroend

Var VerifyOnly

Function .onInit
    SetShellVarContext current
    SetRegView 64
    ${Unless} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "此安装包仅支持 Windows x64。" /SD IDOK
        Abort
    ${EndUnless}
    ${Unless} ${AtLeastWin10}
        MessageBox MB_OK|MB_ICONSTOP "需要 Windows 10 / 11 x64。" /SD IDOK
        Abort
    ${EndUnless}
    StrCpy $VerifyOnly 0
    IfSilent 0 normal_install
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/VERIFY" $1
    IfErrors normal_install
    StrCpy $VerifyOnly 1
    Goto check_install
normal_install:
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\DiskLens"
check_install:
    ; Never install into a directory link or overwrite an unrelated non-empty folder.
    System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i.r0'
    ${If} $0 != -1
        IntOp $1 $0 & 0x410
        ${If} $1 != 0x10
            Goto unsafe_install
        ${EndIf}
    ${EndIf}
    FindFirst $2 $3 "$INSTDIR\*"
scan_install:
    StrCmp $3 "" empty_install
    StrCmp $3 "." next_entry
    StrCmp $3 ".." next_entry
    FindClose $2
    StrCmp $VerifyOnly 1 unsafe_install
    ReadRegStr $0 HKCU "${PRODUCT_KEY}" "InstallLocation"
    StrCmp $0 $INSTDIR 0 unsafe_install
    IfFileExists "$INSTDIR\Uninstall.exe" 0 unsafe_install
    IfFileExists "$INSTDIR\DiskLens.exe" 0 unsafe_install
    !insertmacro RequireInactiveCopy
    Return
next_entry:
    FindNext $2 $3
    Goto scan_install
empty_install:
    FindClose $2
    Return
unsafe_install:
    MessageBox MB_OK|MB_ICONSTOP "安装目录不是普通空目录，也不是已登记的盘镜安装位置。未写入。" /SD IDOK
    Abort
FunctionEnd

Section "盘镜"
    SetOutPath "$INSTDIR"
    SetOverwrite on
    File /r "${PAYLOAD}\*"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    ${If} $VerifyOnly != 1
        CreateDirectory "$SMPROGRAMS\盘镜 DiskLens"
        CreateShortCut "$SMPROGRAMS\盘镜 DiskLens\盘镜 DiskLens.lnk" "$INSTDIR\DiskLens.exe"
        WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayName" "盘镜 DiskLens"
        WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayVersion" "${VERSION}"
        WriteRegStr HKCU "${PRODUCT_KEY}" "InstallLocation" "$INSTDIR"
        WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayIcon" "$INSTDIR\DiskLens.exe"
        WriteRegStr HKCU "${PRODUCT_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
        WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoModify" 1
        WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoRepair" 1
    ${EndIf}
SectionEnd

Function un.onInit
    SetShellVarContext current
    SetRegView 64
    !insertmacro RequireInactiveCopy
FunctionEnd

Section "Uninstall"
    SetOutPath "$TEMP"
    ; Generated manifest removes only shipped files. Never recursively delete user data.
    !include "${REMOVE_LIST}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    ReadRegStr $0 HKCU "${PRODUCT_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
        Delete "$SMPROGRAMS\盘镜 DiskLens\盘镜 DiskLens.lnk"
        RMDir "$SMPROGRAMS\盘镜 DiskLens"
        DeleteRegKey HKCU "${PRODUCT_KEY}"
    ${EndIf}
SectionEnd
