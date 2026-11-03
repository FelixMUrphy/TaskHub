# SSH: configurar un host para TaskHub

[English](ssh.md) · [Українська](ssh.uk.md) · **Español** · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Todas las páginas](README.md)

## Qué hace TaskHub con esto
- TaskHub ejecuta el `ssh.exe` propio de Windows y lee cada host que indiques: carga, memoria, discos, GPU, sesiones de tmux y un proceso que nombres.
- **TaskHub no guarda ninguna clave ni contraseña.** Usa tu `~/.ssh/config`, tu agente SSH y tu `known_hosts`, y solo los lee.
- No instala nada en el host ni cambia nada allí. Por ahora funciona con hosts Linux.

## Antes de añadir un host
TaskHub no puede responder preguntas, así que tres cosas tienen que funcionar sin ninguna.

1. **El cliente OpenSSH.** TaskHub usa el de Windows, no una copia de Git. En un terminal, `C:\Windows\System32\OpenSSH\ssh.exe -V` debe mostrar una versión. Si no lo hace: Configuración de Windows → Sistema → Características opcionales (en Windows 10: Aplicaciones → Características opcionales) → añade *Cliente OpenSSH* ([pasos de Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Una clave sin escribir nada.** Una clave con frase de contraseña tiene que estar en el agente. En PowerShell como administrador, una sola vez:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Luego, con tu usuario: `ssh-add`. ([Guía de claves de Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Los agentes de 1Password o Pageant funcionan si atienden al OpenSSH de Windows.
3. **La clave del host es de confianza.** Conéctate una vez desde un terminal: `ssh my-server`. Compara la huella que muestra con la que te dé el propietario del host y responde `yes`. TaskHub nunca confía por sí mismo en una clave nueva.

La comprobación: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` muestra `Linux` sin hacer preguntas.

El nombre de un host va en `~/.ssh/config`, junto con el usuario, el puerto y la clave:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Añádelo
Configuración → *Conectores* → *SSH* → actívalo, escribe `my-server` y pulsa *Añadir*. Solo letras, dígitos, puntos, guiones y guiones bajos: `user@host` va en el config.

## Qué ves
Una tarea por cada host, con su carga y sus discos. Recibes una notificación cuando se llena un disco, cuando se pierde la conexión o TaskHub tiene que dejar de intentarlo, cuando el host se reinicia y cuando termina un trabajo:
- **una sesión de tmux**, o un comando dentro de una que vuelve a su shell (cerrar un editor también cuenta; un interruptor en Configuración desactiva estos avisos, y quedan en el registro de la tarea);
- **un proceso que nombres:** *Vigilar un proceso* en la tarea del host, con su número (por ejemplo `pgrep -f train`).

TaskHub no puede saber cómo terminó un trabajo, así que dice "ha terminado", no "se completó con éxito". Mientras tu PC está apagado nadie vigila. Un proceso que nombraste y que terminó entretanto se notifica en la siguiente conexión. Los cambios de tmux y un reinicio del host solo se detectan a través de una conexión perdida, mientras TaskHub sigue en marcha: no después de reiniciar TaskHub o tu PC.

## Qué debes saber
- **Tu config se ejecuta.** Un `ProxyCommand` o `Match exec` en `~/.ssh/config` se ejecuta en cada conexión que hace TaskHub, igual que lo haría para ti en un terminal. Los hosts detrás de `ProxyJump` usan los ajustes de tu config para el host de salto.
- **Qué queda abierto.** Un `ssh` por host mientras el conector está activo. Envía cada minuto un script fijo de solo lectura por defecto (como mínimo cada 30 segundos).
- **Una clave cambiada lo detiene.** Si cambia la clave de un host, TaskHub no se conecta y te lo dice. Puede ser un host reinstalado o alguien en medio: compruébalo y arregla `known_hosts` tú mismo.
- **Inicios de sesión fallidos.** Tras una clave rechazada, TaskHub deja de intentarlo hasta que pulses *Comprobar de nuevo*, para que no pueda hacer que bloqueen tu dirección.
- **Qué sale de tu PC.** Solo hacia tus hosts: tu nombre de usuario y tus claves públicas (como hace cualquier ssh), y el script. Lo que vuelve se queda en la carpeta de datos de TaskHub y aparece en las notificaciones, incluidas las de Telegram o Discord si están activadas. Consulta [PRIVACY.md](../../PRIVACY.md).

## Detenerlo
Desactiva el conector o quita el host: la conexión termina. No queda nada en el host, y no hay ninguna clave en TaskHub que borrar.
