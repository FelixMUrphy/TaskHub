# fr-FR

## Description

TaskHub rassemble au même endroit les tâches longues de votre PC et vous permet de les piloter : exports vidéo, rendus, compilations, sauvegardes, entraînement de modèles et scripts que les agents de programmation IA lancent pour vous.

Comment ça marche
Un programme ou un script signale sa progression à TaskHub via une API HTTP locale sur 127.0.0.1, la commande taskhub ou le client Python fourni. TaskHub affiche alors :
• une icône dans la zone de notification avec un anneau de progression et le nombre de tâches actives ;
• un panneau avec la progression, le temps restant, l'état et le journal de chaque tâche, ainsi que les widgets qu'elle demande : graphiques en courbes, en aires et en barres, jauges, valeurs, voyants, tableaux ;
• les commandes propres à la tâche : boutons, champs numériques, texte ou liste, pause, arrêt et fin forcée. Votre choix est renvoyé à la tâche ;
• des notifications Windows quand une tâche se termine, échoue ou ne donne plus de nouvelles, avec les boutons de la tâche si elle les demande.

« taskhub run -- <commande> » encapsule n'importe quelle ligne de commande : la sortie va dans le journal de la tâche, la progression en est extraite, et des boutons Arrêter et Forcer la fin sont ajoutés.

Claude Code et Codex
Depuis le menu de la zone de notification (Agents IA), vous pouvez connecter TaskHub à Claude Code et à Codex. L'agent obtient les outils TaskHub (MCP) pour signaler ses tâches longues, et vous êtes notifié quand l'agent termine une réponse ou attend votre autorisation. TaskHub ajoute ses entrées aux fichiers de configuration des agents et les retire quand vous désactivez l'option.

Telegram (facultatif)
Connectez votre propre bot Telegram pour recevoir les mêmes notifications sur votre téléphone et appuyer de là sur les boutons des tâches. Les messages partent de votre PC directement vers la Bot API de Telegram.

Confidentialité
Pas de compte, pas de service cloud, pas de télémétrie. L'API n'écoute que sur 127.0.0.1, et vos tâches et réglages restent dans %APPDATA%\TaskHub.

Bon à savoir
• Les mises à jour arrivent par le Microsoft Store.
• Le démarrage automatique se désactive dans le menu de la zone de notification ou dans Paramètres → Applications → Démarrage.
• La référence de l'API s'ouvre depuis le menu de la zone de notification (Référence API).
• Nécessite Windows 10 version 1809 ou ultérieure (x64) et Microsoft Edge WebView2 Runtime, déjà inclus dans Windows actuel.
• Langues de l'interface : anglais, ukrainien, espagnol, allemand, français, italien, portugais, polonais, chinois (simplifié), japonais.

## Product features

- Icône dans la zone de notification avec anneau de progression et nombre de tâches actives
- Panneau avec progression, temps restant, journal, graphiques, jauges et tableaux pour chaque tâche
- Boutons et champs déclarés par la tâche elle-même : pause, arrêt, changement de paramètre, fin forcée
- Notifications Windows en fin de tâche, en cas d'échec ou de blocage, avec les boutons de la tâche
- API HTTP locale, commande taskhub et client Python ; « taskhub run » encapsule n'importe quelle commande
- Claude Code et Codex : outils TaskHub (MCP) et notifications, activés depuis le menu
- Bot Telegram facultatif : notifications, progression et boutons de la tâche sur votre téléphone
- Pas de compte, pas de cloud, pas de télémétrie ; l'API n'écoute que sur 127.0.0.1
- Interface en 10 langues

## Short description

Suivez et pilotez les tâches longues de votre PC : progression, graphiques, notifications et boutons propres à chaque tâche. Fonctionne avec n'importe quel script via une API locale ou la commande taskhub, ainsi qu'avec Claude Code et Codex.
