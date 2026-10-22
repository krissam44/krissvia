#!/bin/sh
#
# Installe ou met à jour Krissvia sur le Raspberry Pi, depuis l'archive extraite (docs/DEPLOIEMENT.md).
#
#     tar -xzf krissvia-<version>.tar.gz -C /tmp/krissvia-<version>
#     sudo /tmp/krissvia-<version>/raspberry/installer.sh
#
# Le même script sert aux deux : il est idempotent. Ce qu'il garantit, et pourquoi :
#
#  1. ARRÊT VÉRIFIÉ AVANT DE TOUCHER À QUOI QUE CE SOIT. krissvia.service porte Restart=always : un service qui
#     paraît arrêté peut être revenu dix secondes plus tard, et deux processus sur la même base SQLite, c'est ce
#     qui a corrompu celle de DomoMud le 02/10/2026. On attend que systemd dise « inactive » ET qu'aucun processus
#     de l'utilisateur krissvia (cœur ou plugin) ne reste.
#
#  2. LE PROGRAMME EST REMPLACÉ EN ENTIER, LES DONNÉES JAMAIS. /opt/krissvia/{server,plugins,raspberry} sont
#     supprimés puis recopiés : un fichier retiré d'une version ne traîne pas dans la suivante. Rien de propre
#     à la machine n'y vit — base, clés, données des plugins et widgets sont dans /var/lib/krissvia, les secrets
#     dans /etc/krissvia/krissvia.env.
#
#  3. LE DOSSIER DE LA BASE APPARTIENT À krissvia. SQLite écrit -wal et -shm à côté du fichier : c'est le
#     DOSSIER qui doit être modifiable, pas seulement krissvia.db.

set -eu

UTILISATEUR=krissvia
PROGRAMME=/opt/krissvia
DONNEES=/var/lib/krissvia
SAUVEGARDES=/var/backups/krissvia
CONFIG=/etc/krissvia
DOTNET=/usr/bin/dotnet

SOURCE=$(cd "$(dirname "$0")/.." && pwd)

erreur() { echo "ERREUR : $1" >&2; exit 1; }
info() { echo "→ $1"; }

[ "$(id -u)" -eq 0 ] || erreur "à lancer avec sudo."
[ -f "$SOURCE/server/Krissvia.Server.dll" ] || erreur "$SOURCE/server/Krissvia.Server.dll introuvable : lancer le script depuis l'archive extraite."
[ "$SOURCE" != "$PROGRAMME" ] || erreur "extraire l'archive ailleurs que dans $PROGRAMME (par exemple /tmp/krissvia-<version>)."

# ── Prérequis ─────────────────────────────────────────────────────────────────
[ -x "$DOTNET" ] || erreur "$DOTNET introuvable. Si dotnet est installé ailleurs : sudo ln -s \"\$(command -v dotnet)\" $DOTNET"
"$DOTNET" --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' \
    || erreur "le runtime ASP.NET Core 10 n'est pas installé (dotnet --list-runtimes)."
command -v sqlite3 >/dev/null || echo "ATTENTION : sqlite3 absent, la sauvegarde échouera (sudo apt install sqlite3)."

# ── Utilisateur et dossiers ───────────────────────────────────────────────────
if ! id "$UTILISATEUR" >/dev/null 2>&1; then
    info "Création de l'utilisateur système $UTILISATEUR"
    useradd --system --home-dir "$DONNEES" --no-create-home --shell /usr/sbin/nologin "$UTILISATEUR"
fi

install -d -o root -g root -m 755 "$PROGRAMME"
install -d -o "$UTILISATEUR" -g "$UTILISATEUR" -m 750 "$DONNEES"
install -d -o "$UTILISATEUR" -g "$UTILISATEUR" -m 700 "$SAUVEGARDES"
install -d -o root -g root -m 755 "$CONFIG"

# Une installation déplacée à la main peut avoir laissé des fichiers à root dans le dossier des données ;
# un krissvia.db-shm à root suffit à empêcher le démarrage.
chown -R "$UTILISATEUR:$UTILISATEUR" "$DONNEES"

# ── Arrêt vérifié ─────────────────────────────────────────────────────────────
etait_actif=non
if systemctl is-active --quiet krissvia 2>/dev/null; then
    etait_actif=oui
    info "Arrêt de krissvia"
    systemctl stop krissvia
