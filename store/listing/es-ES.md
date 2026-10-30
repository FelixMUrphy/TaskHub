# es-ES

## Description

TaskHub reúne en un solo lugar los trabajos largos de tu PC y te permite controlarlos: exportaciones de vídeo, renders, compilaciones, copias de seguridad, entrenamiento de modelos y los scripts que lanzan por ti los agentes de IA de programación.

Cómo funciona
Un programa o script informa de su progreso a TaskHub mediante una API HTTP local en 127.0.0.1, el comando taskhub o el cliente de Python incluido. TaskHub muestra:
• un icono en la bandeja con un anillo de progreso y el número de tareas activas;
• un panel con el progreso, el tiempo restante, el estado y el registro de cada tarea, además de los widgets que pida: gráficos de líneas, de área y de barras, indicadores, cifras, tablas;
• los controles propios de la tarea: botones, campos de número, texto o lista, pausa, detener y finalizar. Lo que elijas vuelve a la tarea;
• notificaciones de Windows cuando una tarea termina, falla o deja de informar, con los botones de la tarea si los pide.

«taskhub run -- <comando>» envuelve cualquier línea de comandos: pasa la salida al registro de la tarea, lee de ella el progreso y añade los botones Detener y Finalizar.

Claude Code y Codex
Desde el menú de la bandeja (Agentes de IA) puedes conectar TaskHub con Claude Code y Codex. El agente recibe las herramientas de TaskHub (MCP) para informar de sus trabajos largos, y tú recibes una notificación cuando el agente termina una respuesta o necesita tu permiso. TaskHub añade sus entradas a los archivos de configuración de los agentes y las quita cuando desactivas la opción.

Telegram (opcional)
Conecta tu propio bot de Telegram para recibir las mismas notificaciones en el móvil y pulsar desde allí los botones de las tareas. Los mensajes van de tu PC directamente a la Bot API de Telegram.

Privacidad
Sin cuenta, sin servicio en la nube, sin telemetría. La API solo escucha en 127.0.0.1, y tus tareas y ajustes se guardan en %APPDATA%\TaskHub.

A tener en cuenta
• Las actualizaciones llegan desde Microsoft Store.
• El inicio automático se puede desactivar en el menú de la bandeja o en Configuración → Aplicaciones → Inicio.
• La referencia de la API se abre desde el menú de la bandeja (Referencia de la API).
• Requiere Windows 10 versión 1809 o posterior (x64) y Microsoft Edge WebView2 Runtime, que ya incluye Windows actual.
• Idiomas de la interfaz: inglés, ucraniano, español, alemán, francés, italiano, portugués, polaco, chino (simplificado) y japonés.

## Product features

- Icono en la bandeja con anillo de progreso y número de tareas activas
- Panel con progreso, tiempo restante, registro, gráficos, indicadores y tablas para cada tarea
- Botones y campos que declara la propia tarea: pausa, detener, cambiar un parámetro, finalizar
- Notificaciones de Windows al terminar, fallar o quedarse parada una tarea, con sus botones
- API HTTP local, comando taskhub y cliente de Python; «taskhub run» envuelve cualquier comando
- Claude Code y Codex: herramientas de TaskHub (MCP) y notificaciones, activables desde la bandeja
- Bot de Telegram opcional: notificaciones, progreso y botones de la tarea en el móvil
- Sin cuenta, sin nube, sin telemetría; la API solo escucha en 127.0.0.1
- 10 idiomas de interfaz

## Short description

Sigue y controla los trabajos largos de tu PC: progreso, gráficos, notificaciones y los botones propios de cada tarea. Funciona con cualquier script mediante una API local o el comando taskhub, y con Claude Code y Codex.
