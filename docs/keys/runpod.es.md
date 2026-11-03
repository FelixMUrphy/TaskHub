# Runpod: clave de API para TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · **Español** · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Todas las páginas](README.md)

## Para qué la usa TaskHub
- **Vigilar:** tus pods, su carga, el precio, lo gastado hasta ahora (facturación de Runpod), tu saldo y el tiempo que te queda.
- **Acciones** (solo si la clave lo permite): Detener, Iniciar, Reiniciar, desde el panel, una notificación o Telegram.
- **Eliminar** nunca se hace automáticamente. Solo funciona en el panel de TaskHub, después de escribir el nombre del pod.

## Crea la clave
1. Abre la consola de Runpod: <https://console.runpod.io/user/credentials>.
2. En **API Keys** haz clic en **Create API Key**.
3. Escribe un nombre (por ejemplo `TaskHub`) y elige el tipo (ver abajo).
4. Haz clic en **Create** y copia la clave enseguida: se muestra una sola vez; Runpod no la guarda.

## Qué tipo elegir
| Quieres | Tipo | Notas |
|---|---|---|
| (a) Solo vigilar | **Read Only** | Recomendado si no necesitas los botones. Runpod no documenta si una clave Read Only puede leer la facturación y el saldo; si TaskHub no muestra el saldo, esa es la razón. |
| (b) Detener / Iniciar / Reiniciar | **All** | Runpod documenta los ámbitos **Restricted** solo para los endpoints Serverless, no para los pods; por eso, para las acciones sobre pods usa **All**. |

TaskHub comprueba una vez (y luego a diario) si la clave puede actuar y muestra los botones de acción solo si puede.
Una clave de tipo **All** también podría eliminar pods: TaskHub nunca lo hace por sí mismo, solo desde el panel después de
que escribas el nombre.

## Pégala en TaskHub
1. TaskHub → **Configuración** → **Conectores** → **Runpod**.
2. Actívalo, pega la clave y haz clic en **Guardar**.
3. La línea debe decir **Conectado, saldo …** (o **Conectado**). Si Runpod rechaza la clave, no se guarda nada.

## Dónde se guarda la clave
- Solo en el Administrador de credenciales de Windows, nunca en `config.json`.
- Se envía únicamente a `api.runpod.io` (REST para los pods, los tamaños de los network volumes y la facturación; GraphQL para el saldo).

## Revócala
- **En Runpod:** <https://console.runpod.io/user/credentials> → el icono de la papelera junto a la clave → **Revoke Key**
  (o desactiva la clave con su interruptor).
- **En TaskHub:** Configuración → Conectores → Runpod → **Quitar** (la elimina del Administrador de credenciales).
  Desinstalar TaskHub con **Eliminar también mis datos** también la elimina; una desinstalación normal la conserva.

## Documentación oficial
- Claves de API: <https://docs.runpod.io/get-started/credentials>
- Límites de uso: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Precios y qué pasa al llegar a $0: <https://docs.runpod.io/pods/pricing>
