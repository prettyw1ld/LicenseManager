namespace LicenseServer;

/// <summary>Потокобезопасный цветной вывод в консоль.</summary>
public static class Logger
{
    private static readonly object Sync = new();

    public static void Info(string text) => Write(text, ConsoleColor.Gray);
    public static void Success(string text) => Write(text, ConsoleColor.Green);
    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);
    public static void Error(string text) => Write(text, ConsoleColor.Red);

    public static void Write(string text, ConsoleColor color)
    {
        lock (Sync)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ForegroundColor = old;
        }
    }

    /// <summary>Вывод без отметки времени (таблицы, справка).</summary>
    public static void Plain(string text)
    {
        lock (Sync) Console.WriteLine(text);
    }
}
