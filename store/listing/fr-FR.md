# fr-FR

## Description

TaskHub rassemble au même endroit les tâches longues de votre PC et vous permet de les piloter : exports vidéo, rendus, compilations, sauvegardes, entraînement de modèles, exécutions de CI, machines GPU dans le cloud et scripts que les agents de programmation IA lancent pour vous.

Comment ça marche
Un programme ou un script signale sa progression à TaskHub via une API HTTP locale sur 127.0.0.1, la commande taskhub ou le client Python fourni. TaskHub affiche alors :
• une icône dans la zone de notification avec un anneau de progression et le nombre de tâches actives ;
• un panneau avec la progression, le temps restant, l'état et le journal de chaque tâche, ainsi que les widgets qu'elle demande : graphiques en courbes, en aires et en barres, jauges, valeurs, voyants, tableaux ;
• les commandes propres à la tâche : boutons, champs numériques, texte ou liste, pause, arrêt et fin forcée. Votre choix est renvoyé à la tâche ;
• des notifications Windows quand une tâche se termine, échoue ou ne donne plus de nouvelles, avec les boutons de la tâche si elle les demande.

« taskhub run -- <commande> » encapsule n'importe quelle ligne de commande : la sortie va dans le journal de la tâche, la progression en est extraite, et des boutons Arrêter et Forcer la fin sont ajoutés.

Paramètres dans le panneau
Tout se règle sur une seule page : le bouton ⚙ du panneau (ou Paramètres dans le menu de la zone de notification) ouvre l'apparence, les notifications et Telegram, les connecteurs, les agents IA, le démarrage automatique et la référence de l'API. Le menu de la zone de notification garde les actions rapides : ouvrir le panneau, mettre en sourdine, paramètres, soutenir, quitter.

Claude Code et Codex
Dans Paramètres → Agents IA, vous pouvez connecter TaskHub à Claude Code et à Codex. L'agent obtient les outils TaskHub (MCP) pour signaler ses tâches longues, et vous êtes notifié quand l'agent termine une réponse ou attend votre autorisation. TaskHub ajoute ses entrées aux fichiers de configuration des agents et les retire quand vous désactivez l'option.

Connecteurs (facultatifs, désactivés par défaut)
• GitHub Actions : les exécutions de workflows des dépôts que vous indiquez, sous forme de tâches : progression par jobs et étapes, l'étape en échec et la fin de son journal, boutons pour annuler et relancer.
• Runpod et Vast.ai : vos machines GPU sous forme de tâches : GPU, prix par heure, dépense à ce jour, durée de fonctionnement, SSH, graphiques de charge, alerte « GPU inactif », solde et autonomie avec alerte de solde bas, arrêt / démarrage / redémarrage (suppression uniquement dans le panneau, après avoir saisi le nom de la machine).
Chaque connecteur ne fonctionne qu'une fois activé et après saisie de votre propre clé API. La clé est conservée uniquement dans le Gestionnaire d'identification Windows, et le connecteur ne communique qu'avec l'API de son propre service (api.github.com, api.runpod.io, console.vast.ai).

Protection du solde (facultative, désactivée tant que vous ne l'activez pas)
Pour Runpod et Vast.ai : une alerte à votre propre seuil de solde et, au seuil d'arrêt, les machines en marche sont arrêtées après une notification avec Arrêter maintenant et Reporter. Elle ne fait qu'arrêter les machines, sans jamais les supprimer ; si le solde est illisible, elle n'arrête rien.

Telegram (facultatif)
Connectez votre propre bot Telegram pour recevoir sur votre téléphone les mêmes notifications et un message de progression en direct par tâche, et appuyer de là sur les boutons des tâches. Le jeton du bot est conservé dans le Gestionnaire d'identification Windows, et les messages partent de votre PC directement vers la Bot API de Telegram.

Confidentialité
Pas de compte, pas de télémétrie. L'API n'écoute que sur 127.0.0.1, et vos tâches et réglages restent dans %APPDATA%\TaskHub. Rien ne quitte votre PC tant que vous n'activez pas Telegram ou un connecteur.

Bon à savoir
• Les mises à jour arrivent par le Microsoft Store.
• Le démarrage automatique se désactive dans Paramètres → Système ou dans Paramètres Windows → Applications → Démarrage.
• La référence de l'API s'ouvre depuis la page des paramètres.
• Nécessite Windows 10 version 2004 ou ultérieure (x64) et Microsoft Edge WebView2 Runtime, déjà inclus dans Windows actuel.
• Langues de l'interface : anglais, ukrainien, espagnol, allemand, français, italien, portugais, polonais, chinois (simplifié), japonais.

## What's new in this version

TaskHub 2.9
• Page des paramètres dans le panneau (⚙) : tout au même endroit ; le menu de la zone de notification garde les actions rapides, dans le style de Windows 11.
• Nouveaux connecteurs, désactivés par défaut : GitHub Actions (exécutions de workflows avec annulation et relance), Runpod et Vast.ai (machines GPU, dépense, solde, alerte GPU inactif, arrêt et démarrage). Vos clés API sont conservées uniquement dans le Gestionnaire d'identification Windows.
• Protection du solde pour Runpod et Vast.ai, désactivée tant que vous ne l'activez pas : une alerte à votre seuil et, au seuil d'arrêt, les machines en marche sont arrêtées, jamais supprimées.
• Telegram : le jeton du bot passe dans le Gestionnaire d'identification Windows ; TaskHub respecte le rythme de messages de Telegram au lieu de perdre des messages.
• Réglage de transparence des cartes ; les liens vers des fichiers et dossiers s'affichent dans l'Explorateur, sans jamais être exécutés.
• 2.9.1 : montants avec deux décimales, coût de stockage des network volumes Runpod.
• Nécessite désormais Windows 10 version 2004 ou ultérieure.

## Product features

- Icône dans la zone de notification avec anneau de progression et nombre de tâches actives
- Panneau avec progression, temps restant, journal, graphiques, jauges et tableaux pour chaque tâche
- Boutons et champs déclarés par la tâche elle-même : pause, arrêt, changement de paramètre, fin forcée
- Notifications Windows en fin de tâche, en cas d'échec ou de blocage, avec les boutons de la tâche
- API HTTP locale, commande taskhub et client Python ; « taskhub run » encapsule n'importe quelle commande
- Tous les paramètres sur une page du panneau (⚙) ; actions rapides dans le menu de la zone de notification
- Claude Code et Codex : outils TaskHub (MCP) et notifications, activés dans les paramètres
- Connecteur GitHub Actions facultatif : exécutions de workflows en tâches, avec annulation et relance
- Connecteurs Runpod et Vast.ai facultatifs : machines GPU, dépense, solde, alerte GPU inactif, arrêt et démarrage
- Protection du solde facultative : alerte en cas de solde bas et arrête (sans jamais supprimer) les machines en marche
- Bot Telegram facultatif : notifications, progression et boutons de la tâche sur votre téléphone
- Pas de compte, pas de télémétrie ; clés API uniquement dans le Gestionnaire d'identification Windows ; l'API n'écoute que sur 127.0.0.1
- Interface en 10 langues

## Short description

Suivez et pilotez les tâches longues de votre PC : progression, graphiques, notifications et boutons propres à chaque tâche. Pour tout script via une API locale ou la commande taskhub, pour Claude Code et Codex, et en option pour GitHub Actions, Runpod et Vast.ai.
