<#
.SYNOPSIS
    Publie le cœur et les plugins de Krissvia pour le Raspberry Pi (linux-arm64) en UNE archive .tar.gz.

.DESCRIPTION
    Voir docs/DEPLOIEMENT.md. L'archive contient :

        server/              le cœur (Krissvia.Server.dll, wwwroot, sdk/python/krissvia.py)
        plugins/<nom>/       chaque plugin C# publié, avec son plugin.json ; chaque plugin Python, tel quel
        raspberry/           l'unité systemd, l'installation et la sauvegarde

    Pourquoi PowerShell et pas un script bash : l'archive se fabrique ici avec System.Formats.Tar, qui écrit
    les droits Unix de chaque fichier. Un tar lancé depuis Windows (celui de Windows comme celui de Git Bash)
    ne voit pas le bit « exécutable » d'un fichier ELF sans extension ; or le cœur lance un plugin C# par son
    exécutable (plugin.json, « executable »), et un 644 donnerait « Permission denied » sur le Pi.

    Une seule archive, jamais une copie fichier par fichier : chez DomoMud, une copie interrompue a tronqué
    blazor.web.js et MudBlazor.min.js, et toute l'interface était morte sans une ligne dans le journal.

    Rien n'est construit dans les bin/obj du dépôt (--artifacts-path) : une instance de développement qui
    tourne depuis bin/Debug n'est ni dérangée ni verrouillée.

.PARAMETER Sortie
    Dossier de travail et de l'archive. Par défaut : publish/ à la racine du dépôt.

.PARAMETER Version
    Nom de version de l'archive. Par défaut : date du jour + commit court (20261022-7a3d9eb).
