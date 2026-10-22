using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Android-native HTTPS for TAK enrollment via
    /// <c>us.copix.takxr.enroll.TakXrEnrollHttp</c> (HttpsURLConnection),
    /// matching TAK_Beacon. Avoids Mono SslStream keep-alive stalls on signClient/v2.
    /// </summary>
    public static class TakAndroidHttps
    {
        const string JavaClass = "us.copix.takxr.enroll.TakXrEnrollHttp";

        public struct Result
        {
            public int Status;
            public string Body;
            public string Error;
            public int ElapsedMs;
        }

        public static bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try
                {
                    using (var c = new AndroidJavaClass(JavaClass))
                        return c != null;
                }
                catch
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

        public static Result Request(
            string url, string method, string username, string password,
            byte[] body, string contentType, string accept,
            int connectTimeoutMs, int readTimeoutMs)
        {
            var sw = Stopwatch.StartNew();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJNI.AttachCurrentThread();
            }
            catch (Exception ex)
            {
                return Fail(sw, "JNI attach: " + ex.Message);
            }

            try
            {
                using (var cls = new AndroidJavaClass(JavaClass))
                {
                    // Unity marshals C# byte[] → Java byte[]; empty means no body (GET).
                    byte[] jBody = body != null && body.Length > 0 ? body : Array.Empty<byte>();

                    string raw = cls.CallStatic<string>(
                        "request",
                        url,
                        method ?? "GET",
                        username ?? "",
                        password ?? "",
                        jBody,
                        contentType ?? "text/plain; charset=utf-8",
                        accept ?? "*/*",
                        connectTimeoutMs,
                        readTimeoutMs);

                    return Parse(raw, sw);
                }
            }
            catch (Exception ex)
            {
                return Fail(sw, ex.GetType().Name + ": " + ex.Message);
            }
            // Leave JNI attached — this runs on a short-lived enroll worker thread.
#else
            return Fail(sw, "Android HTTPS not available");
#endif
        }

        public static string ProbeTcp(string host, int port, int timeoutMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJNI.AttachCurrentThread();
                using (var cls = new AndroidJavaClass(JavaClass))
                {
                    return cls.CallStatic<string>("probeTcp", host, port, timeoutMs) ?? "fail: null";
                }
            }
            catch (Exception ex)
            {
                return "fail: " + ex.GetType().Name + ": " + ex.Message;
            }
#else
            return ProbeTcpManaged(host, port, timeoutMs);
#endif
        }

        static string ProbeTcpManaged(string host, int port, int timeoutMs)
        {
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                        return "fail: connect timeout";
                    tcp.EndConnect(ar);
                    return "ok";
                }
            }
            catch (Exception ex)
            {
                return "fail: " + ex.Message;
            }
        }

        static Result Parse(string raw, Stopwatch sw)
        {
            int ms = (int)sw.ElapsedMilliseconds;
            if (string.IsNullOrEmpty(raw))
                return Fail(sw, "empty Android HTTPS response");

            int nl = raw.IndexOf('\n');
            string head = nl >= 0 ? raw.Substring(0, nl) : raw;
            string body = nl >= 0 ? raw.Substring(nl + 1) : "";

            if (string.Equals(head, "ERR", StringComparison.OrdinalIgnoreCase))
                return new Result { Status = 0, Error = body, Body = "", ElapsedMs = ms };

            if (!int.TryParse(head.Trim(), out int code))
                return Fail(sw, "bad Android HTTPS envelope: " + Trunc(raw, 120));

            return new Result { Status = code, Body = body, ElapsedMs = ms };
        }

        static Result Fail(Stopwatch sw, string error) =>
            new Result { Status = 0, Error = error, ElapsedMs = (int)sw.ElapsedMilliseconds };

        static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
    }
}
