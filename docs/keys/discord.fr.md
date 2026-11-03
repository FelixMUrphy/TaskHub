# Discord : jeton de bot pour TaskHub

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · **Français** · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Toutes les pages](README.md)

## À quoi TaskHub l'utilise
- Envoie vos notifications TaskHub, la progression des tâches et des graphiques en messages privés depuis **votre propre** bot.
- Reçoit vos appuis sur les boutons et les commandes `/tasks`, `/mute`, `/unmute`, via une connexion que votre PC ouvre vers Discord. Aucune adresse publique n'est nécessaire.
- Il ne lit pas vos messages : il ne demande aucun intent privilégié et jamais le contenu des messages.

## Créer le bot
1. Ouvrez le Discord Developer Portal : <https://discord.com/developers/applications?new_application=true>.
2. Cliquez sur **Create App**, saisissez un nom (par exemple `TaskHub`) et cliquez sur **Create**.
3. Sur la page **Bot**, sous **Token**, cliquez sur **Reset Token** et copiez le jeton. Discord ne l'affiche qu'une seule fois.
4. Gardez le bot pour vous : sur la page **Installation**, réglez **Install Link** sur **None**, puis sur la page **Bot**, désactivez **Public Bot**. Ainsi désactivé, vous seul pouvez ajouter le bot à des serveurs. (Si le Portal refuse, faites d'abord l'étape **Installation**.)
5. Laissez le reste tel quel : aucun intent privilégié, et **Interactions Endpoint URL** vide. Avec une URL à cet endroit, Discord lui envoie les appuis sur les boutons au lieu de TaskHub, et TaskHub vous avertit.

## Le coller dans TaskHub
1. TaskHub → **Paramètres** → **Discord**. Collez le jeton et cliquez sur **Connecter**. Un jeton que Discord refuse n'est pas enregistré.
2. Ajoutez le bot à un serveur (section suivante). Discord n'autorise un bot à écrire qu'à quelqu'un avec qui il partage un serveur ; d'ici là, TaskHub indique que le bot ne peut pas encore vous écrire.
3. Cliquez sur **Vérifier à nouveau**. Le bot vous envoie un message privé avec un bouton **Connecter**. Appuyez dessus.

Seul le propriétaire de l'application peut être associé (pour l'application d'une équipe, le propriétaire de l'équipe). Seuls les appuis de cette personne, dans ce chat, ont un effet.

## Ajouter le bot à un serveur
1. Dans TaskHub, cliquez sur **Ajouter le bot à un serveur**. La page de Discord s'ouvre dans votre navigateur.
2. Dans la liste des serveurs, choisissez-en un où vous pouvez ajouter des bots : vous en êtes propriétaire ou vous y avez **Manage Server**. Le bot n'y reçoit aucune autorisation et ne le lit pas ; il doit seulement partager un serveur avec vous.
3. Confirmez sur cette page, puis revenez dans TaskHub et cliquez sur **Vérifier à nouveau**.

**Pas de serveur à vous ?** En créer un est gratuit : dans l'application Discord, cliquez sur **Add a Server** en bas de la barre latérale gauche, choisissez **Create My Own**, puis **For me and my friends**, saisissez un nom et cliquez sur **Create**. Reprenez ensuite les étapes ci-dessus.

**Toujours aucun message ?** Vos paramètres de confidentialité le bloquent peut-être : cliquez sur le nom du serveur, ouvrez **Privacy Settings**, activez **Allow direct messages from server members**, puis cliquez sur **Vérifier à nouveau**.

## Où le jeton est conservé
- Uniquement dans le Gestionnaire d'identification Windows, jamais dans `config.json`. Si le Gestionnaire d'identification n'est pas disponible, TaskHub l'indique et ne se connecte pas.
- Envoyé uniquement à `discord.com` et à la passerelle de Discord (`*.discord.gg`).
- Votre identifiant d'utilisateur Discord, celui du chat et celui de l'application sont enregistrés dans `config.json`.

## Le révoquer
- **Chez Discord :** Developer Portal → votre application → **Bot** → **Reset Token**. L'ancien jeton cesse de fonctionner.
- **Dans TaskHub :** Paramètres → Discord → **Déconnecter** (le supprime du Gestionnaire d'identification). Le bot reste dans votre compte Discord jusqu'à ce que vous supprimiez l'application. Désinstaller TaskHub avec **Supprimer aussi mes données** supprime aussi le jeton ; une désinstallation simple le conserve.

## Si le jeton cesse de fonctionner
Discord réinitialise le jeton d'un bot après 1000 nouvelles connexions en un jour et prévient le propriétaire par e-mail. TaskHub reste très en dessous de cette limite et patiente quand il s'en approche. Si cela arrive malgré tout, la page indique que le jeton a été refusé : créez-en un nouveau et reconnectez-vous.

## Documentation officielle
- Prise en main : <https://docs.discord.com/developers/quick-start/getting-started>
- Bots et messages privés, serveurs en commun : <https://docs.discord.com/developers/interactions/application-commands>
- Créer un serveur : <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Messages privés des membres d'un serveur : <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Qui peut ajouter un bot (Public Bot) : <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL et passerelle : <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Limites de connexion : <https://docs.discord.com/developers/events/gateway>
- Politique de confidentialité : <https://discord.com/privacy>
