#!/bin/sh
#
# Sauvegarde de Krissvia, lancée par le timer systemd du même nom. Reprise de celle de DomoMud
# (deploy/raspberry/ de DomoMud), avec ses trois règles tirées de la corruption du 02/10/2026 :
#
#  1. JAMAIS un « cp » de la base. En mode WAL, le fichier .db seul est un instantané incomplet : les dernières
#     transactions vivent dans le -wal. « sqlite3 .backup » fait une copie cohérente pendant que le cœur écrit.
#
#  2. VÉRIFIER AVANT DE FAIRE TOURNER. Une base corrompue produit une sauvegarde corrompue, et une rotation
#     aveugle remplace en vingt-quatre heures toutes les copies saines par des copies inutilisables. Une copie
#     qui ne passe pas l'integrity_check est jetée et la rotation n'a pas lieu.
#
#  3. TOURNER SOUS L'UTILISATEUR DU SERVICE (krissvia). Lancé en root, SQLite peut créer un krissvia.db-shm
#     appartenant à root à côté de la base ; le cœur ne peut alors plus écrire et refuse de démarrer.
#
# Chaque sauvegarde est UNE archive : la base, plus ce qui vit à côté d'elle et ne se recrée pas — données des
# plugins (appareils suivis de la Livebox), widgets locaux, clés des cookies.
#
# Les réglages ntfy sont relus depuis la copie saine et mis en cache : le jour où la base devient illisible,
# c'est le cache qui permet encore de prévenir. Aucun secret n'est écrit dans ce script.

set -eu

DONNEES=${DONNEES:-/var/lib/krissvia}
BASE=${BASE:-$DONNEES/krissvia.db}
DEST=${DEST:-/var/backups/krissvia}
RETENTION=${RETENTION:-28}          # 28 copies = 7 jours à raison d'une toutes les 6 h
CACHE_NTFY="$DEST/.ntfy"

horodate=$(date +%Y%m%d-%H%M%S)
travail="$DEST/.en-cours-$horodate"

umask 077
mkdir -p "$DEST"
chmod 700 "$DEST"

notifier() {
    [ -r "$CACHE_NTFY" ] || return 0
    # shellcheck disable=SC1090
    . "$CACHE_NTFY"
    [ "${NTFY_ACTIF:-}" = 1 ] && [ -n "${NTFY_URL:-}" ] && [ -n "${NTFY_TOPIC:-}" ] || return 0
    if [ -n "${NTFY_TOKEN:-}" ]; then
        curl -fsS --max-time 10 -H "Title: $1" -H "Priority: 5" -H "Tags: rotating_light" \
             -H "Authorization: Bearer $NTFY_TOKEN" -d "$2" "$NTFY_URL/$NTFY_TOPIC" >/dev/null || true
    else
        curl -fsS --max-time 10 -H "Title: $1" -H "Priority: 5" -H "Tags: rotating_light" \
             -d "$2" "$NTFY_URL/$NTFY_TOPIC" >/dev/null || true
    fi
}

echec() {
    echo "ÉCHEC : $1" >&2
    notifier "Sauvegarde Krissvia en échec" "$1"
    rm -rf "$travail"
    exit 1
}

[ -r "$BASE" ] || echec "Base introuvable ou illisible : $BASE"
mkdir -p "$travail"

# ── 1. Copie à chaud, cohérente même pendant que le cœur écrit ────────────────
sqlite3 "$BASE" ".backup '$travail/krissvia.db'" 2>/dev/null \
    || echec "La copie de $BASE a échoué (sqlite3 .backup)."

# ── 2. Contrôle d'intégrité AVANT toute rotation ──────────────────────────────
verdict=$(sqlite3 "$travail/krissvia.db" "PRAGMA integrity_check;" 2>&1 | head -1)
[ "$verdict" = "ok" ] \
    || echec "Base corrompue ($verdict). Sauvegarde refusée : les copies précédentes sont conservées intactes."

# ── 3. Rafraîchir le cache ntfy depuis la copie saine ─────────────────────────
# Document « ntfy » de la collection « notifications », écrit en snake_case par NotificationService.
champ() {
    sqlite3 "$travail/krissvia.db" "SELECT COALESCE(json_extract(json,'\$.$1'), '')
        FROM config_documents WHERE collection='notifications' AND id='ntfy';" 2>/dev/null
}
{
    echo "NTFY_ACTIF='$(champ enabled)'"
    echo "NTFY_URL='$(champ base_url)'"
    echo "NTFY_TOPIC='$(champ topic)'"
    echo "NTFY_TOKEN='$(champ token)'"
} > "$CACHE_NTFY"
chmod 600 "$CACHE_NTFY"

# ── 4. Ce qui vit à côté de la base ───────────────────────────────────────────
for d in plugins-data widgets keys; do
    if [ -d "$DONNEES/$d" ]; then cp -R "$DONNEES/$d" "$travail/"; fi
done

# ── 5. Publication puis rotation ──────────────────────────────────────────────
final="$DEST/krissvia-$horodate.tar.gz"
tar -czf "$final.tmp" -C "$travail" .
mv "$final.tmp" "$final"
chmod 600 "$final"
rm -rf "$travail"

# Les plus récentes d'abord ; au-delà de RETENTION, on supprime.
ls -1t "$DEST"/krissvia-*.tar.gz 2>/dev/null | tail -n +$((RETENTION + 1)) | xargs -r rm -f

nb=$(ls -1 "$DEST"/krissvia-*.tar.gz 2>/dev/null | wc -l)
echo "Sauvegarde OK : $(basename "$final") ($(du -h "$final" | cut -f1)) — $nb copie(s) conservée(s)"
