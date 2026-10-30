# Krissvia

Plateforme domotique : cœur C#, plugins multilangages (processus séparés), frontend déclaratif.
Successeur de DomoMud.

## Règles de dépendance

- `Contracts` ne dépend de rien.
- `Core` ne dépend que de `Contracts`.
- `Climate`, `Persistence`, `PluginHost` dépendent de `Core` et `Contracts`.
- `Server` (composition root) référence tout.
- **Un plugin ne référence jamais `Core`** — uniquement `Contracts` (et plus tard un SDK).

Ces règles sont vérifiées par `tests/Krissvia.Architecture.Tests`.

## Build

    dotnet build
    dotnet test

## Appli mobile

`mobile/` (MAUI, hors de la solution) : voir `mobile/README.md`.

## Déploiement

Sur le Raspberry Pi (linux-arm64, systemd) : `deploy/publier.ps1`, puis `docs/DEPLOIEMENT.md`.
