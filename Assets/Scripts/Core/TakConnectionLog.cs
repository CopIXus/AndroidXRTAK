using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Ring buffer of recent TAK connection events for in-headset diagnostics
    /// and adb logcat correlation.
    /// </summary>
    public static class TakConnectionLog
    {
        public const int Capacity = 40;

        public struct Entry
        {
            public double Time;
            public string Level;
            public string ServerId;
            public string Host;
            public string Message;
        }

        static readonly object Gate = new object();
        static readonly Entry[] Buf = new Entry[Capacity];
        static int _next;
        static int _count;
        static string _lastError;
        static string _logPath;
        static string _externalLogPath;

        /// <summary>On-device log. Pull with adb from this path.</summary>
        public static string LogPath
        {
            get
            {
                EnsureFile();
                return _logPath;
            }
        }

        public static string LastError
        {
            get { lock (Gate) return _lastError; }
        }

        public static void Info(string serverId, string host, string message) =>
            Add("I", serverId, host, message);

        public static void Warn(string serverId, string host, string message) =>
            Add("W", serverId, host, message);

        public static void Error(string serverId, string host, string message) =>
            Add("E", serverId, host, message);

        static void Add(string level, string serverId, string host, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var e = new Entry
            {
                Time = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                Level = level,
                ServerId = serverId ?? "",
                Host = host ?? "",
                Message = message,
            };
            lock (Gate)
            {
                Buf[_next] = e;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
                if (level == "E" || level == "W")
                    _lastError = FormatOne(e);
            }

            string line = $"[TakConn][{level}] {host}: {message}";
            if (level == "E") Debug.LogError(line);
            else if (level == "W") Debug.LogWarning(line);
            else Debug.Log(line);
            AppendFile(line);
        }

        /// <summary>Create the diag file on the main thread before background work writes it.</summary>
        public static void EnsureFile()
        {
            if (!string.IsNullOrEmpty(_logPath)) return;
            try
            {
                _logPath = Path.Combine(Application.persistentDataPath, "takxr-diag.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? Application.persistentDataPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakConn] diag path: " + ex.Message);
            }
            try
            {
                string ext = "/sdcard/takxr";
                Directory.CreateDirectory(ext);
                _externalLogPath = Path.Combine(ext, "takxr-diag.log");
            }
            catch
            {
                _externalLogPath = null;
            }
        }

        static void AppendFile(string line)
        {
            EnsureFile();
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string row = stamp + " " + line + "\n";
            WriteRow(_logPath, row);
            if (!string.IsNullOrEmpty(_externalLogPath))
                WriteRow(_externalLogPath, row);
        }

        static void WriteRow(string path, string row)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 400_000)
                {
                    string keep = File.ReadAllText(path);
                    if (keep.Length > 180_000)
                        keep = keep.Substring(keep.Length - 180_000);
                    File.WriteAllText(path, keep);
                }
                File.AppendAllText(path, row);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakConn] diag write " + path + ": " + ex.Message);
            }
        }

        public static List<Entry> Snapshot(int max = 20)
        {
            var list = new List<Entry>(max);
            lock (Gate)
            {
                int n = Mathf.Min(max, _count);
                for (int i = 0; i < n; i++)
                {
                    int idx = (_next - 1 - i + Capacity * 2) % Capacity;
                    list.Add(Buf[idx]);
                }
            }
            return list;
        }

        public static string FormatRecent(int max = 8)
        {
            var sb = new StringBuilder();
            foreach (var e in Snapshot(max))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(FormatOne(e));
            }
            return sb.ToString();
        }

        static string FormatOne(Entry e)
        {
            string host = string.IsNullOrEmpty(e.Host) ? "?" : e.Host;
            return $"{e.Level} {host} — {e.Message}";
        }
    }
}
