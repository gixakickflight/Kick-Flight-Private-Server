package jp.kickflight.push;

import android.util.Log;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/**
 * Sends the FCM registration token + KickFlight user id to our own server (POST <base>/push/register).
 * Called from smali hooks injected at the start of io.repro.android.Repro.setPushRegistrationID / setUserID
 * (scripts/patch-firebase-push.py). The server base URL comes from manifest meta-data "kf.push_url".
 * Never throws; never logs the full token.
 */
public final class PushRegistrar {
    private static final String TAG = "KFPUSH";
    private static final Object LOCK = new Object();
    private static String token = "";
    private static String userId = "";
    private static String lastSent = "";
    private static String pending = "";
    private static String baseUrl = null;

    private PushRegistrar() {}

    public static void onToken(String t) {
        try {
            synchronized (LOCK) { token = t == null ? "" : t.trim(); }
            maybeSend();
        } catch (Throwable e) { Log.w(TAG, "onToken failed: " + e); }
    }

    public static void onUserId(String u) {
        try {
            synchronized (LOCK) { userId = u == null ? "" : u.trim(); }
            maybeSend();
        } catch (Throwable e) { Log.w(TAG, "onUserId failed: " + e); }
    }

    private static void maybeSend() {
        final String t, u, key;
        synchronized (LOCK) {
            t = token; u = userId;
            if (t.isEmpty() || u.isEmpty()) return;
            key = u + "\n" + t;
            if (key.equals(lastSent) || key.equals(pending)) return;
            pending = key;
        }
        Thread th = new Thread(new Runnable() {
            public void run() { send(u, t, key); }
        }, "kf-push-register");
        th.setDaemon(true);
        th.start();
    }

    private static void send(String u, String t, String key) {
        boolean ok = false;
        try {
            String base = resolveBase();
            if (base == null) { Log.w(TAG, "no kf.push_url"); return; }
            String body = "{\"userId\":\"" + esc(u) + "\",\"token\":\"" + esc(t) + "\",\"platform\":\"android\"}";
            long[] backoff = {0, 2000, 5000, 10000};
            for (int i = 0; i < backoff.length && !ok; i++) {
                if (backoff[i] > 0) { try { Thread.sleep(backoff[i]); } catch (InterruptedException ie) { break; } }
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(base + "/push/register").openConnection();
                    c.setConnectTimeout(10000);
                    c.setReadTimeout(10000);
                    c.setRequestMethod("POST");
                    c.setDoOutput(true);
                    c.setRequestProperty("Content-Type", "application/json");
                    byte[] b = body.getBytes("UTF-8");
                    c.setFixedLengthStreamingMode(b.length);
                    OutputStream os = c.getOutputStream();
                    os.write(b);
                    os.close();
                    int code = c.getResponseCode();
                    ok = code >= 200 && code < 300;
                    Log.w(TAG, "register attempt " + (i + 1) + " http " + code + " token=" + mask(t));
                } catch (Throwable e) {
                    Log.w(TAG, "register attempt " + (i + 1) + " failed: " + e.getClass().getSimpleName());
                } finally {
                    if (c != null) { try { c.disconnect(); } catch (Throwable ignored) {} }
                }
            }
        } catch (Throwable e) {
            Log.w(TAG, "send failed: " + e.getClass().getSimpleName());
        } finally {
            synchronized (LOCK) {
                if (ok) lastSent = key;
                if (key.equals(pending)) pending = "";
            }
        }
    }

    private static synchronized String resolveBase() {
        if (baseUrl != null) return baseUrl;
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            android.app.Application app = (android.app.Application) at.getMethod("currentApplication").invoke(null);
            android.content.pm.ApplicationInfo ai = app.getPackageManager()
                .getApplicationInfo(app.getPackageName(), android.content.pm.PackageManager.GET_META_DATA);
            String v = ai.metaData == null ? null : ai.metaData.getString("kf.push_url");
            if (v != null && v.length() > 0) {
                while (v.endsWith("/")) v = v.substring(0, v.length() - 1);
                baseUrl = v;
            }
        } catch (Throwable e) { Log.w(TAG, "resolveBase failed: " + e.getClass().getSimpleName()); }
        return baseUrl;
    }

    private static String mask(String t) {
        return t.length() <= 8 ? "***" : t.substring(0, 4) + "..." + "(" + t.length() + ")";
    }

    private static String esc(String s) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (ch == '"' || ch == '\\') sb.append('\\').append(ch);
            else if (ch < 0x20) sb.append(String.format("\\u%04x", (int) ch));
            else sb.append(ch);
        }
        return sb.toString();
    }
}
