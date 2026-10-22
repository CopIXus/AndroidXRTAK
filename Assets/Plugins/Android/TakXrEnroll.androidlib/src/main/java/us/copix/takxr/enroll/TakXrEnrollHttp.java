package us.copix.takxr.enroll;

import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;

import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * TAK Server enrollment HTTPS — same stack as ATAK / TAK_Beacon
 * ({@link HttpsURLConnection}). Unity Mono SslStream often waits until the
 * server closes keep-alive after signClient/v2; Android returns as soon as the
 * Content-Length body is complete (usually a few seconds).
 */
public final class TakXrEnrollHttp {
    private static final int MAX_BODY = 512 * 1024;
    private static volatile SSLSocketFactory sTrustAllFactory;
    private static final HostnameVerifier sAllowAll = (hostname, session) -> true;

    private TakXrEnrollHttp() {}

    /**
     * @return "HTTP_STATUS\\nBODY" on success, or "ERR\\nmessage" on failure.
     */
    public static String request(
            String url,
            String method,
            String username,
            String password,
            byte[] body,
            String contentType,
            String accept,
            int connectTimeoutMs,
            int readTimeoutMs) {
        HttpsURLConnection conn = null;
        try {
            conn = (HttpsURLConnection) new URL(url).openConnection();
            conn.setSSLSocketFactory(trustAllFactory());
            conn.setHostnameVerifier(sAllowAll);
            conn.setRequestMethod(method == null ? "GET" : method);
            conn.setConnectTimeout(Math.max(1000, connectTimeoutMs));
            conn.setReadTimeout(Math.max(1000, readTimeoutMs));
            conn.setInstanceFollowRedirects(false);
            conn.setUseCaches(false);
            // Avoid Mono-style keep-alive stall: disconnect after response.
            conn.setRequestProperty("Connection", "close");

            String token = Base64.encodeToString(
                    (username + ":" + password).getBytes(StandardCharsets.UTF_8),
                    Base64.NO_WRAP);
            conn.setRequestProperty("Authorization", "Basic " + token);
            if (accept != null && accept.length() > 0) {
                conn.setRequestProperty("Accept", accept);
            }

            if (body != null && body.length > 0) {
                conn.setDoOutput(true);
                conn.setRequestProperty(
                        "Content-Type",
                        contentType != null && contentType.length() > 0
                                ? contentType
                                : "text/plain; charset=utf-8");
                conn.setFixedLengthStreamingMode(body.length);
                OutputStream os = conn.getOutputStream();
                try {
                    os.write(body);
                    os.flush();
                } finally {
                    os.close();
                }
            }

            int code = conn.getResponseCode();
            InputStream in = code >= 400 ? conn.getErrorStream() : conn.getInputStream();
            String text = in == null ? "" : readUtf8(in);
            return code + "\n" + text;
        } catch (Exception ex) {
            String msg = ex.getMessage();
            if (msg == null || msg.length() == 0) msg = ex.getClass().getSimpleName();
            return "ERR\n" + msg;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    /**
     * TCP reachability probe for enrollment / CoT / Marti ports.
     * @return "ok" or "fail: reason"
     */
    public static String probeTcp(String host, int port, int timeoutMs) {
        Socket sock = null;
        try {
            sock = new Socket();
            sock.connect(new InetSocketAddress(host, port), Math.max(500, timeoutMs));
            return "ok";
        } catch (Exception ex) {
            String msg = ex.getMessage();
            if (msg == null || msg.length() == 0) msg = ex.getClass().getSimpleName();
            return "fail: " + msg;
        } finally {
            if (sock != null) {
                try { sock.close(); } catch (Exception ignored) {}
            }
        }
    }

    private static SSLSocketFactory trustAllFactory() throws Exception {
        SSLSocketFactory existing = sTrustAllFactory;
        if (existing != null) return existing;
        synchronized (TakXrEnrollHttp.class) {
            if (sTrustAllFactory != null) return sTrustAllFactory;
            TrustManager[] trustAll = new TrustManager[] {
                    new X509TrustManager() {
                        public void checkClientTrusted(X509Certificate[] chain, String authType) {}
                        public void checkServerTrusted(X509Certificate[] chain, String authType) {}
                        public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
                    }
            };
            SSLContext ctx = SSLContext.getInstance("TLS");
            ctx.init(null, trustAll, new SecureRandom());
            sTrustAllFactory = ctx.getSocketFactory();
            return sTrustAllFactory;
        }
    }

    private static String readUtf8(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) >= 0) {
            out.write(buf, 0, n);
            if (out.size() > MAX_BODY) throw new IllegalStateException("Response too large");
        }
        return out.toString("UTF-8");
    }
}
