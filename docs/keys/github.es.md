# GitHub Actions: token para TaskHub

[English](github.md) · [Українська](github.uk.md) · **Español** · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Todas las páginas](README.md)

## Para qué lo usa TaskHub
- **Vigilar:** las ejecuciones de workflows de los repositorios que indiques: progreso, el paso que falló, el resultado.
- **Acciones** (solo con acceso de escritura): Cancelar y Relanzar una ejecución.
- **Pull requests** (opcional, solo lectura): los que esperan tu revisión y los pull requests abiertos de Dependabot.
- TaskHub nunca cambia tus workflows, tu código ni la configuración de tus repositorios.

## Crea un token fine-grained
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (enlace directo: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** por ejemplo `TaskHub`.
3. **Expiration:** 30 días por defecto; elige lo que te convenga (TaskHub te avisa una semana antes de que caduque).
4. **Resource owner:** tú, o la organización propietaria de los repositorios.
5. **Repository access:** **Only select repositories** → los repositorios que indicarás en TaskHub.
6. **Permissions** (ver abajo) → **Generate token** → cópialo.

Si el propietario es una organización que exige aprobación, el token queda pendiente hasta que un administrador lo apruebe.

## Permisos mínimos
| Quieres | Repository permissions |
|---|---|
| (a) Solo vigilar | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Cancelar / Relanzar | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requests (opcional): los pull requests de Dependabot necesitan además **Pull requests: Read-only** (GitHub solo muestra la lista
de PR abiertos con ese permiso); la búsqueda de solicitudes de revisión no necesita ningún permiso. Un token fine-grained ve las solicitudes
de revisión solo en los repositorios que se le han dado y en los públicos.

Prefiere este token a la otra opción de TaskHub (el token que ya tiene Git Credential Manager), que tiene
acceso a todos tus repositorios.

## Vigilar un repositorio más adelante
Con **Only select repositories**, el token solo llega a los repositorios que elegiste. Si TaskHub dice que un repositorio
*no se encuentra o que el token no puede acceder a él*: comprueba el nombre y luego abre el token en GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) y añade el repositorio en **Repository access**. GitHub responde igual ante
un nombre incorrecto, un repositorio que no se le dio al token y, en algunas solicitudes, un permiso que falta, así que TaskHub no puede decirte cuál de los casos es.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Pégalo en TaskHub
1. TaskHub → **Configuración** → **Conectores** → **GitHub Actions**.
2. Actívalo, elige **Mi propio token fine-grained (recomendado)**, pega el token, indica los repositorios
   (`owner/repo`) y haz clic en **Guardar**.
3. La línea debe decir **Conectado como** *tu usuario*. Si GitHub rechaza el token, no se guarda nada.

## Dónde se guarda el token
- Solo en el Administrador de credenciales de Windows, nunca en `config.json`.
- Se envía únicamente a `api.github.com`. El final del registro (opcional) se descarga mediante el enlace de un solo uso que da GitHub,
  sin el token.

## Revócalo
- **En GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → el token →
  **Delete**.
- **En TaskHub:** Configuración → Conectores → GitHub Actions → **Quitar** (lo elimina del Administrador de credenciales).

## Documentación oficial
- Crear un token fine-grained:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Buscar pull requests (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Permisos por endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
