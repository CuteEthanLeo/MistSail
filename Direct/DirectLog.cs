using System;
using System.IO;

namespace Sheas_Cealer.Direct;

internal static class DirectLog
{
    private static readonly object Gate = new();
    internal static string LogPath => PathFor("direct.log");

    internal static string PathFor(string fileName) => Path.Combine(AppContext.BaseDirectory, "logs", fileName);

    internal static void Write(string message) => Write("direct.log", message);

    internal static void Write(string fileName, string message)
    {
        string path = PathFor(fileName);
        // Do not log URLs, request headers, cookies or TLS payloads.
        lock (Gate)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}
