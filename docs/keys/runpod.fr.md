# Runpod : clé d'API pour TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · **Français** · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Toutes les pages](README.md)

## À quoi TaskHub l'utilise
- **Surveiller :** vos pods, leur charge, leur prix, les dépenses à ce jour (facturation Runpod), votre solde et votre autonomie.
- **Actions** (uniquement si la clé les autorise) : Arrêter, Démarrer, Redémarrer — depuis le panneau, une notification ou Telegram.
- **Supprimer** n'est jamais fait automatiquement. Cela ne fonctionne que dans le panneau TaskHub, après avoir saisi le nom du pod.

## Créer la clé
1. Ouvrez la console Runpod : <https://console.runpod.io/user/credentials>.
2. Sous **API Keys**, cliquez sur **Create API Key**.
3. Saisissez un nom (par exemple `TaskHub`) et choisissez le type (voir ci-dessous).
4. Cliquez sur **Create** et copiez la clé tout de suite — elle n'est affichée qu'une seule fois ; Runpod ne la conserve pas.

## Quel type choisir
| Vous voulez | Type | Remarques |
|---|---|---|
| (a) Surveiller uniquement | **Read Only** | Recommandé si vous n'avez pas besoin des boutons. Runpod ne précise pas si une clé Read Only peut lire la facturation et le solde ; si TaskHub n'affiche pas de solde, c'est la raison. |
| (b) Arrêter / Démarrer / Redémarrer | **All** | Runpod ne documente les portées **Restricted** que pour les endpoints Serverless, pas pour les pods — pour agir sur les pods, utilisez donc **All**. |

TaskHub vérifie une fois (puis chaque jour) si la clé a le droit d'agir et n'affiche les boutons d'action que si c'est le cas.
Une clé de type **All** pourrait aussi supprimer des pods — TaskHub ne le fait jamais de lui-même, seulement depuis le panneau, après
que vous avez saisi le nom.

## La coller dans TaskHub
1. TaskHub → **Paramètres** → **Connecteurs** → **Runpod**.
2. Activez-le, collez la clé, cliquez sur **Enregistrer**.
3. La ligne doit indiquer **Connecté, solde …** (ou **Connecté**). Si Runpod refuse la clé, rien n'est enregistré.

## Où la clé est conservée
- Uniquement dans le Gestionnaire d'identification Windows — jamais dans `config.json`.
- Envoyée uniquement à `api.runpod.io` (REST pour les pods, la taille des volumes réseau et la facturation, GraphQL pour le solde).

## La révoquer
- **Chez Runpod :** <https://console.runpod.io/user/credentials> → l'icône de corbeille à côté de la clé → **Revoke Key**
  (ou désactivez la clé avec son interrupteur).
- **Dans TaskHub :** Paramètres → Connecteurs → Runpod → **Supprimer** (la retire du Gestionnaire d'identification).
  Désinstaller TaskHub avec **Supprimer aussi mes données** la supprime aussi ; une désinstallation simple la conserve.

## Documentation officielle
- Clés d'API : <https://docs.runpod.io/get-started/credentials>
- Limites de débit : <https://docs.runpod.io/api-reference-v2/rate-limits>
- Tarifs, et ce qui se passe à 0 $ : <https://docs.runpod.io/pods/pricing>
