using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace OpenVerse.Decker.Internal
{
    public static class Logger
    {
        private const int LogRetentionDays = 7;
        private const string LogDirectoryName = "deckerlogs";
        private const string LogFileNamePrefix = "log-";
        private const string LogFileSearchPattern = LogFileNamePrefix + "*.txt";
        private static StreamWriter? fs = null;
        private static readonly Stopwatch stopwatch;

        static Logger()
        {
            stopwatch = new Stopwatch();
            stopwatch.Start();
            Task.Run(DeleteExpiredLogFiles);
        }

        /// <summary>
        /// Writes an informational log message.
        /// </summary>
        /// <param name="message">
        /// Message template. If args is provided, this value must be compatible with string.Format.
        /// </param>
        /// <param name="args">
        /// Optional format arguments. Pass null or an empty array when the message has no placeholders.
        /// </param>
        /// <param name="callerMemberName">
        /// Automatically supplied by the compiler. Represents the calling method or property name.
        /// </param>
        /// <param name="callerFilePath">
        /// Automatically supplied by the compiler. Represents the source file path of the caller.
        /// </param>
        /// <param name="callerLineNumber">
        /// Automatically supplied by the compiler. Represents the source line number of the call site.
        /// </param>
        /// <remarks>
        /// It formats the message, derives a caller class name from the
        /// source file name, and delegates the final output to InternalLog.
        /// </remarks>
        public static void Log(
            string message,
            bool leaveToFile = false,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            if (message == null)
            {
                message = string.Empty;
            }

            string callerClassName = GetCallerClassName(callerFilePath);

            InternalLog(
                leaveToFile: leaveToFile,
                message: message,
                callerClassName: callerClassName,
                callerMemberName: callerMemberName,
                callerLineNumber: callerLineNumber);


        }

        /// <summary>
        /// Writes an informational log message when debug mode is enabled.
        /// </summary>
        /// <param name="message">
        /// Message template. If args is provided, this value must be compatible with string.Format.
        /// </param>
        /// <param name="args">
        /// Optional format arguments. Pass null or an empty array when the message has no placeholders.
        /// </param>
        /// <param name="callerMemberName">
        /// Automatically supplied by the compiler. Represents the calling method or property name.
        /// </param>
        /// <param name="callerFilePath">
        /// Automatically supplied by the compiler. Represents the source file path of the caller.
        /// </param>
        /// <param name="callerLineNumber">
        /// Automatically supplied by the compiler. Represents the source line number of the call site.
        /// </param>
        /// <remarks>
        /// It formats the message, derives a caller class name from the
        /// source file name, and delegates the final output to InternalLog.
        /// </remarks>
        public static void DebugLog(
            string message,
            bool leaveToFile = false,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
#if DEBUG
            if (message == null)
            {
                message = string.Empty;
            }

            string callerClassName = GetCallerClassName(callerFilePath);

            InternalLog(
                leaveToFile: leaveToFile,
                message: $"[DEBUG] {message}",
                callerClassName: callerClassName,
                callerMemberName: callerMemberName,
                callerLineNumber: callerLineNumber);
#endif
        }

        /// <summary>
        /// Writes the final log line to the trace output.
        /// </summary>
        /// <param name="level">
        /// Log level text such as INFO, WARN, or ERROR. It should be short and uppercase.
        /// </param>
        /// <param name="message">
        /// Already formatted log message.
        /// </param>
        /// <param name="leaveToFile">
        /// Leave the message to a log file.
        /// </param>
        /// <param name="callerClassName">
        /// Caller class name estimated from the caller source file name.
        /// </param>
        /// <param name="callerMemberName">
        /// Caller method or property name.
        /// </param>
        /// <param name="callerLineNumber">
        /// Caller source line number.
        /// </param>
        /// <remarks>
        /// This method returns no value. It builds a single-line log record with timestamp, level,
        /// caller information, and message, then writes it using Trace.WriteLine.
        /// </remarks>
        private static void InternalLog(
            bool leaveToFile,
            string message,
            string callerClassName,
            string callerMemberName,
            int callerLineNumber)
        {
            string timestamp = DateTime.Now.ToString(
                "HH:mm:ss.ff",
                CultureInfo.InvariantCulture);

            var thread = Thread.CurrentThread;

            string logLine =
                $"[{timestamp}] [{thread.ManagedThreadId}/{thread.Name}] [{callerClassName}.{callerMemberName}:{callerLineNumber}] {message}";

            Trace.WriteLine(logLine);

            if (leaveToFile && fs != null)
            {
                CreateLogFileIfNotExists();
                string fileLogLine = $"[{timestamp}] {message}";
                fs.WriteLine(fileLogLine);
                fs.Close();
            }
        }

        /// <summary>
        /// Estimates a caller class name from a source file path.
        /// </summary>
        /// <param name="callerFilePath">
        /// Source file path supplied by CallerFilePath. Empty or null values are allowed.
        /// </param>
        /// <returns>
        /// File name without extension. Returns "Unknown" when the path is missing.
        /// </returns>
        /// <remarks>
        /// This is an estimate. It is accurate when one class is defined per file with a matching file name.
        /// </remarks>
        private static string GetCallerClassName(string callerFilePath)
        {
            if (string.IsNullOrWhiteSpace(callerFilePath))
            {
                return "Unknown";
            }

            return Path.GetFileNameWithoutExtension(callerFilePath);
        }

        /// <summary>
        /// Creates a log file if not exists for the current date.<br/>
        /// The file is named "log-YYYY-MM-DD.txt" and stored in "logs" directory.
        /// </summary>
        private static void CreateLogFileIfNotExists()
        {
            DateTime now = DateTime.Now;
            string logDir = LogDirectoryName;
            string filePath = Path.Combine(logDir, $"log-{now:yyyy-MM-dd}.txt");
            if (fs == null)
            {
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                if (!File.Exists(filePath))
                {
                    fs = File.CreateText(filePath);
                }
                else
                {
                    fs = File.AppendText(filePath);
                }
            }
            else
            {
                fs.Dispose();
                fs = File.AppendText(filePath);
            }
        }

        private static void DeleteExpiredLogFiles()
        {
            bool ShouldDeleteLogFile(string filePath, DateTime cutoffDate)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(LogFileNamePrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                string dateText = fileName.Substring(LogFileNamePrefix.Length);
                DateTime logDate;
                if (!DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out logDate))
                {
                    return false;
                }

                return logDate < cutoffDate;
            }

            try
            {
                if (!Directory.Exists(LogDirectoryName))
                {
                    return;
                }

                DateTime cutoffDate = DateTime.Today.AddDays(-LogRetentionDays);
                foreach (string filePath in Directory.EnumerateFiles(LogDirectoryName, LogFileSearchPattern))
                {
                    if (!ShouldDeleteLogFile(filePath, cutoffDate))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (IOException ex)
                    {
                        // you can use Log(,false)
                        Log("Failed to delete expired log file: " + ex.Message);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // you can use Log(,false)
                        Log("Failed to delete expired log file: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to clean up expired log files: " + ex.Message);
            }
        }

        /// <summary>
        /// Used to get a start timestamp for <c>LogWithTimestamp()</c>
        /// </summary>
        /// <returns></returns>
        public static TimeSpan GetTimestamp()
        {
            return stopwatch.Elapsed;
        }

        /// <summary>
        /// Return elapsed time (ms) since the provided timestamp.
        /// </summary>
        /// <param name="lastTimestamp"></param>
        /// <returns></returns>
        public static long GetElapsedMillsec(TimeSpan lastTimestamp)
        {
            return (long)(stopwatch.Elapsed - lastTimestamp).TotalMilliseconds;
        }

        /// <summary>
        /// Log a message with the elapsed time since the provided timestamp.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="lastTimestamp"></param>
        /// <param name="callerMemberName"></param>
        /// <param name="callerFilePath"></param>
        /// <param name="callerLineNumber"></param>
        public static void LogWithTimestamp(
            string message,
            TimeSpan lastTimestamp,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            Log($"{GetElapsedMillsec(lastTimestamp)} ms: {message}",
                callerLineNumber: callerLineNumber,
                callerMemberName: callerMemberName,
                callerFilePath: callerFilePath);
        }
    }
}
