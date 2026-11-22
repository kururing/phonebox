# PhoneBox Agent Instructions

## Auto-Rebuild After Changes

Always rebuild after editing `agent/PhoneBox.Agent/**` or `android/**` (including `android/app/src/main/assets/www/**`):

1. **Agent (`agent/PhoneBox.Agent/**`)**
   - Always run unit tests and rebuild Release after edits:
     ```powershell
     dotnet test agent/PhoneBox.Agent.Tests --nologo -v q
     dotnet build agent/PhoneBox.Agent -c Release -f net10.0-windows10.0.19041.0 --nologo -v q
     ```
   - Rebuild and reinstall the Windows Agent:
     ```powershell
     .\scripts\install.ps1
     ```
     (If `PhoneBoxAgent` is running elevated and `Stop-Process` hits `Access is denied`, close it from the tray or run `.\scripts\install.ps1` in an Administrator PowerShell.)

2. **Android App / Web Assets (`android/**`)**
   - Whenever files under `android/` (including `android/app/src/main/assets/www/**`) are modified, rebuild the APK (both debug & release):
     ```powershell
     $env:JAVA_HOME = "C:\Program Files (x86)\Android\openjdk\jdk-17.0.14"
     $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
     $env:Path = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:Path"
     Push-Location android; .\gradlew.bat assembleDebug assembleRelease --console=plain -q; Pop-Location
     ```
   - Or rebuild & install both Agent and APK together when a phone is connected over ADB:
     ```powershell
     .\scripts\install.ps1 -Apk -BuildApk
     ```
