using System;
using System.IO;
using Rhino;

namespace CoRhino
{
    internal static class Log
    {
        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "CoRhino.log");

        static readonly object Gate = new();

        /// <summary>
        /// Diagnostic trace: appended to the file log only, never shown on the
        /// Rhino command line. Use <see cref="User"/> for the few states the user
        /// must see there.
        /// </summary>
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
        }

        /// <summary>
        /// User-facing message: Rhino command line + file log. Reserve for
        /// user-impacting failures that have no dialog or panel UI of their own.
        /// </summary>
        public static void User(string message)
        {
            RhinoApp.WriteLine(message);
            Write(message);
        }
    }
}
