using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LicenseServer;

public enum SessionState
{
    /// <summary>Подключился, ещё не прошёл аутентификацию.</summary>
    Connected,
    /// <summary>Получил код, ждём от клиента CONFIRM.</summary>
    WaitingConfirm,
    /// <summary>Код подтверждён, лицензия активна.</summary>
    Active,
    /// <summary>Отключается / отключён.</summary>
    Closed
}

/// <summary>Одно TCP-подключение клиента и связанная с ним лицензия.</summary>
public class ClientSession
{
    private static int _nextId;

    private readonly TcpClient _tcp;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public int Id { get; }
    public string Ip { get; }
    public int Port { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;

    public SessionState State { get; set; } = SessionState.Connected;
    public int? UserId { get; set; }
    public string? Login { get; set; }
    public string? Code { get; set; }
    public DateTime? LicensedAt { get; set; }
    public DateTime? CodeIssuedAt { get; set; }
    public int? LogId { get; set; }
    public int FailedAuthAttempts { get; set; }

    /// <summary>Причина, по которой сервер закрыл сессию (для журнала).</summary>
    public string? CloseReason { get; private set; }

    public CancellationTokenSource Cts { get; } = new();
    public StreamReader Reader { get; }

    /// <summary>Есть ли у клиента выданная лицензия (занимает ли он место).</summary>
    public bool HasLicense => Code != null && State is SessionState.WaitingConfirm or SessionState.Active;

    public TimeSpan Duration => DateTime.Now - ConnectedAt;

    public ClientSession(TcpClient tcp)
    {
        _tcp = tcp;
        Id = Interlocked.Increment(ref _nextId);

        var ep = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        Ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4().ToString() : ep.Address.ToString();
        Port = ep.Port;

        var stream = tcp.GetStream();
        var utf8 = new UTF8Encoding(false);
        Reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task SendAsync(JsonObject message)
    {
        if (State == SessionState.Closed && message["type"]?.GetValue<string>() != Protocol.Disconnect) return;

        await _sendLock.WaitAsync();
        try
        {
            await _writer.WriteAsync(Protocol.Serialize(message));
        }
        catch (Exception) when (!_tcp.Connected)
        {
            // клиент уже отвалился — ничего страшного
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Отправляет клиенту DISCONNECT и закрывает соединение. Повторные вызовы игнорируются.</summary>
    public async Task CloseAsync(string reason, string message, bool notifyClient = true)
    {
        lock (this)
        {
            if (State == SessionState.Closed) return;
            State = SessionState.Closed;
            CloseReason = reason;
        }

        if (notifyClient)
            await SendAsync(Protocol.DisconnectMessage(reason, message));

        Cts.Cancel();
        try { _tcp.Close(); } catch { /* ignore */ }
    }

    public string Describe() => Login != null ? $"#{Id} {Login} ({Ip}:{Port})" : $"#{Id} {Ip}:{Port}";
}
