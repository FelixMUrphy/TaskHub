# Discord: token de bot para TaskHub

[English](discord.md) · [Українська](discord.uk.md) · **Español** · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Todas las páginas](README.md)

## Para qué lo usa TaskHub
- Envía tus notificaciones de TaskHub, el progreso de las tareas y los gráficos como mensajes directos desde **tu propio** bot.
- Recibe las pulsaciones de tus botones y los comandos `/tasks`, `/mute`, `/unmute`, a través de una conexión que tu PC abre hacia Discord. No hace falta ninguna dirección pública.
- No lee tus mensajes: no pide intents privilegiados y nunca el contenido de los mensajes.

## Crea el bot
1. Abre el Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Haz clic en **Create App**, escribe un nombre (por ejemplo `TaskHub`) y haz clic en **Create**.
3. En la página **Bot**, en **Token**, haz clic en **Reset Token** y copia el token. Discord lo muestra una sola vez.
4. Mantén el bot como solo tuyo: en la página **Installation** pon **Install Link** en **None** y luego, en la página **Bot**, desactiva **Public Bot**. Con esa opción desactivada, solo tú puedes añadir el bot a servidores. (Si el Portal lo rechaza, haz primero el paso de **Installation**.)
5. Deja el resto como está: sin intents privilegiados y con **Interactions Endpoint URL** vacío. Si hay una URL ahí, Discord le envía las pulsaciones de los botones en lugar de a TaskHub, y TaskHub te avisa.

## Pégalo en TaskHub
1. TaskHub → **Configuración** → **Mensajería** → **Discord**. Pega el token y haz clic en **Conectar**. Un token que Discord rechaza no se guarda.
2. Añade el bot a un servidor (siguiente sección). Discord solo deja que un bot escriba a alguien con quien comparte un servidor, así que hasta entonces TaskHub dice que el bot aún no puede escribirte.
3. Haz clic en **Comprobar de nuevo**. El bot te envía un mensaje directo con un botón **Conectar**. Púlsalo.

Solo se puede vincular al propietario de la aplicación (en una aplicación de un equipo, al propietario del equipo). Solo las pulsaciones de esa persona, en ese chat, hacen algo.

## Añade el bot a un servidor
1. En TaskHub, haz clic en **Añadir el bot a un servidor**. La página de Discord se abre en tu navegador.
2. En la lista de servidores, elige uno donde puedas añadir bots: que sea tuyo o que tengas **Manage Server** en él. El bot no recibe ningún permiso en él y no lo lee; solo necesita compartir un servidor contigo.
3. Confirma en esa página, vuelve a TaskHub y haz clic en **Comprobar de nuevo**.

**¿No tienes un servidor propio?** Crear uno es gratis: en la aplicación de Discord, haz clic en **Add a Server** en la parte inferior de la barra lateral izquierda, elige **Create My Own**, luego **For me and my friends**, escribe un nombre y haz clic en **Create**. Después repite los pasos anteriores.

**¿Sigue sin llegar el mensaje?** Puede que tu configuración de privacidad lo bloquee: haz clic en el nombre del servidor, abre **Privacy Settings**, activa **Allow direct messages from server members** y haz clic en **Comprobar de nuevo**.

## Dónde se guarda el token
- Solo en el Administrador de credenciales de Windows, nunca en `config.json`. Si el Administrador de credenciales no está disponible, TaskHub lo dice y no se conecta.
- Se envía únicamente a `discord.com` y al gateway de Discord (`*.discord.gg`).
- Tu id de usuario de Discord, el id del chat y el id de la aplicación se guardan en `config.json`.

## Revócalo
- **En Discord:** Developer Portal → tu aplicación → **Bot** → **Reset Token**. El token anterior deja de funcionar.
- **En TaskHub:** Configuración → Mensajería → Discord → **Desconectar** (lo elimina del Administrador de credenciales). El bot sigue en tu cuenta de Discord hasta que elimines la aplicación. Desinstalar TaskHub con **Eliminar también mis datos** también elimina el token; una desinstalación normal lo conserva.

## Si el token deja de funcionar
Discord restablece el token de un bot tras 1000 conexiones nuevas en un día y avisa por correo al propietario. TaskHub se mantiene muy por debajo de ese límite y espera cuando se acerca. Si ocurre de todos modos, la página dice que el token fue rechazado: crea uno nuevo y vuelve a conectar.

## Documentación oficial
- Primeros pasos: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots y mensajes directos, servidores en común: <https://docs.discord.com/developers/interactions/application-commands>
- Crear un servidor: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Mensajes directos de los miembros del servidor: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Quién puede añadir un bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL frente al gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Límites de conexión: <https://docs.discord.com/developers/events/gateway>
- Política de privacidad: <https://discord.com/privacy>