#>
[CmdletBinding()]
param(
    [string] $Sortie,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$depot = Split-Path -Parent $PSScriptRoot
if (-not $Sortie) { $Sortie = Join-Path $depot 'publish' }
$Sortie = [IO.Path]::GetFullPath($Sortie)

if (-not $Version) {
    $commit = (git -C $depot rev-parse --short HEAD 2>$null)
    $Version = (Get-Date -Format 'yyyyMMdd') + ($commit ? "-$commit" : '')
}

# Ce qui appartient à la machine, jamais à l'archive : jetons locaux, base, clés, données des plugins.
# appsettings.Development.json aussi : il accepte les plugins sans jeton, et ne doit pas pouvoir s'activer
# par erreur sur le Pi.
$exclusions = @(
    'appsettings.Local.json', 'appsettings.Development.json',
    '*.db', '*.db-wal', '*.db-shm', '*.db-journal',
    'keys', 'plugins-data', '__pycache__', 'test_*.py', '*.pdb'
)

function Test-Exclu([string] $relatif) {
    foreach ($segment in $relatif -split '[\\/]') {
        foreach ($motif in $exclusions) { if ($segment -like $motif) { return $true } }
    }
    return $false
}

$etat = git -C $depot status --porcelain 2>$null
if ($etat) {
    Write-Warning "Le dépôt a des modifications non validées : elles partiront dans l'archive. Publier depuis un checkout propre pour livrer exactement un commit."
}

$contenu = Join-Path $Sortie 'contenu'
$artefacts = Join-Path $Sortie 'obj'
if (Test-Path $contenu) { Remove-Item $contenu -Recurse -Force }
New-Item -ItemType Directory -Force $contenu | Out-Null

function Publier([string] $projet, [string] $destination) {
    Write-Host "→ $([IO.Path]::GetFileNameWithoutExtension($projet))" -ForegroundColor Cyan
    # Toujours le projet, jamais la solution : elle contient les tests. Lancé depuis le dépôt, pour que son
    # global.json choisisse le SDK (et pas celui du dossier où l'on se trouve).
    Push-Location $depot
    try {
        dotnet publish $projet -c Release -r linux-arm64 --self-contained false -p:DebugType=none `
            --artifacts-path $artefacts -o $destination --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de $projet" }
    }
    finally { Pop-Location }
}

# ── Le cœur ───────────────────────────────────────────────────────────────────
Publier (Join-Path $depot 'src/Krissvia.Server/Krissvia.Server.csproj') (Join-Path $contenu 'server')

# ── Les plugins ───────────────────────────────────────────────────────────────
# Les exécutables des plugins C# : ils recevront le droit d'exécution dans l'archive.
$executables = [Collections.Generic.HashSet[string]]::new()
foreach ($dossier in Get-ChildItem (Join-Path $depot 'plugins') -Directory) {
    $fiche = Join-Path $dossier.FullName 'plugin.json'
    if (-not (Test-Path $fiche)) { continue }
    $manifeste = Get-Content $fiche -Raw | ConvertFrom-Json
    $destination = Join-Path $contenu "plugins/$($dossier.Name)"
    $projet = Get-ChildItem $dossier.FullName -Filter '*.csproj' | Select-Object -First 1

    if ($projet) {
        Publier $projet.FullName $destination   # le csproj copie lui-même son plugin.json
        if (-not (Test-Path (Join-Path $destination 'plugin.json'))) { throw "$($dossier.Name) : plugin.json absent de la publication." }
        [void] $executables.Add("plugins/$($dossier.Name)/$($manifeste.executable)")
    }
    elseif ($manifeste.PSObject.Properties['runtime']) {
        Write-Host "→ $($dossier.Name) ($($manifeste.runtime), copié)" -ForegroundColor Cyan
        foreach ($f in Get-ChildItem $dossier.FullName -Recurse -File) {
            $relatif = [IO.Path]::GetRelativePath($dossier.FullName, $f.FullName)
            if (Test-Exclu $relatif) { continue }
            $cible = Join-Path $destination $relatif
            New-Item -ItemType Directory -Force (Split-Path $cible) | Out-Null
            Copy-Item $f.FullName $cible
        }
    }
    else {
        Write-Warning "$($dossier.Name) : ni projet C# ni runtime déclaré, ignoré."
    }
}

# ── Les fichiers du Pi ────────────────────────────────────────────────────────
# Fins de ligne forcées en LF : .gitattributes s'en charge au checkout, mais un éditeur Windows peut remettre des
# CRLF, et sous Linux « #!/bin/sh\r » ne se lance pas, « User=krissvia\r » n'existe pas.
New-Item -ItemType Directory -Force (Join-Path $contenu 'raspberry') | Out-Null
foreach ($f in Get-ChildItem (Join-Path $PSScriptRoot 'raspberry') -File) {
    $texte = [IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $contenu "raspberry/$($f.Name)"), $texte, [Text.UTF8Encoding]::new($false))
}

# ── L'archive ─────────────────────────────────────────────────────────────────
$archive = Join-Path $Sortie "krissvia-$Version.tar.gz"
if (Test-Path $archive) { Remove-Item $archive -Force }

$rwx = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'   # 755
$rw  = [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'                                             # 644

$flux = [IO.File]::Create($archive)
$gzip = [IO.Compression.GZipStream]::new($flux, [IO.Compression.CompressionLevel]::Optimal)
$tar  = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
$exclus = [Collections.Generic.List[string]]::new()
try {
    $dossiersVus = [Collections.Generic.HashSet[string]]::new()
    foreach ($f in Get-ChildItem $contenu -Recurse -File | Sort-Object FullName) {
        $relatif = [IO.Path]::GetRelativePath($contenu, $f.FullName).Replace('\', '/')
        if (Test-Exclu $relatif) { $exclus.Add($relatif); continue }

        # Les dossiers parents d'abord, en 755 : sans entrée explicite, certains tar les créent en 700.
        $parties = $relatif.Split('/')
        for ($i = 1; $i -lt $parties.Length; $i++) {
            $d = ($parties[0..($i - 1)] -join '/') + '/'
            if ($dossiersVus.Add($d)) {
                $entreeDossier = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, $d)
                $entreeDossier.Mode = $rwx
                $tar.WriteEntry($entreeDossier)
            }
        }

        $entree = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $relatif)
        $entree.Mode = ($executables.Contains($relatif) -or $relatif.EndsWith('.sh')) ? $rwx : $rw
        $entree.ModificationTime = $f.LastWriteTimeUtc
        $lecture = $f.OpenRead()
        try { $entree.DataStream = $lecture; $tar.WriteEntry($entree) } finally { $lecture.Dispose() }
    }
}
finally {
    $tar.Dispose(); $gzip.Dispose(); $flux.Dispose()
}

foreach ($e in $executables) {
    if (-not (Test-Path (Join-Path $contenu $e))) { throw "Exécutable de plugin introuvable dans la publication : $e" }
}
if ($exclus.Count) { Write-Host "Écartés de l'archive : $($exclus -join ', ')" -ForegroundColor DarkGray }

$taille = '{0:N1} Mo' -f ((Get-Item $archive).Length / 1MB)
Write-Host "`nArchive : $archive ($taille)" -ForegroundColor Green
Write-Host "Exécutables : $($executables -join ', ')"
