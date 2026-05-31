using System;
using System.IO;
using Rhino;

namespace CoRhino
{
    internal static class Log
    {
        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "CoRhino.log");

        static readonly object Gate = new();

        public static void Write(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Claude/Log] Write failed: {ex.GetType().Name}: {ex.Message} path={LogPath}");
            }
            RhinoApp.WriteLine(message);
        }
    }
}
