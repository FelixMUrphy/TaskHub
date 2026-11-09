# SSH : configurer un hôte pour TaskHub

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · **Français** · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Toutes les pages](README.md)

## Ce que TaskHub en fait
- TaskHub lance le `ssh.exe` propre à Windows et interroge chaque hôte que vous indiquez : charge, mémoire, disques, GPU, sessions tmux et un processus que vous nommez.
- **TaskHub ne garde ni clé ni mot de passe.** Il utilise votre `~/.ssh/config`, votre agent SSH et votre `known_hosts`, qu'il ne fait que lire.
- Il n'installe rien sur l'hôte et n'y change rien. Pour l'instant, il fonctionne avec des hôtes Linux.

## Avant d'ajouter un hôte
TaskHub ne sait pas répondre à des questions : trois choses doivent donc fonctionner sans en poser.

1. **Le client OpenSSH.** TaskHub utilise celui de Windows, pas une copie fournie par Git. Dans un terminal, `C:\Windows\System32\OpenSSH\ssh.exe -V` doit afficher une version. Sinon : Paramètres Windows → Système → Fonctionnalités facultatives (sous Windows 10 : Applications → Fonctionnalités facultatives) → ajoutez *Client OpenSSH* ([la procédure de Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Une clé sans saisie.** Une clé protégée par une phrase secrète doit se trouver dans l'agent. Dans PowerShell en tant qu'administrateur, une seule fois :
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Puis, sous votre propre compte : `ssh-add`. ([Le guide des clés de Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Les agents de 1Password ou de Pageant conviennent s'ils servent l'OpenSSH de Windows.
3. **La clé de l'hôte est approuvée.** Connectez-vous une fois depuis un terminal : `ssh my-server`. Comparez l'empreinte affichée avec celle que vous donne le propriétaire de l'hôte et répondez `yes`. TaskHub n'approuve jamais une nouvelle clé de lui-même.

La vérification : `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` affiche `Linux` sans poser de question.

Le nom d'un hôte se place dans `~/.ssh/config`, avec l'utilisateur, le port et la clé :
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## L'ajouter
Paramètres → *Connecteurs* → *SSH* → activez-le, saisissez `my-server` et appuyez sur *Ajouter*. Uniquement des lettres, chiffres, points, tirets et tirets bas : `user@host` se met dans la config.

## Ce que vous voyez
Une tâche par hôte, avec sa charge et ses disques. Vous recevez une notification quand un disque est plein, quand la connexion est perdue ou que TaskHub doit cesser d'essayer, quand l'hôte redémarre, et quand un travail se termine :
- **une session tmux**, ou une commande qu'elle contient qui rend la main au shell (fermer un éditeur compte aussi ; un interrupteur dans les Paramètres désactive ces notifications, et elles restent dans le journal de la tâche) ;
- **un processus que vous nommez :** *Surveiller un processus* sur la tâche de l'hôte, avec son numéro (`pgrep -f train`, par exemple).

TaskHub ne peut pas savoir comment un travail s'est terminé : il indique « terminé », pas « réussi ». Quand votre PC est éteint, personne ne surveille. Un processus nommé qui s'est terminé entre-temps est signalé à la connexion suivante. Les changements de tmux et un redémarrage de l'hôte ne sont détectés qu'à travers une connexion perdue, tant que TaskHub reste lancé : pas après un redémarrage de TaskHub ou de votre PC.

## À savoir
- **Votre config s'exécute.** Un `ProxyCommand` ou `Match exec` dans `~/.ssh/config` s'exécute à chaque connexion de TaskHub, comme pour vous dans un terminal. Les hôtes derrière un `ProxyJump` utilisent les réglages de votre config pour l'hôte de rebond.
- **Ce qui reste ouvert.** Un `ssh` par hôte tant que le connecteur est activé. Il envoie chaque minute un script fixe en lecture seule (30 secondes au minimum).
- **Une clé modifiée l'arrête.** Si la clé d'un hôte change, TaskHub ne se connecte pas et vous le dit. Il peut s'agir d'un hôte réinstallé ou de quelqu'un entre les deux : vérifiez, puis corrigez `known_hosts` vous-même.
- **Échecs de connexion.** Après le refus d'une clé, TaskHub cesse d'essayer jusqu'à ce que vous appuyiez sur *Vérifier à nouveau*, pour ne pas faire bannir votre adresse.
- **Ce qui quitte votre PC.** Uniquement vers vos hôtes : votre nom d'utilisateur et vos clés publiques (comme tout client ssh), et le script. Ce qui revient reste dans le dossier de données de TaskHub et apparaît dans les notifications, y compris Telegram ou Discord lorsqu'ils sont activés. Voir [PRIVACY.md](../../PRIVACY.md).

## Arrêter
Désactivez le connecteur ou retirez l'hôte : la connexion se termine. Rien ne reste sur l'hôte, et il n'y a aucune clé dans TaskHub à supprimer.
