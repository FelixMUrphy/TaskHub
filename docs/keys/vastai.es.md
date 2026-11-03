# Vast.ai: clave de API para TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · **Español** · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Todas las páginas](README.md)

## Para qué la usa TaskHub
- **Vigilar:** tus instancias, su carga, el precio, los cargos de los últimos 30 días, tu saldo y tu crédito.
- **Acciones** (solo si la clave lo permite): Detener, Iniciar, Reiniciar, desde el panel, una notificación o Telegram.
- **Eliminar (destroy)** nunca se hace automáticamente. Solo funciona en el panel de TaskHub, después de escribir el nombre.

## Permisos mínimos
| Quieres | Permisos |
|---|---|
| (a) Solo vigilar | `instance_read`, `user_read`, `billing_read` |
| (b) Detener / Iniciar / Reiniciar | los anteriores + `instance_write` |

Ojo: el permiso `instance_write` de Vast.ai abarca "creating, managing, and destroying instances and volumes"; no
existe un permiso para detener / iniciar sin poder destruir. TaskHub nunca destruye por sí mismo, solo desde el panel después de que
escribas el nombre.

## Crea la clave
**En la consola:** abre la sección **Keys** de la consola de Vast.ai → **API Keys** → **+New**. En la ventana emergente
ponle un nombre (por ejemplo `TaskHub`) y elige los permisos: por defecto están marcados todos los permisos de tu cuenta, así que deja solo los
de la tabla. Créala y copia la clave.

**Con la CLI:** las páginas de permisos de Vast.ai describen las claves restringidas mediante la CLI / API; úsala si la
consola no te deja limitarlos. Guarda esto como `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Añade `"instance_write": {}` para las acciones y ejecuta:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## Pégala en TaskHub
1. TaskHub → **Configuración** → **Conectores** → **Vast.ai**.
2. Actívalo, pega la clave y haz clic en **Guardar**.
3. La línea debe decir **Conectado, saldo …** (o **Conectado**). Si Vast.ai rechaza la clave, no se guarda nada.

TaskHub comprueba una vez (y luego a diario) si la clave puede actuar y muestra los botones de acción solo si puede.

## Dónde se guarda la clave
- Solo en el Administrador de credenciales de Windows, nunca en `config.json`.
- Se envía únicamente a `console.vast.ai`.

## Revócala
- **En Vast.ai:** consola → **Keys** → **Delete** junto a la clave (**Reset** la sustituye por una nueva).
- **En TaskHub:** Configuración → Conectores → Vast.ai → **Quitar** (la elimina del Administrador de credenciales).

## Documentación oficial
- Claves de API: <https://docs.vast.ai/guides/reference/keys>
- Permisos: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Facturación y saldo: <https://docs.vast.ai/guides/reference/billing>
