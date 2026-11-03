# SSH: setting up a host for TaskHub

[Українською](ssh.uk.md) · [All keys](README.md)

## What TaskHub does with it
- TaskHub runs Windows' own `ssh.exe` and reads each host you list: load, memory, disks, GPU, tmux sessions, and a process you name.
- **TaskHub keeps no key and no password.** It uses your `~/.ssh/config`, your SSH agent and your `known_hosts`, and it only reads them.
- It installs nothing on the host and changes nothing there. For now it works with Linux hosts.

## Before you add a host
TaskHub can't answer questions, so three things have to work without any.

1. **The OpenSSH client.** TaskHub uses the one in Windows, not a copy from Git. In a terminal, `C:\Windows\System32\OpenSSH\ssh.exe -V` should print a version. If it doesn't: Windows Settings → System → Optional features (on Windows 10: Apps → Optional features) → add *OpenSSH Client* ([Microsoft's steps](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **A key without typing.** A key with a passphrase has to be in the agent. In PowerShell as administrator, once:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Then, as yourself: `ssh-add`. ([Microsoft's guide to keys](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Agents from 1Password or Pageant work if they serve Windows' OpenSSH.
3. **The host's key is trusted.** Connect once from a terminal: `ssh my-server`. Compare the fingerprint it shows with the one from the host's owner and answer `yes`. TaskHub never trusts a new key by itself.

The check: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` prints `Linux` with no questions.

A host's name goes in `~/.ssh/config`, along with the user, port and key:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Add it
Settings → *Connectors* → *SSH* → turn it on, type `my-server` and press *Add*. Letters, digits, dots, dashes and underscores only: `user@host` goes in the config.

## What you see
A task for each host, with its load and disks. You get a notification when a disk fills up, when the connection is lost or TaskHub has to stop trying, when the host restarts, and when a job ends:
- **a tmux session**, or a command in one going back to its shell (closing an editor counts too; a switch in Settings turns these off, and they stay in the task's log);
- **a process you name:** *Watch a process* on the host's task, with its number (`pgrep -f train`, for example).

TaskHub can't tell how a job ended, so it says "ended", not "succeeded". While your PC is off nobody is watching. A process you named that ended meanwhile is reported at the next connection. tmux changes and a restart of the host are noticed only across a lost connection, while TaskHub keeps running: not after TaskHub or your PC restarts.

## What to know
- **Your config runs.** A `ProxyCommand` or `Match exec` in `~/.ssh/config` runs on every connection TaskHub makes, as it would for you in a terminal. Hosts behind `ProxyJump` use your config's settings for the jump host.
- **What stays open.** One `ssh` per host while the connector is on. It sends a fixed read-only script every minute by default (30 seconds at the least).
- **A changed key stops it.** If a host's key changes, TaskHub won't connect and tells you. It may be a reinstalled host or someone in between: check, then fix `known_hosts` yourself.
- **Failed logins.** After a refused key TaskHub stops trying until you press *Check again*, so it can't get your address banned.
- **What leaves your PC.** To your hosts only: your user name and public keys (as any ssh does), and the script. What comes back stays in TaskHub's data folder and shows in notifications, including Telegram or Discord when on. See [PRIVACY.md](../../PRIVACY.md).

## Stop
Turn the connector off or remove the host: the connection ends. Nothing stays on the host, and there is no key in TaskHub to delete.
