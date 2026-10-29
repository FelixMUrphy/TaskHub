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
