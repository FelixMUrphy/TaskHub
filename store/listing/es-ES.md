# es-ES

## Description

TaskHub reúne en un solo lugar los trabajos largos de tu PC y te permite controlarlos: exportaciones de vídeo, renders, compilaciones, copias de seguridad, entrenamiento de modelos, ejecuciones de CI, máquinas GPU en la nube y los scripts que lanzan por ti los agentes de IA de programación.

Cómo funciona
Un programa o script informa de su progreso a TaskHub mediante una API HTTP local en 127.0.0.1, el comando taskhub o el cliente de Python incluido. TaskHub muestra:
• un icono en la bandeja con un anillo de progreso y el número de tareas activas;
• un panel con el progreso, el tiempo restante, el estado y el registro de cada tarea, además de los widgets que pida: gráficos de líneas, de área y de barras, indicadores, cifras, tablas;
• los controles propios de la tarea: botones, campos de número, texto o lista, pausa, detener y finalizar. Lo que elijas vuelve a la tarea;
• notificaciones de Windows cuando una tarea termina, falla o deja de informar, con los botones de la tarea si los pide.

«taskhub run -- <comando>» envuelve cualquier línea de comandos: pasa la salida al registro de la tarea, lee de ella el progreso y añade los botones Detener y Finalizar.

Configuración en el panel
Todo se configura en una sola página: el botón ⚙ del panel (o Configuración en el menú de la bandeja) abre la apariencia, las notificaciones y Telegram, los conectores, los agentes de IA, el inicio automático y la referencia de la API. El menú de la bandeja conserva las acciones rápidas: abrir el panel, silenciar, configuración, apoyar, salir.

Claude Code y Codex
En Configuración → Agentes de IA puedes conectar TaskHub con Claude Code y Codex. El agente recibe las herramientas de TaskHub (MCP) para informar de sus trabajos largos, y tú recibes una notificación cuando el agente termina una respuesta o necesita tu permiso. TaskHub añade sus entradas a los archivos de configuración de los agentes y las quita cuando desactivas la opción.

Conectores (opcionales, desactivados por defecto)
• GitHub Actions: las ejecuciones de workflows de los repositorios que indiques, como tareas: progreso por jobs y pasos, el paso que falló y el final de su registro, botones para cancelar y volver a ejecutar.
• Runpod y Vast.ai: tus máquinas GPU como tareas: GPU, precio por hora, gasto acumulado, tiempo encendida, SSH, gráficos de carga, aviso de «GPU inactiva», saldo y autonomía con aviso de saldo bajo, detener / iniciar / reiniciar (eliminar solo en el panel, tras escribir el nombre de la máquina).
Cada conector funciona solo cuando lo activas e introduces tu propia clave de API. La clave se guarda solo en el Administrador de credenciales de Windows, y el conector habla solo con la API de su propio servicio (api.github.com, api.runpod.io, console.vast.ai).

Protección de saldo (opcional, desactivada hasta que la actives)
Para Runpod y Vast.ai: un aviso en tu propio umbral de saldo y, en el umbral de parada, las máquinas en marcha se detienen tras una notificación con Detener ahora y Posponer. Solo detiene máquinas y nunca las elimina; si no puede leer el saldo, no detiene nada.

Telegram (opcional)
Conecta tu propio bot de Telegram para recibir en el móvil las mismas notificaciones y un mensaje de progreso en vivo por tarea, y pulsar desde allí los botones de las tareas. El token del bot se guarda en el Administrador de credenciales de Windows, y los mensajes van de tu PC directamente a la Bot API de Telegram.

Privacidad
Sin cuenta, sin telemetría. La API solo escucha en 127.0.0.1, y tus tareas y ajustes se guardan en %APPDATA%\TaskHub. Nada sale de tu PC salvo que actives Telegram o un conector.

A tener en cuenta
• Las actualizaciones llegan desde Microsoft Store.
• El inicio automático se puede desactivar en Configuración → Sistema o en Configuración de Windows → Aplicaciones → Inicio.
• La referencia de la API se abre desde la página de configuración.
• Requiere Windows 10 versión 2004 o posterior (x64) y Microsoft Edge WebView2 Runtime, que ya incluye Windows actual.
• Idiomas de la interfaz: inglés, ucraniano, español, alemán, francés, italiano, portugués, polaco, chino (simplificado) y japonés.

## What's new in this version

TaskHub 2.9
• Página de configuración en el panel (⚙): todo en un solo lugar; el menú de la bandeja conserva las acciones rápidas, al estilo de Windows 11.
• Nuevos conectores, desactivados por defecto: GitHub Actions (ejecuciones de workflows con cancelar y volver a ejecutar), Runpod y Vast.ai (máquinas GPU, gasto, saldo, aviso de GPU inactiva, detener e iniciar). Tus claves de API se guardan solo en el Administrador de credenciales de Windows.
• Protección de saldo para Runpod y Vast.ai, desactivada hasta que la actives: un aviso en tu umbral y, en el umbral de parada, las máquinas en marcha se detienen, nunca se eliminan.
• Telegram: el token del bot pasa al Administrador de credenciales de Windows; TaskHub respeta el ritmo de mensajes de Telegram en lugar de perder mensajes.
• Ajuste de transparencia de las tarjetas; los enlaces a archivos y carpetas se muestran en el Explorador, nunca se ejecutan.
• 2.9.1: importes con dos decimales, coste de almacenamiento de los network volumes de Runpod.
• Ahora requiere Windows 10 versión 2004 o posterior.

## Product features

- Icono en la bandeja con anillo de progreso y número de tareas activas
- Panel con progreso, tiempo restante, registro, gráficos, indicadores y tablas para cada tarea
- Botones y campos que declara la propia tarea: pausa, detener, cambiar un parámetro, finalizar
- Notificaciones de Windows al terminar, fallar o quedarse parada una tarea, con sus botones
- API HTTP local, comando taskhub y cliente de Python; «taskhub run» envuelve cualquier comando
- Toda la configuración en una página del panel (⚙); acciones rápidas en el menú de la bandeja
- Claude Code y Codex: herramientas de TaskHub (MCP) y notificaciones, activables en Configuración
- Conector opcional de GitHub Actions: ejecuciones de workflows como tareas, con cancelar y volver a ejecutar
- Conectores opcionales de Runpod y Vast.ai: máquinas GPU, gasto, saldo, aviso de GPU inactiva, detener e iniciar
- Protección de saldo opcional: avisa con saldo bajo y detiene (nunca elimina) las máquinas en marcha
- Bot de Telegram opcional: notificaciones, progreso y botones de la tarea en el móvil
- Sin cuenta, sin telemetría; claves de API solo en el Administrador de credenciales de Windows; la API solo escucha en 127.0.0.1
- 10 idiomas de interfaz

## Short description

Sigue y controla los trabajos largos de tu PC: progreso, gráficos, notificaciones y los botones propios de cada tarea. Funciona con cualquier script mediante una API local o el comando taskhub, con Claude Code y Codex y, si quieres, con GitHub Actions, Runpod y Vast.ai.
