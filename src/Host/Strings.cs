using System.Globalization;

namespace TaskHub;

/// <summary>Host-side UI strings (tray menu, tooltip, toasts). The panel has its own table in ui.html.</summary>
static class L
{
    public static readonly string[] Langs = { "en", "uk", "es", "de", "fr", "it", "pt", "pl", "zh", "ja" };
    public static readonly Dictionary<string, string> Names = new()
    {
        ["en"] = "English", ["uk"] = "Українська", ["es"] = "Español", ["de"] = "Deutsch", ["fr"] = "Français",
        ["it"] = "Italiano", ["pt"] = "Português", ["pl"] = "Polski", ["zh"] = "中文", ["ja"] = "日本語",
    };

    public static Settings S;
    public const string RepoUrl = "https://github.com/FelixMUrphy/TaskHub";
    public const string SupportUrl = "https://github.com/sponsors/FelixMUrphy";

    /// <summary>Setting value: "auto" or a language code.</summary>
    public static string Setting
    {
        get
        {
            var v = S == null ? "auto" : J.Str(S.Get("language"), "auto").ToLowerInvariant();
            return v == "auto" || Langs.Contains(v) ? v : "auto";
        }
    }

    /// <summary>Effective language code.</summary>
    public static string Lang
    {
        get
        {
            var v = Setting;
            if (v != "auto") return v;
            var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Langs.Contains(sys) ? sys : "en";
        }
    }

