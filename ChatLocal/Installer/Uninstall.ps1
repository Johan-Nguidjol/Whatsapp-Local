$ErrorActionPreference = 'Stop'
$installRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WhatsApp Local'))
if (-not [string]::Equals($installRoot, $expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Le dossier d’installation ne correspond pas à celui de WhatsApp Local.'
}
if (Get-Process -Name ChatLocal -ErrorAction SilentlyContinue) {
    throw 'Fermez WhatsApp Local avant de le désinstaller.'
}

$shell = New-Object -ComObject WScript.Shell
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WhatsApp Local.lnk'
$startShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WhatsApp Local.lnk'
Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WhatsAppLocal' -Recurse -Force -ErrorAction SilentlyContinue

$deleteScript = Join-Path ([System.IO.Path]::GetTempPath()) ('WhatsAppLocal-Remove-' + [guid]::NewGuid().ToString('N') + '.cmd')
$deleteContents = "@echo off`r`ntimeout /t 2 /nobreak >nul`r`nrmdir /s /q `"$installRoot`"`r`ndel `"%~f0`"`r`n"
[System.IO.File]::WriteAllText($deleteScript, $deleteContents, [System.Text.Encoding]::ASCII)
Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', "`"$deleteScript`"") -WindowStyle Hidden
Write-Host 'WhatsApp Local a été désinstallé. Les données du profil dans AppData ont été conservées.'
