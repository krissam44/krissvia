<#
.SYNOPSIS
    Construit l'APK Android signé de l'appli mobile Krissvia, à installer directement sur le téléphone.

.DESCRIPTION
    Reprend les pièges de l'appli DomoMud (mobile/README.md) :
      - AndroidPackageFormat=apk, sinon Release produit un .aab (Play Store), non installable tel quel ;
      - dossier de sortie vidé d'abord : un APK périmé d'une construction précédente ne peut pas être pris pour le bon ;
      - vérification que l'APK est bien signé (bloc « APK Sig Block 42 ») avant de le donner.

    Le mot de passe du keystore est tapé ici, masqué, et passé à la construction par une variable d'environnement
    du seul processus (« env: » des propriétés de signature .NET Android) : il n'apparaît ni dans la ligne de
    commande, ni dans un fichier, ni dans le journal de construction.

    Sans keystore, le script propose de le créer avec keytool (validité 30 ans). Le keystore reste HORS du dépôt :
    le perdre, c'est ne plus pouvoir mettre à jour l'appli installée sans la désinstaller.

.EXAMPLE
    .\mobile\publier-apk.ps1
    .\mobile\publier-apk.ps1 -Keystore 'D:\Cles\krissvia.keystore' -Alias krissvia
#>
param(
    [string]$Keystore = 'D:\Cles\krissvia.keystore',
    [string]$Alias = 'krissvia'
)

$ErrorActionPreference = 'Stop'
$racine = Split-Path -Parent $PSScriptRoot
$projet = Join-Path $PSScriptRoot 'Krissvia.Mobile\Krissvia.Mobile.csproj'
$sortie = Join-Path $racine 'publish\apk'
$variable = 'KRISSVIA_APK_PASS'

if (-not (Test-Path $Keystore)) {
    Write-Host "Aucun keystore à $Keystore."
    if ((Read-Host 'Le créer maintenant ? (o/n)') -ne 'o') { exit 1 }
    $keytool = Get-ChildItem 'C:\Program Files\Microsoft\jdk*\bin\keytool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $keytool) { throw 'keytool introuvable (JDK Microsoft, installé avec le workload Android).' }
    New-Item -ItemType Directory -Force (Split-Path -Parent $Keystore) | Out-Null
    # keytool demande lui-même le mot de passe et le nom : rien ne passe par ce script.
    & $keytool.FullName -genkeypair -v -keystore $Keystore -alias $Alias -keyalg RSA -keysize 2048 -validity 10950
    if ($LASTEXITCODE -ne 0) { throw 'Création du keystore refusée par keytool.' }
}

$secret = Read-Host "Mot de passe du keystore ($Alias)" -AsSecureString
$clair = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret))
[Environment]::SetEnvironmentVariable($variable, $clair, 'Process')
$clair = $null

try {
    # Depuis la racine du dépôt : son global.json choisit le SDK, quel que soit le dossier d'où le script est lancé.
    Push-Location $racine
    if (Test-Path $sortie) { Remove-Item -Recurse -Force $sortie }

    dotnet publish $projet -f net10.0-android -c Release -o $sortie `
        -p:AndroidPackageFormat=apk `
        -p:AndroidKeyStore=true `
        -p:AndroidSigningKeyStore="$Keystore" `
        -p:AndroidSigningKeyAlias=$Alias `
        -p:AndroidSigningStorePass="env:$variable" `
        -p:AndroidSigningKeyPass="env:$variable"
    # Un mot de passe faux se lit « java.exe a quitté avec le code 2 » : message opaque, cause simple.
    if ($LASTEXITCODE -ne 0) { throw 'Construction échouée (un mot de passe faux donne « java.exe … code 2 »).' }
}
finally {
    [Environment]::SetEnvironmentVariable($variable, $null, 'Process')
    Pop-Location
}

$apk = Get-ChildItem $sortie -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $apk) { throw "Aucun APK signé dans $sortie." }

$octets = [IO.File]::ReadAllBytes($apk.FullName)
if (-not ([Text.Encoding]::ASCII.GetString($octets).Contains('APK Sig Block 42'))) {
    throw "$($apk.Name) n'est pas signé : ne pas l'installer."
}

[xml]$csproj = Get-Content $projet
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.ApplicationDisplayVersion } | Select-Object -First 1).ApplicationDisplayVersion
$final = Join-Path $sortie "Krissvia-$version.apk"
Copy-Item $apk.FullName $final -Force

Write-Host ''
Write-Host "APK signé : $final" -ForegroundColor Green
Write-Host "  $([math]::Round((Get-Item $final).Length / 1MB, 1)) Mo, construit le $((Get-Item $final).LastWriteTime)"
Write-Host '  Sur le téléphone : autoriser les sources inconnues pour l''appli qui ouvre le fichier, puis l''installer.'
