using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LicenseServer;

/// <summary>
/// Протокол обмена: по TCP передаются JSON-объекты в UTF-8, по одному на строку (разделитель '\n').
/// У каждого сообщения есть поле "type". Подробное описание — в README.md.
/// </summary>
public static class Protocol
{
    // Запросы клиента
    public const string Auth = "AUTH";
    public const string Confirm = "CONFIRM";
    public const string Check = "CHECK";
    public const string Renew = "RENEW";
    public const string Bye = "BYE";

    // Ответы и события сервера
    public const string Hello = "HELLO";
    public const string Code = "CODE";
    public const string Confirmed = "CONFIRMED";
    public const string Status = "STATUS";
    public const string Error = "ERROR";
    public const string Disconnect = "DISCONNECT";

    // Коды ошибок (поле "error" в сообщении ERROR)
    public const string ErrBadRequest = "BAD_REQUEST";
    public const string ErrAuthFailed = "AUTH_FAILED";
    public const string ErrBlacklisted = "BLACKLISTED";
    public const string ErrNoSlots = "NO_SLOTS";
    public const string ErrAlreadyConnected = "ALREADY_CONNECTED";
    public const string ErrNotAuthorized = "NOT_AUTHORIZED";
    public const string ErrInvalidCode = "INVALID_CODE";
    public const string ErrServer = "SERVER_ERROR";

    // Причины отключения (поле "reason" в сообщении DISCONNECT)
    public const string ReasonExpired = "EXPIRED";
    public const string ReasonKicked = "KICKED";
    public const string ReasonBlacklisted = "BLACKLISTED";
    public const string ReasonConfirmTimeout = "CONFIRM_TIMEOUT";
    public const string ReasonShutdown = "SERVER_SHUTDOWN";
    public const string ReasonClientBye = "CLIENT_BYE";
    public const string ReasonConnectionLost = "CONNECTION_LOST";
    public const string ReasonRejected = "REJECTED";

    // Кириллица передаётся как есть (без \uXXXX), чтобы сообщения было удобно читать при отладке.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(JsonObject message) => message.ToJsonString(JsonOptions) + "\n";

    public static JsonObject? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonObject ErrorMessage(string error, string message) => new()
    {
        ["type"] = Error,
        ["error"] = error,
        ["message"] = message
    };

    public static JsonObject DisconnectMessage(string reason, string message) => new()
    {
        ["type"] = Disconnect,
        ["reason"] = reason,
        ["message"] = message
    };

    /// <summary>Формат даты во всех сообщениях: ISO 8601 с часовым поясом.</summary>
    public static string FormatDate(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:sszzz");
}
