using System.Text;

namespace RonPatcher.Diagnostics;

internal static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RONPatcher");

    public static string FilePath => Path.Combine(DirectoryPath, "startup.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append("  ")
                    .AppendLine(message);
                if (exception is not null)
                    builder.AppendLine(exception.ToString());
                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Startup diagnostics must never become another startup failure.
        }
    }
}