    // key -> en, uk, es, de, fr, it, pt, pl, zh, ja
    static readonly Dictionary<string, string[]> T_ = new()
    {
        ["open_panel"] = new[] { "Open panel", "Відкрити панель", "Abrir panel", "Panel öffnen", "Ouvrir le panneau", "Apri pannello", "Abrir painel", "Otwórz panel", "打开面板", "パネルを開く" },
        ["mute"] = new[] { "Mute notifications", "Без сповіщень", "Silenciar notificaciones", "Benachrichtigungen stumm", "Couper les notifications", "Silenzia notifiche", "Silenciar notificações", "Wycisz powiadomienia", "静音通知", "通知をミュート" },
        ["clear_done"] = new[] { "Clear finished", "Прибрати завершені", "Quitar finalizadas", "Abgeschlossene entfernen", "Retirer les terminées", "Rimuovi completate", "Remover concluídas", "Usuń zakończone", "清除已完成", "完了を消去" },
        ["autostart"] = new[] { "Start with Windows", "Автозапуск", "Iniciar con Windows", "Mit Windows starten", "Lancer avec Windows", "Avvia con Windows", "Iniciar com o Windows", "Uruchamiaj z Windows", "开机启动", "Windows 起動時に開始" },
        ["test_toast"] = new[] { "Test notification", "Тестове сповіщення", "Notificación de prueba", "Testbenachrichtigung", "Notification de test", "Notifica di prova", "Notificação de teste", "Powiadomienie testowe", "测试通知", "テスト通知" },
        ["data_folder"] = new[] { "Data folder", "Тека даних", "Carpeta de datos", "Datenordner", "Dossier de données", "Cartella dati", "Pasta de dados", "Folder danych", "数据文件夹", "データフォルダー" },
        ["api_help"] = new[] { "API reference", "API (довідка)", "Referencia de la API", "API-Referenz", "Référence API", "Riferimento API", "Referência da API", "Dokumentacja API", "API 文档", "API リファレンス" },
        ["support"] = new[] { "❤ Support TaskHub", "❤ Підтримати TaskHub", "❤ Apoyar TaskHub", "❤ TaskHub unterstützen", "❤ Soutenir TaskHub", "❤ Sostieni TaskHub", "❤ Apoiar o TaskHub", "❤ Wesprzyj TaskHub", "❤ 支持 TaskHub", "❤ TaskHub を支援" },
        ["integrations"] = new[] { "AI agents", "AI-агенти", "Agentes de IA", "KI-Agenten", "Agents IA", "Agenti IA", "Agentes de IA", "Agenci AI", "AI 助手", "AI エージェント" },
        ["menu_hooks"] = new[] { "notifications (hooks)", "сповіщення (hooks)", "notificaciones (hooks)", "Benachrichtigungen (Hooks)", "notifications (hooks)", "notifiche (hooks)", "notificações (hooks)", "powiadomienia (hooks)", "通知（hooks）", "通知（hooks）" },
        ["menu_mcp"] = new[] { "TaskHub tools (MCP)", "інструменти TaskHub (MCP)", "herramientas de TaskHub (MCP)", "TaskHub-Werkzeuge (MCP)", "outils TaskHub (MCP)", "strumenti TaskHub (MCP)", "ferramentas do TaskHub (MCP)", "narzędzia TaskHub (MCP)", "TaskHub 工具（MCP）", "TaskHub ツール（MCP）" },
        ["setup_mcp"] = new[] { "Let {0} show its long tasks in TaskHub (MCP tools)", "Дозволити {0} показувати свої довгі задачі в TaskHub (інструменти MCP)", "Permitir que {0} muestre sus tareas largas en TaskHub (herramientas MCP)", "{0} darf seine langen Aufgaben in TaskHub zeigen (MCP-Werkzeuge)", "Permettre à {0} d'afficher ses longues tâches dans TaskHub (outils MCP)", "Consenti a {0} di mostrare i suoi task lunghi in TaskHub (strumenti MCP)", "Permitir que {0} mostre suas tarefas longas no TaskHub (ferramentas MCP)", "Pozwól {0} pokazywać długie zadania w TaskHub (narzędzia MCP)", "允许 {0} 在 TaskHub 中显示长任务（MCP 工具）", "{0} が長いタスクを TaskHub に表示できるようにする（MCP ツール）" },
        ["hint_claude"] = new[] { "Claude Code: start a new local session (desktop app Code tab → Local, CLI or VS Code). Cloud sessions can't reach TaskHub.", "Claude Code: відкрий нову локальну сесію (додаток → Code → Local, CLI або VS Code). Хмарні сесії не бачать TaskHub.", "Claude Code: inicia una nueva sesión local (app de escritorio Code → Local, CLI o VS Code). Las sesiones en la nube no llegan a TaskHub.", "Claude Code: neue lokale Sitzung starten (Desktop-App Code → Local, CLI oder VS Code). Cloud-Sitzungen erreichen TaskHub nicht.", "Claude Code : ouvrez une nouvelle session locale (app Code → Local, CLI ou VS Code). Les sessions cloud n'atteignent pas TaskHub.", "Claude Code: avvia una nuova sessione locale (app desktop Code → Local, CLI o VS Code). Le sessioni cloud non raggiungono TaskHub.", "Claude Code: inicie uma nova sessão local (app Code → Local, CLI ou VS Code). Sessões na nuvem não alcançam o TaskHub.", "Claude Code: uruchom nową sesję lokalną (aplikacja Code → Local, CLI lub VS Code). Sesje w chmurze nie widzą TaskHub.", "Claude Code：新开一个本地会话（桌面应用 Code → Local、CLI 或 VS Code）。云端会话无法访问 TaskHub。", "Claude Code：新しいローカルセッションを開始（デスクトップアプリ Code → Local、CLI、VS Code）。クラウドセッションは TaskHub に届きません。" },
        ["hint_claude_mcp"] = new[] { "Claude Code: start a new local session; /mcp should show ‘taskhub’ as connected. Cloud sessions can't reach TaskHub.", "Claude Code: відкрий нову локальну сесію; команда /mcp має показати «taskhub» як підключений. Хмарні сесії не бачать TaskHub.", "Claude Code: inicia una nueva sesión local; /mcp debe mostrar «taskhub» conectado. Las sesiones en la nube no llegan a TaskHub.", "Claude Code: neue lokale Sitzung starten; /mcp sollte „taskhub“ als verbunden zeigen. Cloud-Sitzungen erreichen TaskHub nicht.", "Claude Code : nouvelle session locale ; /mcp doit afficher « taskhub » connecté. Les sessions cloud n'atteignent pas TaskHub.", "Claude Code: avvia una nuova sessione locale; /mcp deve mostrare «taskhub» connesso. Le sessioni cloud non raggiungono TaskHub.", "Claude Code: inicie uma nova sessão local; /mcp deve mostrar “taskhub” conectado. Sessões na nuvem não alcançam o TaskHub.", "Claude Code: uruchom nową sesję lokalną; /mcp powinno pokazać „taskhub” jako połączony. Sesje w chmurze nie widzą TaskHub.", "Claude Code：新开本地会话；/mcp 应显示“taskhub”已连接。云端会话无法访问 TaskHub。", "Claude Code：新しいローカルセッションを開始。/mcp で「taskhub」が接続済みと表示されます。クラウドセッションは TaskHub に届きません。" },
        ["hint_codex_mcp"] = new[] { "Codex: restart Codex; Settings → MCP servers should list ‘taskhub’ as enabled.", "Codex: перезапусти Codex; у Settings → MCP servers має бути ввімкнений «taskhub».", "Codex: reinicia Codex; Settings → MCP servers debe mostrar «taskhub» activado.", "Codex: Codex neu starten; unter Settings → MCP servers sollte „taskhub“ aktiviert sein.", "Codex : redémarrez Codex ; Settings → MCP servers doit afficher « taskhub » activé.", "Codex: riavvia Codex; in Settings → MCP servers deve comparire «taskhub» attivo.", "Codex: reinicie o Codex; em Settings → MCP servers deve aparecer “taskhub” ativado.", "Codex: uruchom ponownie Codex; w Settings → MCP servers powinien być włączony „taskhub”.", "Codex：重启 Codex；Settings → MCP servers 中应显示已启用的“taskhub”。", "Codex：Codex を再起動。Settings → MCP servers に「taskhub」が有効として表示されます。" },
        ["setup_agent"] = new[] { "Notify me when {0} finishes or needs my permission", "Сповіщати, коли {0} закінчив або чекає на мій дозвіл", "Avisarme cuando {0} termine o necesite mi permiso", "Benachrichtigen, wenn {0} fertig ist oder meine Erlaubnis braucht", "M'avertir quand {0} a terminé ou attend mon autorisation", "Avvisami quando {0} finisce o chiede il mio permesso", "Avisar quando {0} terminar ou precisar da minha permissão", "Powiadamiaj, gdy {0} skończy lub potrzebuje mojej zgody", "{0} 完成或需要我的授权时通知我", "{0} が完了したときや許可が必要なときに通知" },
        ["codex_trust"] = new[] { "One more step for Codex: open Codex → Settings → Hooks and press “Trust all”.", "Ще один крок для Codex: відкрий Codex → Settings → Hooks і натисни «Trust all».", "Un paso más para Codex: abre Codex → Settings → Hooks y pulsa «Trust all».", "Noch ein Schritt für Codex: Codex → Settings → Hooks öffnen und „Trust all“ klicken.", "Encore une étape pour Codex : ouvrez Codex → Settings → Hooks et cliquez sur « Trust all ».", "Ancora un passo per Codex: apri Codex → Settings → Hooks e premi «Trust all».", "Mais um passo para o Codex: abra Codex → Settings → Hooks e clique em “Trust all”.", "Jeszcze jeden krok dla Codex: otwórz Codex → Settings → Hooks i kliknij „Trust all”.", "Codex 还需一步：打开 Codex → Settings → Hooks，点击“Trust all”。", "Codex でもう一手順：Codex → Settings → Hooks を開き「Trust all」を押してください。" },
        ["int_error"] = new[] { "Could not update {0} settings ({1}).", "Не вдалося змінити налаштування {0} ({1}).", "No se pudo actualizar la configuración de {0} ({1}).", "Einstellungen von {0} konnten nicht geändert werden ({1}).", "Impossible de modifier les réglages de {0} ({1}).", "Impossibile aggiornare le impostazioni di {0} ({1}).", "Não foi possível atualizar as configurações do {0} ({1}).", "Nie udało się zmienić ustawień {0} ({1}).", "无法更新 {0} 的设置（{1}）。", "{0} の設定を更新できませんでした（{1}）。" },
        ["agent_done"] = new[] { "response ready", "відповідь готова", "respuesta lista", "Antwort fertig", "réponse prête", "risposta pronta", "resposta pronta", "odpowiedź gotowa", "回复已就绪", "応答完了" },
        ["agent_input"] = new[] { "Needs your input", "Потрібна твоя дія", "Necesita tu respuesta", "Braucht deine Eingabe", "Attend ta réponse", "Serve il tuo input", "Precisa da sua ação", "Czeka na Twoją decyzję", "需要你的操作", "入力が必要です" },
        ["tg_menu"] = new[] { "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…", "Telegram…" },
        ["tg_title"] = new[] { "Telegram notifications", "Сповіщення в Telegram", "Notificaciones en Telegram", "Benachrichtigungen in Telegram", "Notifications Telegram", "Notifiche su Telegram", "Notificações no Telegram", "Powiadomienia w Telegramie", "Telegram 通知", "Telegram 通知" },
        ["tg_intro"] = new[] { "Get TaskHub notifications (with their buttons) in your own Telegram bot:\n1. In Telegram open @BotFather, send /newbot and copy the token it gives you.\n2. Paste the token below and press Connect.\n3. Open the link that appears and press Start.", "Отримуйте сповіщення TaskHub (з кнопками) у власному Telegram-боті:\n1. У Telegram відкрийте @BotFather, надішліть /newbot і скопіюйте токен.\n2. Вставте токен нижче й натисніть «Підключити».\n3. Відкрийте посилання, що з'явиться, і натисніть Start.", "Recibe las notificaciones de TaskHub (con sus botones) en tu propio bot de Telegram:\n1. En Telegram abre @BotFather, envía /newbot y copia el token.\n2. Pega el token abajo y pulsa Conectar.\n3. Abre el enlace que aparece y pulsa Start.", "TaskHub-Benachrichtigungen (mit Buttons) im eigenen Telegram-Bot erhalten:\n1. In Telegram @BotFather öffnen, /newbot senden und den Token kopieren.\n2. Token unten einfügen und Verbinden klicken.\n3. Den angezeigten Link öffnen und Start drücken.", "Recevez les notifications TaskHub (avec leurs boutons) dans votre propre bot Telegram :\n1. Dans Telegram, ouvrez @BotFather, envoyez /newbot et copiez le jeton.\n2. Collez le jeton ci-dessous et cliquez sur Connecter.\n3. Ouvrez le lien affiché et appuyez sur Start.", "Ricevi le notifiche di TaskHub (con i pulsanti) nel tuo bot Telegram:\n1. In Telegram apri @BotFather, invia /newbot e copia il token.\n2. Incolla il token qui sotto e premi Connetti.\n3. Apri il link che compare e premi Start.", "Receba as notificações do TaskHub (com botões) no seu próprio bot do Telegram:\n1. No Telegram abra o @BotFather, envie /newbot e copie o token.\n2. Cole o token abaixo e clique em Conectar.\n3. Abra o link que aparecer e toque em Start.", "Otrzymuj powiadomienia TaskHub (z przyciskami) we własnym bocie Telegram:\n1. W Telegramie otwórz @BotFather, wyślij /newbot i skopiuj token.\n2. Wklej token poniżej i kliknij Połącz.\n3. Otwórz wyświetlony link i naciśnij Start.", "在你自己的 Telegram 机器人中接收 TaskHub 通知（含按钮）：\n1. 在 Telegram 打开 @BotFather，发送 /newbot 并复制令牌。\n2. 将令牌粘贴到下方并点击“连接”。\n3. 打开出现的链接并点击 Start。", "TaskHub の通知（ボタン付き）を自分の Telegram ボットで受け取れます：\n1. Telegram で @BotFather を開き /newbot を送信してトークンをコピー。\n2. 下にトークンを貼り付けて「接続」を押す。\n3. 表示されたリンクを開いて Start を押す。" },
        ["tg_token"] = new[] { "Bot token:", "Токен бота:", "Token del bot:", "Bot-Token:", "Jeton du bot :", "Token del bot:", "Token do bot:", "Token bota:", "机器人令牌：", "ボットのトークン：" },
        ["tg_connect"] = new[] { "Connect", "Підключити", "Conectar", "Verbinden", "Connecter", "Connetti", "Conectar", "Połącz", "连接", "接続" },
        ["tg_open"] = new[] { "Or open on this PC: {0}", "Або відкрити на цьому ПК: {0}", "O abrir en este PC: {0}", "Oder auf diesem PC öffnen: {0}", "Ou ouvrir sur ce PC : {0}", "Oppure apri su questo PC: {0}", "Ou abrir neste PC: {0}", "Lub otwórz na tym PC: {0}", "或在此电脑上打开：{0}", "またはこの PC で開く：{0}" },
        ["tg_state_off"] = new[] { "Not connected.", "Не підключено.", "No conectado.", "Nicht verbunden.", "Non connecté.", "Non connesso.", "Não conectado.", "Nie połączono.", "未连接。", "未接続。" },
        ["tg_state_wait"] = new[] { "Waiting for Start in Telegram…", "Чекаю на Start у Telegram…", "Esperando Start en Telegram…", "Warte auf Start in Telegram…", "En attente de Start dans Telegram…", "In attesa di Start in Telegram…", "Aguardando Start no Telegram…", "Czekam na Start w Telegramie…", "等待在 Telegram 中点击 Start…", "Telegram で Start を待っています…" },
        ["tg_state_ok"] = new[] { "Connected to @{0}.", "Підключено до @{0}.", "Conectado a @{0}.", "Verbunden mit @{0}.", "Connecté à @{0}.", "Connesso a @{0}.", "Conectado a @{0}.", "Połączono z @{0}.", "已连接 @{0}。", "@{0} に接続しました。" },
        ["tg_away"] = new[] { "Only when I'm away from the PC (no keyboard/mouse for 5 min)", "Лише коли мене немає за ПК (5 хв без клавіатури й миші)", "Solo cuando no estoy en el PC (5 min sin teclado ni ratón)", "Nur wenn ich nicht am PC bin (5 Min. ohne Tastatur/Maus)", "Seulement quand je ne suis pas au PC (5 min sans clavier ni souris)", "Solo quando non sono al PC (5 min senza tastiera e mouse)", "Só quando não estou no PC (5 min sem teclado e mouse)", "Tylko gdy nie ma mnie przy PC (5 min bez klawiatury i myszy)", "仅当我不在电脑前（5 分钟无键鼠操作）", "PC から離れているときだけ（5 分間キーボード・マウス操作なし）" },
        ["tg_test"] = new[] { "Send test", "Тестове повідомлення", "Enviar prueba", "Test senden", "Envoyer un test", "Invia prova", "Enviar teste", "Wyślij test", "发送测试", "テスト送信" },
        ["tg_disconnect"] = new[] { "Disconnect", "Відключити", "Desconectar", "Trennen", "Déconnecter", "Disconnetti", "Desconectar", "Rozłącz", "断开", "切断" },
        ["tg_close"] = new[] { "Close", "Закрити", "Cerrar", "Schließen", "Fermer", "Chiudi", "Fechar", "Zamknij", "关闭", "閉じる" },
        ["tg_test_msg"] = new[] { "Test message from TaskHub on {0}.", "Тестове повідомлення від TaskHub на {0}.", "Mensaje de prueba de TaskHub en {0}.", "Testnachricht von TaskHub auf {0}.", "Message de test de TaskHub sur {0}.", "Messaggio di prova da TaskHub su {0}.", "Mensagem de teste do TaskHub em {0}.", "Wiadomość testowa z TaskHub na {0}.", "来自 {0} 上 TaskHub 的测试消息。", "{0} の TaskHub からのテストメッセージ。" },
        ["tg_linked"] = new[] { "Connected to TaskHub on {0}. Notifications will arrive here. /tasks - active tasks, /mute, /unmute.", "Підключено до TaskHub на {0}. Сповіщення приходитимуть сюди. /tasks - активні задачі, /mute, /unmute.", "Conectado a TaskHub en {0}. Las notificaciones llegarán aquí. /tasks - tareas activas, /mute, /unmute.", "Mit TaskHub auf {0} verbunden. Benachrichtigungen kommen hier an. /tasks - aktive Aufgaben, /mute, /unmute.", "Connecté à TaskHub sur {0}. Les notifications arriveront ici. /tasks - tâches actives, /mute, /unmute.", "Connesso a TaskHub su {0}. Le notifiche arriveranno qui. /tasks - task attivi, /mute, /unmute.", "Conectado ao TaskHub em {0}. As notificações chegarão aqui. /tasks - tarefas ativas, /mute, /unmute.", "Połączono z TaskHub na {0}. Powiadomienia będą tutaj. /tasks - aktywne zadania, /mute, /unmute.", "已连接到 {0} 上的 TaskHub。通知将发送到这里。/tasks - 活动任务，/mute，/unmute。", "{0} の TaskHub に接続しました。通知はここに届きます。/tasks - 実行中のタスク、/mute、/unmute。" },
        ["tg_expired"] = new[] { "This button is no longer active.", "Ця кнопка вже неактивна.", "Este botón ya no está activo.", "Dieser Button ist nicht mehr aktiv.", "Ce bouton n'est plus actif.", "Questo pulsante non è più attivo.", "Este botão não está mais ativo.", "Ten przycisk jest już nieaktywny.", "此按钮已失效。", "このボタンはもう無効です。" },
        ["tg_no_tasks"] = new[] { "No active tasks.", "Немає активних задач.", "No hay tareas activas.", "Keine aktiven Aufgaben.", "Aucune tâche active.", "Nessun task attivo.", "Nenhuma tarefa ativa.", "Brak aktywnych zadań.", "没有活动任务。", "実行中のタスクはありません。" },
        ["tg_help"] = new[] { "/tasks - active tasks, /mute - mute notifications, /unmute - turn them back on.", "/tasks - активні задачі, /mute - вимкнути сповіщення, /unmute - увімкнути знову.", "/tasks - tareas activas, /mute - silenciar, /unmute - reactivar.", "/tasks - aktive Aufgaben, /mute - stumm, /unmute - wieder an.", "/tasks - tâches actives, /mute - couper, /unmute - réactiver.", "/tasks - task attivi, /mute - silenzia, /unmute - riattiva.", "/tasks - tarefas ativas, /mute - silenciar, /unmute - reativar.", "/tasks - aktywne zadania, /mute - wycisz, /unmute - włącz ponownie.", "/tasks - 活动任务，/mute - 静音，/unmute - 取消静音。", "/tasks - 実行中のタスク、/mute - ミュート、/unmute - 解除。" },
        ["tg_qr"] = new[] { "Scan this code with the phone that has your Telegram account (camera app), open the bot and press Start.", "Відскануйте цей код телефоном, де ваш акаунт Telegram (камерою), відкрийте бота й натисніть Start.", "Escanea este código con el teléfono que tiene tu cuenta de Telegram (cámara), abre el bot y pulsa Start.", "Diesen Code mit dem Handy scannen, auf dem dein Telegram-Konto ist (Kamera), den Bot öffnen und Start drücken.", "Scannez ce code avec le téléphone qui a votre compte Telegram (appareil photo), ouvrez le bot et appuyez sur Start.", "Scansiona questo codice con il telefono che ha il tuo account Telegram (fotocamera), apri il bot e premi Start.", "Escaneie este código com o celular que tem sua conta do Telegram (câmera), abra o bot e toque em Start.", "Zeskanuj ten kod telefonem z Twoim kontem Telegram (aparat), otwórz bota i naciśnij Start.", "用登录了你的 Telegram 账号的手机（相机）扫描此码，打开机器人并点击 Start。", "Telegram アカウントのあるスマホ（カメラ）でこのコードを読み取り、ボットを開いて Start を押してください。" },
        ["tg_relink_ph"] = new[] { "Bot is set up - leave empty and press Connect to link another chat", "Бот уже налаштовано - залиште порожнім і натисніть «Підключити», щоб прив'язати інший чат", "El bot ya está configurado: deja vacío y pulsa Conectar para vincular otro chat", "Bot ist eingerichtet - leer lassen und Verbinden drücken, um einen anderen Chat zu verknüpfen", "Bot déjà configuré : laissez vide et cliquez sur Connecter pour lier un autre chat", "Bot già configurato: lascia vuoto e premi Connetti per collegare un'altra chat", "Bot já configurado: deixe vazio e clique em Conectar para vincular outro chat", "Bot jest skonfigurowany - zostaw puste i kliknij Połącz, aby powiązać inny czat", "机器人已设置——留空并点击“连接”可绑定另一个聊天", "ボットは設定済み - 空のまま「接続」を押すと別のチャットを紐付けます" },
        ["tg_progress"] = new[] { "Progress bar (one message, updated live) and charts when a task finishes", "Прогрес (одне повідомлення, що оновлюється) і графіки після завершення задачі", "Progreso (un mensaje que se actualiza) y gráficos al terminar la tarea", "Fortschritt (eine Nachricht, live aktualisiert) und Diagramme am Ende der Aufgabe", "Progression (un message mis à jour) et graphiques à la fin de la tâche", "Avanzamento (un messaggio aggiornato) e grafici al termine dell'attività", "Progresso (uma mensagem atualizada) e gráficos ao terminar a tarefa", "Postęp (jedna aktualizowana wiadomość) i wykresy po zakończeniu zadania", "进度（同一条消息实时更新）以及任务完成时的图表", "進捗（1件のメッセージを更新）とタスク完了時のグラフ" },
        ["tg_ask"] = new[] { "{0} - reply to this message with the value", "{0} - надішліть значення у відповідь на це повідомлення", "{0}: responde a este mensaje con el valor", "{0} - antworte auf diese Nachricht mit dem Wert", "{0} : répondez à ce message avec la valeur", "{0} - rispondi a questo messaggio con il valore", "{0} - responda a esta mensagem com o valor", "{0} - odpowiedz na tę wiadomość, podając wartość", "{0} - 回复此消息并填写数值", "{0} - このメッセージに返信して値を送ってください" },
        ["tg_private_only"] = new[] { "Open this link in a private chat with the bot, not in a group: in a group every member could press the tasks' buttons.", "Відкрий посилання в приватному чаті з ботом, а не в групі: у групі кнопки задач міг би натискати будь-який учасник.", "Abre el enlace en un chat privado con el bot, no en un grupo: en un grupo cualquier miembro podría pulsar los botones de las tareas.", "Öffne den Link in einem privaten Chat mit dem Bot, nicht in einer Gruppe: dort könnte jedes Mitglied die Schaltflächen der Aufgaben drücken.", "Ouvre le lien dans une conversation privée avec le bot, pas dans un groupe : dans un groupe, chaque membre pourrait appuyer sur les boutons des tâches.", "Apri il link in una chat privata con il bot, non in un gruppo: in un gruppo qualsiasi membro potrebbe premere i pulsanti dei task.", "Abra o link num chat privado com o bot, não num grupo: num grupo qualquer membro poderia premir os botões das tarefas.", "Otwórz link w prywatnym czacie z botem, nie w grupie: w grupie każdy członek mógłby naciskać przyciski zadań.", "请在与机器人的私聊中打开此链接，而不是在群组中：在群组中任何成员都可以按下任务按钮。", "グループではなくボットとの個人チャットでこのリンクを開いてください。グループではメンバー全員がタスクのボタンを押せてしまいます。" },
        ["tg_bad_value"] = new[] { "Invalid value: {0}", "Неправильне значення: {0}", "Valor no válido: {0}", "Ungültiger Wert: {0}", "Valeur invalide : {0}", "Valore non valido: {0}", "Valor inválido: {0}", "Nieprawidłowa wartość: {0}", "无效的值：{0}", "無効な値: {0}" },
        ["quit"] = new[] { "Exit", "Вийти", "Salir", "Beenden", "Quitter", "Esci", "Sair", "Zakończ", "退出", "終了" },
        ["language"] = new[] { "Language", "Мова", "Idioma", "Sprache", "Langue", "Lingua", "Idioma", "Język", "语言", "言語" },
        ["auto"] = new[] { "Auto (system)", "Авто (системна)", "Automático (sistema)", "Automatisch (System)", "Auto (système)", "Automatica (sistema)", "Automático (sistema)", "Automatycznie (system)", "自动（系统）", "自動（システム）" },
        ["yes"] = new[] { "yes", "так", "sí", "ja", "oui", "sì", "sim", "tak", "是", "はい" },
        ["no"] = new[] { "no", "ні", "no", "nein", "non", "no", "não", "nie", "否", "いいえ" },
        ["active_n"] = new[] { "{0} active", "{0} активних", "{0} activas", "{0} aktiv", "{0} actives", "{0} attive", "{0} ativas", "{0} aktywne", "{0} 个进行中", "{0} 件実行中" },
        ["failed_n"] = new[] { "{0} failed", "{0} з помилкою", "{0} fallidas", "{0} fehlgeschlagen", "{0} en échec", "{0} fallite", "{0} com falha", "{0} z błędem", "{0} 个失败", "{0} 件失敗" },
        ["muted"] = new[] { "(muted)", "(без сповіщень)", "(silenciado)", "(stumm)", "(muet)", "(silenzioso)", "(silenciado)", "(wyciszone)", "（已静音）", "（ミュート）" },
        ["done"] = new[] { "done", "готово", "completada", "fertig", "terminée", "completata", "concluída", "gotowe", "已完成", "完了" },
        ["cancelled"] = new[] { "cancelled", "скасовано", "cancelada", "abgebrochen", "annulée", "annullata", "cancelada", "anulowane", "已取消", "キャンセル" },
        ["started"] = new[] { "started", "запущено", "iniciada", "gestartet", "démarrée", "avviata", "iniciada", "uruchomione", "已开始", "開始" },
        ["no_updates"] = new[] { "no updates for {0}", "немає оновлень {0}", "sin actualizaciones desde hace {0}", "seit {0} keine Updates", "aucune mise à jour depuis {0}", "nessun aggiornamento da {0}", "sem atualizações há {0}", "brak aktualizacji od {0}", "{0} 内无更新", "{0} 更新なし" },
        ["install_q"] = new[] {
            "Install TaskHub for the current user?\n\nYes - install to {0}, start with Windows, add the 'taskhub' command to PATH.\nNo - just run it from this folder.",
            "Встановити TaskHub для поточного користувача?\n\nТак - встановити в {0}, запускати разом з Windows, додати команду 'taskhub' у PATH.\nНі - просто запустити з цієї теки.",
            "¿Instalar TaskHub para el usuario actual?\n\nSí - instalar en {0}, iniciar con Windows, añadir el comando 'taskhub' al PATH.\nNo - solo ejecutarlo desde esta carpeta.",
            "TaskHub für den aktuellen Benutzer installieren?\n\nJa - nach {0} installieren, mit Windows starten, Befehl 'taskhub' zum PATH hinzufügen.\nNein - nur aus diesem Ordner starten.",
            "Installer TaskHub pour l'utilisateur actuel ?\n\nOui - installer dans {0}, lancer avec Windows, ajouter la commande 'taskhub' au PATH.\nNon - simplement l'exécuter depuis ce dossier.",
            "Installare TaskHub per l'utente corrente?\n\nSì - installa in {0}, avvio con Windows, comando 'taskhub' nel PATH.\nNo - eseguilo solo da questa cartella.",
            "Instalar o TaskHub para o usuário atual?\n\nSim - instalar em {0}, iniciar com o Windows, adicionar o comando 'taskhub' ao PATH.\nNão - apenas executar desta pasta.",
            "Zainstalować TaskHub dla bieżącego użytkownika?\n\nTak - instalacja w {0}, start z Windows, polecenie 'taskhub' w PATH.\nNie - tylko uruchom z tego folderu.",
            "为当前用户安装 TaskHub？\n\n是 - 安装到 {0}，开机启动，将 'taskhub' 命令加入 PATH。\n否 - 仅从此文件夹运行。",
            "現在のユーザーに TaskHub をインストールしますか？\n\nはい - {0} にインストールし、Windows 起動時に開始し、'taskhub' コマンドを PATH に追加します。\nいいえ - このフォルダーから実行するだけです。" },
        ["update_q"] = new[] {
            "TaskHub is already installed in {0}.\n\nYes - replace it with this version (settings and tasks are kept).\nNo - just run this copy.",
            "TaskHub вже встановлено в {0}.\n\nТак - замінити цією версією (налаштування і задачі збережуться).\nНі - просто запустити цю копію.",
            "TaskHub ya está instalado en {0}.\n\nSí - reemplazarlo por esta versión (se conservan ajustes y tareas).\nNo - solo ejecutar esta copia.",
            "TaskHub ist bereits in {0} installiert.\n\nJa - durch diese Version ersetzen (Einstellungen und Aufgaben bleiben).\nNein - nur diese Kopie starten.",
            "TaskHub est déjà installé dans {0}.\n\nOui - le remplacer par cette version (réglages et tâches conservés).\nNon - lancer seulement cette copie.",
            "TaskHub è già installato in {0}.\n\nSì - sostituiscilo con questa versione (impostazioni e attività restano).\nNo - esegui solo questa copia.",
            "O TaskHub já está instalado em {0}.\n\nSim - substituir por esta versão (configurações e tarefas são mantidas).\nNão - apenas executar esta cópia.",
            "TaskHub jest już zainstalowany w {0}.\n\nTak - zastąp tą wersją (ustawienia i zadania zostaną).\nNie - tylko uruchom tę kopię.",
            "TaskHub 已安装在 {0}。\n\n是 - 用此版本替换（保留设置和任务）。\n否 - 仅运行此副本。",
            "TaskHub は既に {0} にインストールされています。\n\nはい - このバージョンに置き換える（設定とタスクは保持）。\nいいえ - このコピーを実行するだけです。" },
        // setup dialog                       en, uk, es, de, fr, it, pt, pl, zh, ja
        ["setup_title"] = new[] { "Setup", "Встановлення", "Instalación", "Installation", "Installation", "Installazione", "Instalação", "Instalacja", "安装", "セットアップ" },
        ["setup_update"] = new[] { "Update", "Оновлення", "Actualización", "Aktualisierung", "Mise à jour", "Aggiornamento", "Atualização", "Aktualizacja", "更新", "更新" },
        ["setup_intro"] = new[] {
            "TaskHub will be installed for the current user (no administrator rights needed). Settings and task history are stored in %APPDATA%\\TaskHub.",
            "TaskHub буде встановлено для поточного користувача (права адміністратора не потрібні). Налаштування та історія задач зберігаються в %APPDATA%\\TaskHub.",
            "TaskHub se instalará para el usuario actual (sin derechos de administrador). Los ajustes y el historial se guardan en %APPDATA%\\TaskHub.",
            "TaskHub wird für den aktuellen Benutzer installiert (keine Administratorrechte nötig). Einstellungen und Verlauf liegen in %APPDATA%\\TaskHub.",
            "TaskHub sera installé pour l'utilisateur actuel (sans droits administrateur). Réglages et historique : %APPDATA%\\TaskHub.",
            "TaskHub verrà installato per l'utente corrente (senza diritti di amministratore). Impostazioni e cronologia in %APPDATA%\\TaskHub.",
            "O TaskHub será instalado para o usuário atual (sem direitos de administrador). Configurações e histórico ficam em %APPDATA%\\TaskHub.",
            "TaskHub zostanie zainstalowany dla bieżącego użytkownika (bez uprawnień administratora). Ustawienia i historia: %APPDATA%\\TaskHub.",
            "将为当前用户安装 TaskHub（无需管理员权限）。设置和任务历史保存在 %APPDATA%\\TaskHub。",
            "TaskHub を現在のユーザーにインストールします（管理者権限は不要）。設定と履歴は %APPDATA%\\TaskHub に保存されます。" },
        ["setup_reinstall"] = new[] { "Reinstall", "Перевстановлення", "Reinstalación", "Neuinstallation", "Réinstallation", "Reinstallazione", "Reinstalação", "Ponowna instalacja", "重新安装", "再インストール" },
        ["setup_downgrade"] = new[] { "Older version", "Старіша версія", "Versión anterior", "Ältere Version", "Version plus ancienne", "Versione precedente", "Versão anterior", "Starsza wersja", "旧版本", "以前のバージョン" },
        ["setup_installed"] = new[] {
            "TaskHub {0} is installed in {1}.",
            "TaskHub {0} встановлено в {1}.",
            "TaskHub {0} está instalado en {1}.",
            "TaskHub {0} ist in {1} installiert.",
            "TaskHub {0} est installé dans {1}.",
            "TaskHub {0} è installato in {1}.",
            "O TaskHub {0} está instalado em {1}.",
            "TaskHub {0} jest zainstalowany w {1}.",
            "TaskHub {0} 已安装在 {1}。",
            "TaskHub {0} は {1} にインストールされています。" },
        ["setup_will_update"] = new[] {
            "It will be updated to {0}; settings and tasks are kept.",
            "Його буде оновлено до {0}, налаштування і задачі збережуться.",
            "Se actualizará a la {0}; se conservan ajustes y tareas.",
            "Es wird auf {0} aktualisiert; Einstellungen und Aufgaben bleiben.",
            "Il sera mis à jour vers {0} ; réglages et tâches sont conservés.",
            "Sarà aggiornato alla {0}; impostazioni e attività restano.",
            "Ele será atualizado para {0}; configurações e tarefas são mantidas.",
            "Zostanie zaktualizowany do {0}; ustawienia i zadania zostaną.",
            "将更新到 {0}，设置和任务保留。",
            "{0} に更新します（設定とタスクは保持）。" },
        ["setup_will_reinstall"] = new[] {
            "This is the same version. Reinstall it to repair the files; settings and tasks are kept.",
            "Це та сама версія. Перевстановлення відновить файли, налаштування і задачі збережуться.",
            "Es la misma versión. Reinstalarla repara los archivos; se conservan ajustes y tareas.",
            "Das ist dieselbe Version. Eine Neuinstallation repariert die Dateien; Einstellungen und Aufgaben bleiben.",
            "C'est la même version. La réinstaller répare les fichiers ; réglages et tâches sont conservés.",
            "È la stessa versione. Reinstallarla ripara i file; impostazioni e attività restano.",
            "É a mesma versão. Reinstalar repara os arquivos; configurações e tarefas são mantidas.",
            "To ta sama wersja. Ponowna instalacja naprawi pliki; ustawienia i zadania zostaną.",
            "这是相同的版本。重新安装可修复文件，设置和任务保留。",
            "同じバージョンです。再インストールでファイルを修復します（設定とタスクは保持）。" },
        ["setup_will_downgrade"] = new[] {
            "This setup contains an older version ({0}). Installing it replaces the newer one; settings and tasks are kept.",
            "Цей інсталятор містить старішу версію ({0}). Вона замінить новішу, налаштування і задачі збережуться.",
            "Este instalador contiene una versión anterior ({0}). Reemplazará la más nueva; se conservan ajustes y tareas.",
            "Dieses Setup enthält eine ältere Version ({0}). Sie ersetzt die neuere; Einstellungen und Aufgaben bleiben.",
            "Ce programme contient une version plus ancienne ({0}). Elle remplacera la plus récente ; réglages et tâches sont conservés.",
            "Questo setup contiene una versione precedente ({0}). Sostituirà quella più recente; impostazioni e attività restano.",
            "Este instalador contém uma versão anterior ({0}). Ela substituirá a mais nova; configurações e tarefas são mantidas.",
            "Ten instalator zawiera starszą wersję ({0}). Zastąpi nowszą; ustawienia i zadania zostaną.",
            "此安装程序包含旧版本（{0}）。安装后将替换较新的版本，设置和任务保留。",
            "このセットアップは以前のバージョン（{0}）です。新しいバージョンを置き換えます（設定とタスクは保持）。" },
        ["setup_reinstall_btn"] = new[] { "Reinstall", "Перевстановити", "Reinstalar", "Neu installieren", "Réinstaller", "Reinstalla", "Reinstalar", "Zainstaluj ponownie", "重新安装", "再インストール" },
        ["setup_downgrade_btn"] = new[] { "Install {0}", "Встановити {0}", "Instalar {0}", "{0} installieren", "Installer {0}", "Installa {0}", "Instalar {0}", "Zainstaluj {0}", "安装 {0}", "{0} をインストール" },
        ["setup_folder"] = new[] { "Install folder:", "Тека встановлення:", "Carpeta de instalación:", "Installationsordner:", "Dossier d'installation :", "Cartella di installazione:", "Pasta de instalação:", "Folder instalacji:", "安装文件夹：", "インストール先フォルダー：" },
        ["setup_autostart"] = new[] { "Start with Windows", "Запускати разом з Windows", "Iniciar con Windows", "Mit Windows starten", "Lancer avec Windows", "Avvia con Windows", "Iniciar com o Windows", "Uruchamiaj z Windows", "开机启动", "Windows 起動時に開始" },
        ["setup_path"] = new[] { "Add the 'taskhub' command to PATH", "Додати команду 'taskhub' у PATH", "Añadir el comando 'taskhub' al PATH", "Befehl 'taskhub' zum PATH hinzufügen", "Ajouter la commande 'taskhub' au PATH", "Aggiungi il comando 'taskhub' al PATH", "Adicionar o comando 'taskhub' ao PATH", "Dodaj polecenie 'taskhub' do PATH", "将 'taskhub' 命令加入 PATH", "'taskhub' コマンドを PATH に追加" },
        ["setup_install"] = new[] { "Install", "Встановити", "Instalar", "Installieren", "Installer", "Installa", "Instalar", "Zainstaluj", "安装", "インストール" },
        ["setup_update_btn"] = new[] { "Update", "Оновити", "Actualizar", "Aktualisieren", "Mettre à jour", "Aggiorna", "Atualizar", "Aktualizuj", "更新", "更新" },
        ["setup_run"] = new[] { "Run without installing", "Запустити без встановлення", "Ejecutar sin instalar", "Ohne Installation starten", "Lancer sans installer", "Esegui senza installare", "Executar sem instalar", "Uruchom bez instalacji", "不安装直接运行", "インストールせずに実行" },
        ["setup_cancel"] = new[] { "Cancel", "Скасувати", "Cancelar", "Abbrechen", "Annuler", "Annulla", "Cancelar", "Anuluj", "取消", "キャンセル" },
        ["setup_bad_folder"] = new[] { "Enter a full folder path.", "Вкажіть повний шлях до теки.", "Indica una ruta completa.", "Bitte einen vollständigen Ordnerpfad angeben.", "Indiquez un chemin complet.", "Indica un percorso completo.", "Informe um caminho completo.", "Podaj pełną ścieżkę folderu.", "请输入完整的文件夹路径。", "フォルダーのフルパスを入力してください。" },
        ["setup_no_access"] = new[] { "Cannot write to {0}. Choose a folder you can write to without administrator rights (e.g. inside your user folder).", "Немає доступу на запис у {0}. Оберіть теку, доступну без прав адміністратора (наприклад, у теці користувача).", "No se puede escribir en {0}. Elige una carpeta con permiso de escritura sin derechos de administrador.", "Kein Schreibzugriff auf {0}. Wählen Sie einen Ordner ohne Administratorrechte (z. B. im Benutzerordner).", "Impossible d'écrire dans {0}. Choisissez un dossier accessible sans droits administrateur.", "Impossibile scrivere in {0}. Scegli una cartella scrivibile senza diritti di amministratore.", "Não é possível gravar em {0}. Escolha uma pasta gravável sem direitos de administrador.", "Brak zapisu do {0}. Wybierz folder dostępny bez uprawnień administratora.", "无法写入 {0}。请选择无需管理员权限即可写入的文件夹。", "{0} に書き込めません。管理者権限なしで書き込めるフォルダーを選んでください。" },
        ["installed"] = new[] { "TaskHub is installed and running (tray icon).", "TaskHub встановлено й запущено (іконка в треї).", "TaskHub está instalado y en ejecución (icono en la bandeja).", "TaskHub ist installiert und läuft (Infobereich).", "TaskHub est installé et lancé (zone de notification).", "TaskHub è installato e in esecuzione (area di notifica).", "O TaskHub está instalado e em execução (bandeja).", "TaskHub jest zainstalowany i działa (zasobnik).", "TaskHub 已安装并正在运行（托盘图标）。", "TaskHub をインストールし、起動しました（タスクトレイ）。" },
        ["uninstall_q"] = new[] { "Uninstall TaskHub?", "Видалити TaskHub?", "¿Desinstalar TaskHub?", "TaskHub deinstallieren?", "Désinstaller TaskHub ?", "Disinstallare TaskHub?", "Desinstalar o TaskHub?", "Odinstalować TaskHub?", "卸载 TaskHub？", "TaskHub をアンインストールしますか？" },
        ["uninstalled"] = new[] { "TaskHub removed. Your data is kept in {0}.", "TaskHub видалено. Дані збережено в {0}.", "TaskHub eliminado. Tus datos se conservan en {0}.", "TaskHub entfernt. Daten bleiben in {0}.", "TaskHub supprimé. Vos données restent dans {0}.", "TaskHub rimosso. I dati restano in {0}.", "TaskHub removido. Os dados ficam em {0}.", "TaskHub usunięty. Dane pozostają w {0}.", "已移除 TaskHub。数据保留在 {0}。", "TaskHub を削除しました。データは {0} に残っています。" },
        ["updates"] = new[] { "Updates", "Оновлення", "Actualizaciones", "Updates", "Mises à jour", "Aggiornamenti", "Atualizações", "Aktualizacje", "更新", "アップデート" },
        ["update_check_now"] = new[] { "Check now", "Перевірити зараз", "Comprobar ahora", "Jetzt prüfen", "Vérifier maintenant", "Controlla ora", "Verificar agora", "Sprawdź teraz", "立即检查", "今すぐ確認" },
        ["update_auto"] = new[] { "Check daily", "Перевіряти щодня", "Comprobar a diario", "Täglich prüfen", "Vérifier chaque jour", "Controlla ogni giorno", "Verificar diariamente", "Sprawdzaj codziennie", "每天检查", "毎日確認" },
        ["update_to"] = new[] { "⬆ Update to {0}…", "⬆ Оновити до {0}…", "⬆ Actualizar a {0}…", "⬆ Auf {0} aktualisieren…", "⬆ Mettre à jour vers {0}…", "⬆ Aggiorna a {0}…", "⬆ Atualizar para {0}…", "⬆ Aktualizuj do {0}…", "⬆ 更新到 {0}…", "⬆ {0} に更新…" },
        ["update_title"] = new[] { "TaskHub {0} is available", "Доступний TaskHub {0}", "TaskHub {0} disponible", "TaskHub {0} ist verfügbar", "TaskHub {0} est disponible", "TaskHub {0} è disponibile", "TaskHub {0} disponível", "Dostępny TaskHub {0}", "TaskHub {0} 已发布", "TaskHub {0} が利用可能です" },
        ["update_msg"] = new[] { "You have {0}. Click to open the download page.", "У вас {0}. Натисніть, щоб відкрити сторінку завантаження.", "Tienes la {0}. Haz clic para abrir la página de descarga.", "Installiert: {0}. Klicken öffnet die Download-Seite.", "Vous avez la {0}. Cliquez pour ouvrir la page de téléchargement.", "Hai la {0}. Fai clic per aprire la pagina di download.", "Você tem a {0}. Clique para abrir a página de download.", "Masz {0}. Kliknij, aby otworzyć stronę pobierania.", "当前版本 {0}。点击打开下载页面。", "現在 {0}。クリックでダウンロードページを開きます。" },
        ["update_latest"] = new[] { "TaskHub {0} is the latest version.", "TaskHub {0} — остання версія.", "TaskHub {0} es la última versión.", "TaskHub {0} ist aktuell.", "TaskHub {0} est la dernière version.", "TaskHub {0} è l'ultima versione.", "TaskHub {0} é a versão mais recente.", "TaskHub {0} to najnowsza wersja.", "TaskHub {0} 已是最新版本。", "TaskHub {0} は最新です。" },
        ["update_failed"] = new[] { "Could not check for updates: {0}", "Не вдалося перевірити оновлення: {0}", "No se pudieron buscar actualizaciones: {0}", "Update-Prüfung fehlgeschlagen: {0}", "Impossible de vérifier les mises à jour : {0}", "Impossibile controllare gli aggiornamenti: {0}", "Não foi possível verificar atualizações: {0}", "Nie udało się sprawdzić aktualizacji: {0}", "无法检查更新：{0}", "更新を確認できませんでした: {0}" },
        ["store_managed"] = new[] {
            "This TaskHub is installed from the Microsoft Store: Windows installs, updates and removes it (Settings → Apps).",
            "Цей TaskHub встановлено з Microsoft Store: його встановлює, оновлює й видаляє Windows (Параметри → Програми).",
            "Este TaskHub se instaló desde Microsoft Store: Windows lo instala, actualiza y quita (Configuración → Aplicaciones).",
            "Dieses TaskHub stammt aus dem Microsoft Store: Windows installiert, aktualisiert und entfernt es (Einstellungen → Apps).",
            "Ce TaskHub provient du Microsoft Store : Windows l'installe, le met à jour et le supprime (Paramètres → Applications).",
            "Questo TaskHub proviene dal Microsoft Store: Windows lo installa, aggiorna e rimuove (Impostazioni → App).",
            "Este TaskHub foi instalado pela Microsoft Store: o Windows o instala, atualiza e remove (Configurações → Aplicativos).",
            "Ten TaskHub pochodzi z Microsoft Store: Windows go instaluje, aktualizuje i usuwa (Ustawienia → Aplikacje).",
            "此 TaskHub 来自 Microsoft Store：由 Windows 安装、更新和删除（设置 → 应用）。",
            "この TaskHub は Microsoft Store からインストールされています。インストール・更新・削除は Windows が行います（設定 → アプリ）。" },
        ["startup_by_user"] = new[] {
            "Autostart was turned off in Windows. Turn it on in Settings → Apps → Startup (or Task Manager → Startup apps).",
            "Автозапуск вимкнено у Windows. Увімкніть його в Параметри → Програми → Автозавантаження (або Диспетчер завдань → Автозавантаження).",
            "El inicio automático se desactivó en Windows. Actívalo en Configuración → Aplicaciones → Inicio (o Administrador de tareas → Aplicaciones de arranque).",
            "Autostart wurde in Windows deaktiviert. Aktiviere ihn unter Einstellungen → Apps → Autostart (oder Task-Manager → Autostart-Apps).",
            "Le démarrage automatique a été désactivé dans Windows. Activez-le dans Paramètres → Applications → Démarrage (ou Gestionnaire des tâches → Applications de démarrage).",
            "L'avvio automatico è stato disattivato in Windows. Attivalo in Impostazioni → App → Avvio (o Gestione attività → App di avvio).",
            "A inicialização automática foi desativada no Windows. Ative-a em Configurações → Aplicativos → Inicialização (ou Gerenciador de Tarefas → Aplicativos de inicialização).",
            "Autostart został wyłączony w Windows. Włącz go w Ustawienia → Aplikacje → Uruchamianie (lub Menedżer zadań → Aplikacje autostartu).",
            "已在 Windows 中关闭自动启动。请在 设置 → 应用 → 启动（或 任务管理器 → 启动应用）中开启。",
            "Windows で自動起動がオフになっています。設定 → アプリ → スタートアップ（またはタスク マネージャー → スタートアップ アプリ）でオンにしてください。" },
        ["kill_confirm"] = new[] { "Kill process {0} and its children?", "Завершити процес {0} і його дочірні?", "¿Terminar el proceso {0} y sus hijos?", "Prozess {0} samt Unterprozessen beenden?", "Tuer le processus {0} et ses enfants ?", "Terminare il processo {0} e i figli?", "Encerrar o processo {0} e os filhos?", "Zakończyć proces {0} i jego potomne?", "结束进程 {0} 及其子进程？", "プロセス {0} と子プロセスを終了しますか？" },
    };

    public static string T(string key, params object[] a)
    {
        if (!T_.TryGetValue(key, out var arr)) return key;
        var i = Array.IndexOf(Langs, Lang);
        var s = arr[i < 0 ? 0 : i];
        return a.Length > 0 ? string.Format(s, a) : s;
    }
}
