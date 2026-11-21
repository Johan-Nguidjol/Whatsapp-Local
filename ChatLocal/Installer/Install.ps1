$ErrorActionPreference = 'Stop'

$appName = 'WhatsApp Local'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\WhatsApp Local'
$archivePath = Join-Path $PSScriptRoot 'WhatsAppLocal-win-x64.zip'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('WhatsAppLocal-Install-' + [guid]::NewGuid().ToString('N'))
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startShortcutPath = Join-Path $startMenuDirectory 'WhatsApp Local.lnk'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WhatsApp Local.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WhatsAppLocal'

try {
    if (-not (Test-Path -LiteralPath $archivePath)) { throw 'Les fichiers d’installation sont incomplets.' }
    if (Get-Process -Name ChatLocal -ErrorAction SilentlyContinue) {
        throw 'Fermez WhatsApp Local avant de lancer ou de mettre à jour l’installation.'
    }

    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $tempRoot -Force
    if (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'ChatLocal.exe'))) {
        throw 'Le programme principal est absent de l’archive d’installation.'
    }

    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $tempRoot '*') -Destination $installRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $installRoot 'Uninstall.ps1') -Force

    New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutPath in @($startShortcutPath, $desktopShortcutPath)) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $installRoot 'ChatLocal.exe'
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.IconLocation = (Join-Path $installRoot 'ChatLocal.exe') + ',0'
        $shortcut.Save()
    }

    New-Item -Path $uninstallKey -Force | Out-Null
    Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value $appName
    Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.0.0'
    Set-ItemProperty -Path $uninstallKey -Name Publisher -Value 'ChatLocal'
    Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot
    Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value ((Join-Path $installRoot 'ChatLocal.exe') + ',0')
    Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installRoot 'Uninstall.ps1') + '"')
    Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord

    Write-Host "$appName a été installé dans : $installRoot"
    Write-Host 'Un raccourci a été ajouté au menu Démarrer et au bureau.'
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedAllowedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTempRoot.StartsWith($resolvedAllowedTemp, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTempRoot)) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
