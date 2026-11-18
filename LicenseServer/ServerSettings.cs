using System.Net;
using System.Text.Json;

namespace LicenseServer;

/// <summary>
/// Настройки сервера. Запрашиваются у пользователя при запуске,
/// последние введённые значения сохраняются в settings.json и предлагаются по умолчанию.
/// </summary>
public class ServerSettings
{
    private const string FileName = "settings.json";

    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5050;

    /// <summary>Сколько уникальных кодов (лицензий) может быть выдано одновременно.</summary>
    public int MaxLicenses { get; set; } = 5;

    /// <summary>Через сколько секунд сервер отключает клиента для обновления кода.</summary>
    public int SessionLifetimeSeconds { get; set; } = 300;

    /// <summary>За сколько секунд клиент должен подтвердить полученный код.</summary>
    public int ConfirmTimeoutSeconds { get; set; } = 30;

    public string ConnectionString { get; set; } =
        "Server=localhost;Database=LicenseManager;Trusted_Connection=True;TrustServerCertificate=True";

    public static ServerSettings Load()
    {
        try
        {
            if (File.Exists(FileName))
                return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FileName)) ?? new ServerSettings();
        }
        catch
        {
            // повреждённый файл настроек — просто берём значения по умолчанию
        }
        return new ServerSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FileName, json);
    }

    /// <summary>Интерактивный ввод настроек. Enter — оставить значение по умолчанию.</summary>
    public static ServerSettings AskUser()
    {
        var s = Load();
        Console.WriteLine("=== Настройка сервера (Enter — значение в скобках) ===");

        s.IpAddress = AskString("IP-адрес сервера", s.IpAddress, v => IPAddress.TryParse(v, out _));
        s.Port = AskInt("Порт сервера", s.Port, 1, 65535);
        s.MaxLicenses = AskInt("Количество уникальных кодов (лицензий)", s.MaxLicenses, 1, 10000);
        s.SessionLifetimeSeconds = AskInt("Время до отключения клиента, сек", s.SessionLifetimeSeconds, 10, 86400);
        s.ConfirmTimeoutSeconds = AskInt("Время подтверждения уникального кода, сек", s.ConfirmTimeoutSeconds, 5, 3600);
        s.ConnectionString = AskString("Строка подключения к MSSQL", s.ConnectionString, v => v.Length > 0);

        s.Save();
        return s;
    }

    private static string AskString(string title, string def, Func<string, bool> isValid)
    {
        while (true)
        {
            Console.Write($"{title} [{def}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) return def;
            if (isValid(input)) return input;
            Console.WriteLine("  Некорректное значение, попробуйте ещё раз.");
        }
    }

    private static int AskInt(string title, int def, int min, int max)
    {
        while (true)
        {
            Console.Write($"{title} [{def}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) return def;
            if (int.TryParse(input, out var v) && v >= min && v <= max) return v;
            Console.WriteLine($"  Введите целое число от {min} до {max}.");
        }
    }
}
