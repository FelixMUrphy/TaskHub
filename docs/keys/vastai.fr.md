# Vast.ai : clé d'API pour TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · **Français** · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Toutes les pages](README.md)

## À quoi TaskHub l'utilise
- **Surveiller :** vos instances, leur charge, leur prix, les frais des 30 derniers jours, votre solde et votre crédit.
- **Actions** (uniquement si la clé les autorise) : Arrêter, Démarrer, Redémarrer — depuis le panneau, une notification ou Telegram.
- **Supprimer (destroy)** n'est jamais fait automatiquement. Cela ne fonctionne que dans le panneau TaskHub, après avoir saisi le nom.

## Autorisations minimales
| Vous voulez | Autorisations |
|---|---|
| (a) Surveiller uniquement | `instance_read`, `user_read`, `billing_read` |
| (b) Arrêter / Démarrer / Redémarrer | celles ci-dessus + `instance_write` |

À savoir : `instance_write` de Vast.ai couvre « creating, managing, and destroying instances and volumes » — il n'existe
aucune autorisation pour arrêter / démarrer sans pouvoir détruire. TaskHub ne détruit jamais de lui-même, seulement depuis le panneau, après que vous
avez saisi le nom.

## Créer la clé
**Dans la console :** ouvrez la section **Keys** de la console Vast.ai → **API Keys** → **+New**. Dans la fenêtre qui s'ouvre,
donnez-lui un nom (par exemple `TaskHub`) et choisissez les autorisations — toutes les autorisations de votre compte sont cochées par
défaut, ne gardez donc que celles du tableau. Créez-la et copiez la clé.

**Avec la CLI :** les pages d'autorisations de Vast.ai décrivent les clés restreintes via la CLI / l'API — utilisez cette voie si la
console ne permet pas de les restreindre. Enregistrez ceci sous `perms.json` :

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Ajoutez `"instance_write": {}` pour les actions, puis exécutez :

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## La coller dans TaskHub
1. TaskHub → **Paramètres** → **Connecteurs** → **Vast.ai**.
2. Activez-le, collez la clé, cliquez sur **Enregistrer**.
3. La ligne doit indiquer **Connecté, solde …** (ou **Connecté**). Si Vast.ai refuse la clé, rien n'est enregistré.

TaskHub vérifie une fois (puis chaque jour) si la clé a le droit d'agir et n'affiche les boutons d'action que si c'est le cas.

## Où la clé est conservée
- Uniquement dans le Gestionnaire d'identification Windows — jamais dans `config.json`.
- Envoyée uniquement à `console.vast.ai`.

## La révoquer
- **Chez Vast.ai :** console → **Keys** → **Delete** à côté de la clé (**Reset** la remplace par une nouvelle).
- **Dans TaskHub :** Paramètres → Connecteurs → Vast.ai → **Supprimer** (la retire du Gestionnaire d'identification).

## Documentation officielle
- Clés d'API : <https://docs.vast.ai/guides/reference/keys>
- Autorisations : <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Facturation et solde : <https://docs.vast.ai/guides/reference/billing>
