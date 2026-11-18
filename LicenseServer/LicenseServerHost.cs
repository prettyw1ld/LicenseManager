using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LicenseServer;

/// <summary>
/// TCP-сервер менеджера лицензий: принимает клиентов, проверяет логин/пароль и чёрный список,
/// выдаёт уникальные коды, следит за временем подтверждения и временем жизни лицензии.
/// </summary>
public class LicenseServerHost
{
    private const int MaxAuthAttempts = 3;
    private const int AuthTimeoutSeconds = 60;
    private const int MaxLineLength = 4096;

    private readonly ServerSettings _settings;
    private readonly Database _db;
    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private readonly object _licenseLock = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public LicenseServerHost(ServerSettings settings, Database db)
    {
        _settings = settings;
        _db = db;
    }

    public ServerSettings Settings => _settings;
    public Database Db => _db;

    public IReadOnlyList<ClientSession> Sessions =>
        _sessions.Values.Where(s => s.State != SessionState.Closed).OrderBy(s => s.Id).ToList();

    public int UsedLicenses => _sessions.Values.Count(s => s.HasLicense);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Parse(_settings.IpAddress), _settings.Port);
        _listener.Start();
        Logger.Success($"Сервер запущен на {_settings.IpAddress}:{_settings.Port}. " +
                       $"Лицензий: {_settings.MaxLicenses}, время жизни: {_settings.SessionLifetimeSeconds} с, " +
                       $"подтверждение: {_settings.ConfirmTimeoutSeconds} с.");

        _ = AcceptLoopAsync(_cts.Token);
        _ = WatchdogLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var s in _sessions.Values)
            await CloseSessionAsync(s, Protocol.ReasonShutdown, "Сервер остановлен.");
        Logger.Warn("Сервер остановлен.");
    }

    // ================== Приём подключений ==================

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Logger.Error($"Ошибка приёма подключения: {ex.Message}");
                continue;
            }

            var session = new ClientSession(tcp);
            _sessions[session.Id] = session;
            Logger.Info($"Новое подключение {session.Describe()}");
            _ = HandleClientAsync(session);
        }
    }

    private async Task HandleClientAsync(ClientSession session)
    {
        try
        {
            await session.SendAsync(new JsonObject
            {
                ["type"] = Protocol.Hello,
                ["server"] = "LicenseServer",
                ["version"] = 1,
                ["authRequired"] = true,
                ["sessionId"] = session.Id,
                ["clientIp"] = session.Ip,
                ["clientPort"] = session.Port,
                ["connectedAt"] = Protocol.FormatDate(session.ConnectedAt)
            });

            while (!session.Cts.IsCancellationRequested)
            {
                var line = await session.Reader.ReadLineAsync(session.Cts.Token);
                if (line == null) break; // клиент закрыл соединение
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxLineLength)
                {
                    await session.SendAsync(Protocol.ErrorMessage(Protocol.ErrBadRequest, "Слишком длинное сообщение."));
                    continue;
                }

                var msg = Protocol.TryParse(line);
                if (msg == null)
                {
                    await session.SendAsync(Protocol.ErrorMessage(Protocol.ErrBadRequest, "Ожидается JSON-объект."));
                    continue;
                }

                await HandleMessageAsync(session, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка обработки клиента {session.Describe()}: {ex.Message}");
        }
        finally
        {
            await CloseSessionAsync(session, Protocol.ReasonConnectionLost, "Соединение закрыто.", notifyClient: false);
        }
    }

    // ================== Обработка запросов ==================

    private async Task HandleMessageAsync(ClientSession s, JsonObject msg)
    {
        var type = msg["type"]?.GetValue<string>()?.ToUpperInvariant();
        switch (type)
        {
            case Protocol.Auth: await HandleAuthAsync(s, msg); break;
            case Protocol.Confirm: await HandleConfirmAsync(s, msg); break;
            case Protocol.Check: await HandleCheckAsync(s, msg); break;
            case Protocol.Renew: await HandleRenewAsync(s, msg); break;
            case Protocol.Bye:
                Logger.Info($"Клиент {s.Describe()} отключился сам.");
                await CloseSessionAsync(s, Protocol.ReasonClientBye, "До свидания!");
                break;
            default:
                await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrBadRequest, $"Неизвестный тип сообщения: {type ?? "(нет)"}"));
                break;
        }
    }

    private async Task HandleAuthAsync(ClientSession s, JsonObject msg)
    {
        if (s.State != SessionState.Connected)
        {
            await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrBadRequest, "Вы уже авторизованы."));
            return;
        }

        var login = msg["login"]?.GetValue<string>()?.Trim();
        var password = msg["password"]?.GetValue<string>();
        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
        {
            await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrBadRequest, "Укажите login и password."));
            return;
        }

        try
        {
            // 1. Чёрный список (по IP и по логину)
            var ban = await _db.FindInBlacklistAsync(login, s.Ip);
            if (ban != null)
            {
                Logger.Warn($"Отказ {s.Describe()} [{login}]: в чёрном списке ({ban.Reason ?? "без причины"}).");
                await RejectAsync(s, Protocol.ErrBlacklisted,
                    $"Доступ запрещён: вы в чёрном списке. Причина: {ban.Reason ?? "не указана"}.");
                return;
            }

            // 2. Логин и пароль из БД
            var user = await _db.AuthenticateAsync(login, password);
            if (user == null)
            {
                s.FailedAuthAttempts++;
                Logger.Warn($"Неверный логин/пароль от {s.Describe()} [{login}], попытка {s.FailedAuthAttempts}/{MaxAuthAttempts}.");
                if (s.FailedAuthAttempts >= MaxAuthAttempts)
                    await RejectAsync(s, Protocol.ErrAuthFailed, "Превышено число попыток входа.");
                else
                    await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrAuthFailed,
                        $"Неверный логин или пароль. Осталось попыток: {MaxAuthAttempts - s.FailedAuthAttempts}."));
                return;
            }

            // 3. Место для лицензии + выдача уникального кода (атомарно)
            string? code;
            string? error = null;
            lock (_licenseLock)
            {
                if (_sessions.Values.Any(o => o.Id != s.Id && o.HasLicense &&
                                              string.Equals(o.Login, user.Login, StringComparison.OrdinalIgnoreCase)))
                {
                    error = Protocol.ErrAlreadyConnected;
                    code = null;
                }
                else if (UsedLicenses >= _settings.MaxLicenses)
                {
                    error = Protocol.ErrNoSlots;
                    code = null;
                }
                else
                {
                    code = GenerateUniqueCode();
                    s.UserId = user.Id;
                    s.Login = user.Login;
                    s.Code = code;
                    s.LicensedAt = DateTime.Now;
                    s.CodeIssuedAt = DateTime.Now;
                    s.State = SessionState.WaitingConfirm;
                }
            }

            if (error == Protocol.ErrAlreadyConnected)
            {
                Logger.Warn($"Отказ {s.Describe()} [{login}]: пользователь уже подключён.");
                await RejectAsync(s, error, "Этот пользователь уже получил лицензию с другого подключения.");
                return;
            }
            if (error == Protocol.ErrNoSlots)
            {
                Logger.Warn($"Отказ {s.Describe()} [{login}]: нет свободных лицензий ({UsedLicenses}/{_settings.MaxLicenses}).");
                await RejectAsync(s, error,
                    $"Нет свободного места для лицензирования (занято {_settings.MaxLicenses} из {_settings.MaxLicenses}).");
                return;
            }

            s.LogId = await TryDbAsync(() => _db.LogConnectAsync(s.UserId, s.Login, s.Ip, s.Port, s.Code, s.ConnectedAt));
            Logger.Success($"Выдан код {code} клиенту {s.Describe()}. Занято лицензий: {UsedLicenses}/{_settings.MaxLicenses}.");
            await s.SendAsync(BuildCodeMessage(s));
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка аутентификации {s.Describe()}: {ex.Message}");
            await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrServer, "Внутренняя ошибка сервера (БД недоступна?)."));
        }
    }

    private async Task HandleConfirmAsync(ClientSession s, JsonObject msg)
    {
        if (!await RequireCodeAsync(s, msg)) return;

        if (s.State == SessionState.WaitingConfirm)
        {
            s.State = SessionState.Active;
            Logger.Info($"Код {s.Code} подтверждён клиентом {s.Describe()}.");
        }
        await s.SendAsync(new JsonObject
        {
            ["type"] = Protocol.Confirmed,
            ["code"] = s.Code,
            ["expiresInSeconds"] = SecondsLeft(s)
        });
    }

    private async Task HandleCheckAsync(ClientSession s, JsonObject msg)
    {
        if (!await RequireCodeAsync(s, msg)) return;

        BlacklistEntry? ban = null;
        try { ban = await _db.FindInBlacklistAsync(s.Login, s.Ip); }
        catch (Exception ex) { Logger.Error($"БД: {ex.Message}"); }

        var status = BuildStatusMessage(s);
        status["blocked"] = ban != null;
        await s.SendAsync(status);

        if (ban != null)
        {
            Logger.Warn($"Клиент {s.Describe()} в чёрном списке — отключаем.");
            await CloseSessionAsync(s, Protocol.ReasonBlacklisted, $"Вы добавлены в чёрный список: {ban.Reason ?? "без причины"}.");
        }
    }

    private async Task HandleRenewAsync(ClientSession s, JsonObject msg)
    {
        if (!await RequireCodeAsync(s, msg)) return;

        string oldCode = s.Code!;
        lock (_licenseLock)
        {
            s.Code = GenerateUniqueCode();
            s.CodeIssuedAt = DateTime.Now;
            s.State = SessionState.WaitingConfirm;
        }
        if (s.LogId is int logId) await TryDbAsync(() => _db.LogCodeChangedAsync(logId, s.Code!));

        Logger.Info($"Код клиента {s.Describe()} обновлён: {oldCode} -> {s.Code}.");
        await s.SendAsync(BuildCodeMessage(s));
    }

    /// <summary>Проверяет, что клиент авторизован и прислал свой текущий код.</summary>
    private async Task<bool> RequireCodeAsync(ClientSession s, JsonObject msg)
    {
        if (!s.HasLicense)
        {
            await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrNotAuthorized, "Сначала выполните AUTH."));
            return false;
        }
        var code = msg["code"]?.GetValue<string>();
        if (!string.Equals(code, s.Code, StringComparison.OrdinalIgnoreCase))
        {
            await s.SendAsync(Protocol.ErrorMessage(Protocol.ErrInvalidCode, "Код не совпадает с выданным сервером."));
            return false;
        }
        return true;
    }

    private JsonObject BuildCodeMessage(ClientSession s) => new()
    {
        ["type"] = Protocol.Code,
        ["code"] = s.Code,
        ["login"] = s.Login,
        ["clientIp"] = s.Ip,
        ["clientPort"] = s.Port,
        ["connectedAt"] = Protocol.FormatDate(s.ConnectedAt),
        ["confirmTimeoutSeconds"] = _settings.ConfirmTimeoutSeconds,
        ["expiresInSeconds"] = SecondsLeft(s)
    };

    private JsonObject BuildStatusMessage(ClientSession s) => new()
    {
        ["type"] = Protocol.Status,
        ["code"] = s.Code,
        ["login"] = s.Login,
        ["clientIp"] = s.Ip,
        ["clientPort"] = s.Port,
        ["connectedAt"] = Protocol.FormatDate(s.ConnectedAt),
        ["durationSeconds"] = (int)s.Duration.TotalSeconds,
        ["expiresInSeconds"] = SecondsLeft(s),
        ["confirmed"] = s.State == SessionState.Active,
        ["blocked"] = false
    };

    private int SecondsLeft(ClientSession s)
    {
        if (s.LicensedAt == null) return 0;
        var left = s.LicensedAt.Value.AddSeconds(_settings.SessionLifetimeSeconds) - DateTime.Now;
        return Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
    }

    /// <summary>Код вида XXXX-XXXX-XXXX-XXXX (A-Z, 2-9 без похожих символов), уникальный среди активных.</summary>
    private string GenerateUniqueCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var chars = new char[19];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = (i + 1) % 5 == 0 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            var code = new string(chars);
            if (!_sessions.Values.Any(x => x.Code == code)) return code;
        }
    }

    private async Task RejectAsync(ClientSession s, string error, string message)
    {
        await s.SendAsync(Protocol.ErrorMessage(error, message));
        await CloseSessionAsync(s, Protocol.ReasonRejected, message);
    }

    // ================== Таймеры ==================

    /// <summary>Раз в секунду проверяет время подтверждения кода, время жизни лицензии и таймаут авторизации.</summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.Now;
                foreach (var s in _sessions.Values)
                {
                    if (s.State == SessionState.Connected && (now - s.ConnectedAt).TotalSeconds > AuthTimeoutSeconds)
                    {
                        Logger.Warn($"Клиент {s.Describe()} не авторизовался за {AuthTimeoutSeconds} с — отключаем.");
                        await CloseSessionAsync(s, Protocol.ReasonRejected, "Время на авторизацию истекло.");
                    }
                    else if (s.State == SessionState.WaitingConfirm && s.CodeIssuedAt != null &&
                             (now - s.CodeIssuedAt.Value).TotalSeconds > _settings.ConfirmTimeoutSeconds)
                    {
                        Logger.Warn($"Клиент {s.Describe()} не подтвердил код {s.Code} — отключаем.");
                        await CloseSessionAsync(s, Protocol.ReasonConfirmTimeout, "Код не подтверждён вовремя.");
                    }
                    else if (s.HasLicense && SecondsLeft(s) <= 0)
                    {
                        Logger.Info($"Время лицензии клиента {s.Describe()} истекло — отключаем для обновления кода.");
                        await CloseSessionAsync(s, Protocol.ReasonExpired,
                            "Время действия кода истекло. Подключитесь заново, чтобы получить новый код.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ================== Команды администратора ==================

    public ClientSession? FindSession(string idOrCode)
    {
        if (int.TryParse(idOrCode.TrimStart('#'), out var id) && _sessions.TryGetValue(id, out var byId) &&
            byId.State != SessionState.Closed)
            return byId;
        return Sessions.FirstOrDefault(s => string.Equals(s.Code, idOrCode, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(s.Login, idOrCode, StringComparison.OrdinalIgnoreCase));
    }

    public Task KickAsync(ClientSession s) =>
        CloseSessionAsync(s, Protocol.ReasonKicked, "Вы отключены администратором сервера.");

    /// <summary>Добавляет логин клиента в чёрный список и отключает его.</summary>
    public async Task BanSessionAsync(ClientSession s, string? reason)
    {
        await _db.AddToBlacklistAsync(s.Login, s.Login == null ? s.Ip : null, reason);
        await CloseSessionAsync(s, Protocol.ReasonBlacklisted, $"Вы добавлены в чёрный список: {reason ?? "без причины"}.");
    }

    /// <summary>Добавляет логин или IP в чёрный список и отключает все подходящие подключения.</summary>
    public async Task<int> BanAsync(string loginOrIp, string? reason)
    {
        bool isIp = IPAddress.TryParse(loginOrIp, out _);
        await _db.AddToBlacklistAsync(isIp ? null : loginOrIp, isIp ? loginOrIp : null, reason);

        var targets = Sessions.Where(s => isIp
            ? s.Ip == loginOrIp
            : string.Equals(s.Login, loginOrIp, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in targets)
            await CloseSessionAsync(s, Protocol.ReasonBlacklisted, $"Вы добавлены в чёрный список: {reason ?? "без причины"}.");
        return targets.Count;
    }

    private async Task CloseSessionAsync(ClientSession s, string reason, string message, bool notifyClient = true)
    {
        if (s.State == SessionState.Closed)
        {
            _sessions.TryRemove(s.Id, out _);
            return;
        }

        await s.CloseAsync(reason, message, notifyClient);
        _sessions.TryRemove(s.Id, out _);

        if (s.LogId is int logId) await TryDbAsync(() => _db.LogDisconnectAsync(logId, reason));
        if (reason != Protocol.ReasonClientBye)
            Logger.Info($"Клиент {s.Describe()} отключён ({reason}). Занято лицензий: {UsedLicenses}/{_settings.MaxLicenses}.");
    }

    private static async Task<T?> TryDbAsync<T>(Func<Task<T>> action) where T : struct
    {
        try { return await action(); }
        catch (Exception ex) { Logger.Error($"БД: {ex.Message}"); return null; }
    }

    private static async Task TryDbAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Logger.Error($"БД: {ex.Message}"); }
    }
}