fi
n=0
while systemctl is-active --quiet krissvia 2>/dev/null || pgrep -u "$UTILISATEUR" >/dev/null 2>&1; do
    n=$((n + 1))
    [ $n -le 30 ] || erreur "krissvia ne s'arrête pas (processus : $(pgrep -u "$UTILISATEUR" -d ' ' || true)). Rien n'a été modifié."
    sleep 1
done

# ── Programme ─────────────────────────────────────────────────────────────────
info "Copie du programme dans $PROGRAMME"
rm -rf "$PROGRAMME/server" "$PROGRAMME/plugins" "$PROGRAMME/raspberry"
cp -R "$SOURCE/server" "$SOURCE/plugins" "$SOURCE/raspberry" "$PROGRAMME/"
chown -R root:root "$PROGRAMME"
chmod -R u=rwX,go=rX "$PROGRAMME"

# Ceinture : l'archive porte déjà le droit d'exécution des plugins C#, mais une archive refaite à la main ne
# l'aurait pas, et le cœur afficherait alors « Permission denied » dans la page Services.
for fiche in "$PROGRAMME"/plugins/*/plugin.json; do
    if [ ! -f "$fiche" ] || grep -q '"runtime"' "$fiche"; then continue; fi
    exe=$(sed -n 's/.*"executable"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$fiche" | head -1)
    if [ -n "$exe" ] && [ -f "$(dirname "$fiche")/$exe" ]; then chmod 755 "$(dirname "$fiche")/$exe"; fi
done

# ── Secrets ───────────────────────────────────────────────────────────────────
if [ ! -f "$CONFIG/krissvia.env" ]; then
    info "Création de $CONFIG/krissvia.env (clé d'API tirée au hasard)"
    cle=$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
    umask 077
    sed "s/^Api__Key=.*/Api__Key=$cle/" "$SOURCE/raspberry/krissvia.env.exemple" > "$CONFIG/krissvia.env"
    umask 022
fi
chown root:root "$CONFIG/krissvia.env"
chmod 600 "$CONFIG/krissvia.env"

# ── Unités systemd ────────────────────────────────────────────────────────────
info "Installation des unités systemd"
install -m 644 "$SOURCE/raspberry/krissvia.service" /etc/systemd/system/
install -m 644 "$SOURCE/raspberry/sauvegarde-krissvia.service" "$SOURCE/raspberry/sauvegarde-krissvia.timer" /etc/systemd/system/
install -m 755 "$SOURCE/raspberry/sauvegarde-krissvia.sh" /usr/local/bin/
systemctl daemon-reload
systemctl enable --quiet krissvia.service
systemctl enable --quiet --now sauvegarde-krissvia.timer

# ── Plugins Python ────────────────────────────────────────────────────────────
if ls "$PROGRAMME"/plugins/*/plugin.json >/dev/null 2>&1 && grep -lq '"runtime"[[:space:]]*:[[:space:]]*"python"' "$PROGRAMME"/plugins/*/plugin.json; then
    if ! runuser -u "$UTILISATEUR" -- python3 -c 'import websockets, sys; sys.exit(int(websockets.__version__.split(".")[0]) < 13)' 2>/dev/null; then
        echo "ATTENTION : python3 ou websockets ≥ 13 manquant pour $UTILISATEUR ; les plugins Python ne démarreront pas (sudo apt install python3-websockets)."
    fi
fi

# ── DomoMud ───────────────────────────────────────────────────────────────────
if systemctl is-active --quiet domomud 2>/dev/null; then
    echo
    echo "ATTENTION : domomud est en service. N'activez dans Krissvia ni thermostat, ni délestage, ni programmation"
    echo "tant qu'il tourne : les deux piloteraient les mêmes radiateurs (docs/DEPLOIEMENT.md §7)."
    echo
fi

# ── Démarrage ─────────────────────────────────────────────────────────────────
info "Démarrage de krissvia"
systemctl start krissvia
sleep 3
systemctl is-active --quiet krissvia || erreur "krissvia ne démarre pas : journalctl -u krissvia -n 50 --no-pager"

[ "$etait_actif" = oui ] && quoi="mis à jour" || quoi="installé"
echo
echo "Krissvia $quoi : http://$(hostname -I | awk '{print $1}'):12795"
echo "Premier démarrage : le lien de création du compte est dans le journal :"
echo "    journalctl -u krissvia --no-pager | grep installation"
