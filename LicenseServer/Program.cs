using System.Text;
using LicenseServer;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
Console.Title = "Менеджер лицензий — сервер";

var settings = ServerSettings.AskUser();
var db = new Database(settings.ConnectionString);

Logger.Info("Проверка подключения к базе данных...");
if (!await db.TestConnectionAsync())
{
    Logger.Error("Сервер не может работать без БД. Проверьте строку подключения и выполните Database/LicenseManager.sql.");
    Console.WriteLine("Нажмите любую клавишу для выхода...");
    Console.ReadKey();
    return;
}
Logger.Success("База данных доступна.");

var server = new LicenseServerHost(settings, db);
try
{
    server.Start();
}
catch (Exception ex)
{
    Logger.Error($"Не удалось запустить сервер: {ex.Message}");
    return;
}

var commands = new ServerCommands(server);
commands.PrintHelp();

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
    if (!await commands.ExecuteAsync(line)) break;
}

await server.StopAsync();
