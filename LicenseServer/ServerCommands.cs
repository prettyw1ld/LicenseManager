namespace LicenseServer;

/// <summary>Консольные команды администратора сервера.</summary>
public class ServerCommands
{
    private readonly LicenseServerHost _server;

    public ServerCommands(LicenseServerHost server) => _server = server;

    public void PrintHelp()
    {
        Logger.Plain("""

        Команды:
          list                     — подключённые клиенты (дата, длительность, IP, порт, код)
          kick <№|код|логин>       — отключить клиента
          ban <№|логин|IP> [прич.] — добавить в чёрный список (и отключить)
          unban <логин|IP>         — убрать из чёрного списка
          blacklist                — показать чёрный список
          users                    — пользователи из БД
          adduser <логин> <пароль> — добавить пользователя
          settings                 — текущие настройки
          clear                    — очистить экран
          help                     — эта справка
          exit                     — остановить сервер

        """);
    }

    /// <summary>Выполняет команду. Возвращает false, если нужно завершить работу.</summary>
    public async Task<bool> ExecuteAsync(string line)
    {
        var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        var cmd = parts[0].ToLowerInvariant();
        var arg1 = parts.Length > 1 ? parts[1] : null;
        var rest = parts.Length > 2 ? parts[2] : null;

        try
        {
            switch (cmd)
            {
                case "list": case "ls": PrintClients(); break;
                case "kick": await KickAsync(arg1); break;
                case "ban": await BanAsync(arg1, rest); break;
                case "unban": await UnbanAsync(arg1); break;
                case "blacklist": case "bl": await PrintBlacklistAsync(); break;
                case "users": await PrintUsersAsync(); break;
                case "adduser": await AddUserAsync(arg1, rest); break;
                case "settings": PrintSettings(); break;
                case "clear": case "cls": Console.Clear(); break;
                case "help": case "?": PrintHelp(); break;
                case "exit": case "quit": case "stop": return false;
                default: Logger.Warn($"Неизвестная команда «{cmd}». Введите help."); break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка выполнения команды: {ex.Message}");
        }
        return true;
    }

    private void PrintClients()
    {
        var list = _server.Sessions;
        Logger.Plain($"\nПодключено клиентов: {list.Count}, занято лицензий: {_server.UsedLicenses}/{_server.Settings.MaxLicenses}");
        if (list.Count == 0) { Logger.Plain(""); return; }

        Logger.Plain($"{"№",-4} {"Логин",-12} {"IP-адрес",-16} {"Порт",-6} {"Подключён",-19} {"Длит.",-9} {"Осталось",-9} {"Код",-20} Состояние");
        Logger.Plain(new string('-', 112));
        foreach (var s in list)
        {
            var left = s.LicensedAt == null
                ? "-"
                : FormatSpan(s.LicensedAt.Value.AddSeconds(_server.Settings.SessionLifetimeSeconds) - DateTime.Now);
            Logger.Plain($"{s.Id,-4} {s.Login ?? "-",-12} {s.Ip,-16} {s.Port,-6} {s.ConnectedAt:dd.MM.yyyy HH:mm:ss} " +
                         $"{FormatSpan(s.Duration),-9} {left,-9} {s.Code ?? "-",-20} {StateName(s.State)}");
        }
        Logger.Plain("");
    }

    private async Task KickAsync(string? target)
    {
        if (target == null) { Logger.Warn("Использование: kick <№|код|логин>"); return; }
        var s = _server.FindSession(target);
        if (s == null) { Logger.Warn($"Клиент «{target}» не найден."); return; }
        await _server.KickAsync(s);
        Logger.Success($"Клиент {s.Describe()} отключён.");
    }

    private async Task BanAsync(string? target, string? reason)
    {
        if (target == null) { Logger.Warn("Использование: ban <№|логин|IP> [причина]"); return; }

        // Номер подключения -> баним логин этого клиента
        if (int.TryParse(target.TrimStart('#'), out _) && _server.FindSession(target) is { } s)
        {
            await _server.BanSessionAsync(s, reason);
            Logger.Success($"Клиент {s.Describe()} добавлен в чёрный список и отключён.");
            return;
        }

        var kicked = await _server.BanAsync(target, reason);
        Logger.Success($"«{target}» добавлен в чёрный список. Отключено подключений: {kicked}.");
    }

    private async Task UnbanAsync(string? target)
    {
        if (target == null) { Logger.Warn("Использование: unban <логин|IP>"); return; }
        var n = await _server.Db.RemoveFromBlacklistAsync(target);
        if (n > 0) Logger.Success($"«{target}» удалён из чёрного списка (записей: {n}).");
        else Logger.Warn($"«{target}» нет в чёрном списке.");
    }

    private async Task PrintBlacklistAsync()
    {
        var list = await _server.Db.GetBlacklistAsync();
        Logger.Plain($"\nЧёрный список ({list.Count}):");
        foreach (var b in list)
            Logger.Plain($"  {b.Id,-4} логин: {b.Login ?? "-",-12} IP: {b.IpAddress ?? "-",-16} {b.AddedAt:dd.MM.yyyy HH:mm}  {b.Reason}");
        Logger.Plain("");
    }

    private async Task PrintUsersAsync()
    {
        var list = await _server.Db.GetUsersAsync();
        Logger.Plain($"\nПользователи ({list.Count}):");
        foreach (var u in list)
            Logger.Plain($"  {u.Id,-4} {u.Login,-15} {u.FullName}");
        Logger.Plain("");
    }

    private async Task AddUserAsync(string? login, string? password)
    {
        if (login == null || string.IsNullOrWhiteSpace(password))
        {
            Logger.Warn("Использование: adduser <логин> <пароль>");
            return;
        }
        if (await _server.Db.AddUserAsync(login, password.Trim(), null))
            Logger.Success($"Пользователь «{login}» добавлен.");
        else
            Logger.Warn($"Пользователь «{login}» уже существует.");
    }

    private void PrintSettings()
    {
        var s = _server.Settings;
        Logger.Plain($"""

          Адрес:                 {s.IpAddress}:{s.Port}
          Лицензий:              {s.MaxLicenses}
          Время до отключения:   {s.SessionLifetimeSeconds} с
          Время подтверждения:   {s.ConfirmTimeoutSeconds} с

        """);
    }

    private static string FormatSpan(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static string StateName(SessionState st) => st switch
    {
        SessionState.Connected => "авторизация",
        SessionState.WaitingConfirm => "ждёт подтверждения",
        SessionState.Active => "активен",
        _ => "отключён"
    };
}
