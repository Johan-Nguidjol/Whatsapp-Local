$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectDirectory 'ChatLocal.csproj'
$artifactsDirectory = Join-Path $projectDirectory 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish-win-x64'
$stagingDirectory = Join-Path $artifactsDirectory ('setup-package-' + [guid]::NewGuid().ToString('N'))
$publishArchive = Join-Path $stagingDirectory 'WhatsAppLocal-win-x64.zip'
$finalPackage = Join-Path $artifactsDirectory 'WhatsAppLocal-Setup.zip'

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "La publication autonome de l’application a échoué." }

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $publishArchive -CompressionLevel Optimal
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $stagingDirectory

$readme = @'
WHATSAPP LOCAL — INSTALLATION WINDOWS 64 BITS

1. Extraire tout le contenu de WhatsAppLocal-Setup.zip dans un dossier.
2. Double-cliquer sur Install.cmd.
3. L’application sera installée dans AppData\Local\Programs\WhatsApp Local.
   Un raccourci sera ajouté au bureau et au menu Démarrer.

Cette version est autonome : .NET n’a pas besoin d’être installé séparément.
L’application fonctionne en réseau local. Pour l’assistant IA en ligne, chaque utilisateur ajoute sa propre clé API dans l’onglet IA.
'@
Set-Content -LiteralPath (Join-Path $stagingDirectory 'LISEZMOI-INSTALLATION.txt') -Value $readme -Encoding UTF8

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $finalPackage -CompressionLevel Optimal -Force
Write-Host "Paquet prêt : $finalPackage"

$innoCompiler = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
if (-not $innoCompiler) {
    $compilerCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $innoCompiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ($innoCompiler) {
    & $innoCompiler (Join-Path $PSScriptRoot 'WhatsAppLocal.iss')
    if ($LASTEXITCODE -ne 0) { throw 'La compilation Inno Setup a échoué.' }
    Write-Host "Installateur prêt : $(Join-Path $artifactsDirectory 'WhatsAppLocal-Setup.exe')"
}
else {
    Write-Host "Inno Setup Compiler n’est pas installé. Installe Inno Setup, puis relance ce script pour générer le Setup.exe."
}
