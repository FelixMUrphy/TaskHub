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

    /// <summary>Setting value: "auto" or a language code.</summary>
    public static string Setting
    {
        get
        {
            var v = S == null ? "auto" : J.Str(S.Cfg["language"], "auto").ToLowerInvariant();
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
        ["setup_intro_update"] = new[] {
            "TaskHub is already installed. The installed version will be replaced; settings and tasks are kept.",
            "TaskHub вже встановлено. Встановлену версію буде замінено, налаштування і задачі збережуться.",
            "TaskHub ya está instalado. Se reemplazará la versión instalada; se conservan ajustes y tareas.",
            "TaskHub ist bereits installiert. Die installierte Version wird ersetzt; Einstellungen und Aufgaben bleiben.",
            "TaskHub est déjà installé. La version installée sera remplacée ; réglages et tâches sont conservés.",
            "TaskHub è già installato. La versione installata sarà sostituita; impostazioni e attività restano.",
            "O TaskHub já está instalado. A versão instalada será substituída; configurações e tarefas são mantidas.",
            "TaskHub jest już zainstalowany. Zainstalowana wersja zostanie zastąpiona; ustawienia i zadania zostaną.",
            "TaskHub 已安装。将替换已安装的版本，设置和任务保留。",
            "TaskHub は既にインストールされています。インストール済みのバージョンを置き換えます（設定とタスクは保持）。" },
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
