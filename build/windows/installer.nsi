; ============================================================
; Ash Server — Windows NSIS Installer
; Build with:  makensis build\windows\installer.nsi
; Requires NSIS 3.x: https://nsis.sourceforge.io/
;
; Before running, publish the win-x64 binary:
;   dotnet publish ash-server-cs.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o build\dist\win-x64
; ============================================================

!define PRODUCT_NAME      "Ash Server"
!define PRODUCT_VERSION   "1.1.1"
!define SERVICE_NAME      "ash-server"
!define INSTALL_DIR       "$PROGRAMFILES64\Ash Server"
!define UNINSTALL_REG     "Software\Microsoft\Windows\CurrentVersion\Uninstall\AshServer"
!define PUBLISHER         "Ash Server Project"
!define PRODUCT_URL       "https://github.com/ssfdre38/ash-server-cs"

; Source directory (relative to THIS script file — build\windows\installer.nsi)
!define SRC_DIR           "..\dist\win-x64"

; ── NSIS settings ────────────────────────────────────────────────────────────
Name          "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile       "..\dist\ash-server-${PRODUCT_VERSION}-windows-x64-setup.exe"
InstallDir    "${INSTALL_DIR}"
InstallDirRegKey HKLM "${UNINSTALL_REG}" "InstallLocation"
RequestExecutionLevel admin
Unicode True

; Modern UI
!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON          "ash-server.ico"
!define MUI_UNICON        "ash-server.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ── Install section ───────────────────────────────────────────────────────────
Section "Install" SEC_MAIN
    SetOutPath "$INSTDIR"

    ; Copy all published files
    File /r "${SRC_DIR}\*.*"

    ; Write uninstaller
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; Registry: Add/Remove Programs entry
    WriteRegStr   HKLM "${UNINSTALL_REG}" "DisplayName"      "${PRODUCT_NAME}"
    WriteRegStr   HKLM "${UNINSTALL_REG}" "DisplayVersion"   "${PRODUCT_VERSION}"
    WriteRegStr   HKLM "${UNINSTALL_REG}" "Publisher"        "${PUBLISHER}"
    WriteRegStr   HKLM "${UNINSTALL_REG}" "URLInfoAbout"     "${PRODUCT_URL}"
    WriteRegStr   HKLM "${UNINSTALL_REG}" "InstallLocation"  "$INSTDIR"
    WriteRegStr   HKLM "${UNINSTALL_REG}" "UninstallString"  '"$INSTDIR\uninstall.exe"'
    WriteRegDWORD HKLM "${UNINSTALL_REG}" "NoModify"         1
    WriteRegDWORD HKLM "${UNINSTALL_REG}" "NoRepair"         1

    ; Start Menu shortcut (for manual start/stop)
    CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
    CreateShortCut  "$SMPROGRAMS\${PRODUCT_NAME}\Ash Server Admin.lnk" \
                    "http://localhost:18799/admin.html"
    CreateShortCut  "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall Ash Server.lnk" \
                    "$INSTDIR\uninstall.exe"

    ; Install bundled SQLite3 directly (zero-config, local scope visibility)
    DetailPrint "Installing bundled SQLite3 CLI..."
    File "dependencies\sqlite3.exe"

    ; Optional Tailscale Mesh VPN check and installation using bundled MSI
    nsExec::ExecToStack 'cmd.exe /c "where tailscale.exe"'
    Pop $0
    Pop $1
    ${If} $0 != 0
        MessageBox MB_YESNO|MB_ICONQUESTION "Tailscale Mesh VPN is highly recommended for secure remote access without opening public firewall ports.$\r$\n$\r$\nWould you like to install Tailscale now?" IDNO skip_tailscale
        DetailPrint "Extracting Tailscale installer..."
        InitPluginsDir
        File "/oname=$PLUGINSDIR\tailscale-setup.msi" "dependencies\tailscale-setup.msi"
        
        DetailPrint "Installing Tailscale silently..."
        ExecWait '"msiexec.exe" /i "$PLUGINSDIR\tailscale-setup.msi" /qn' $0
        ${If} $0 != 0
            DetailPrint "Failed to install Tailscale. Exit code: $0"
        ${Else}
            DetailPrint "Tailscale installed successfully."
        ${EndIf}
        skip_tailscale:
    ${EndIf}

    ; Register and start the Windows service
    nsExec::ExecToLog '"$INSTDIR\ash-server.exe" install-service'
    nsExec::ExecToLog 'sc.exe start ${SERVICE_NAME}'
SectionEnd

; ── Uninstall section ─────────────────────────────────────────────────────────
Section "Uninstall"
    ; Stop and remove service
    nsExec::ExecToLog '"$INSTDIR\ash-server.exe" uninstall-service'

    ; Remove files (preserve database and config.json so user data survives)
    Delete "$INSTDIR\ash-server.exe"
    Delete "$INSTDIR\sqlite3.exe"
    Delete "$INSTDIR\appsettings.json"
    Delete "$INSTDIR\uninstall.exe"
    RMDir  /r "$INSTDIR\wwwroot"
    RMDir  /r "$INSTDIR\personality"

    ; Remove Start Menu shortcuts
    RMDir /r "$SMPROGRAMS\${PRODUCT_NAME}"

    ; Remove registry entries
    DeleteRegKey HKLM "${UNINSTALL_REG}"

    ; Leave $INSTDIR itself (may still have ash_server.db / config.json)
    ; User can manually delete the folder if they want a clean uninstall
    MessageBox MB_ICONINFORMATION \
        "Ash Server has been removed.$\r$\n\
Your database and config.json have been preserved at:$\r$\n\
$INSTDIR$\r$\n$\r$\n\
Delete that folder manually if you want a complete removal."
SectionEnd
