package kf;

import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.util.Base64;
import android.util.Log;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;

/**
 * Previous-session exit reporter (client 2.11.3, scripts/patch-exit-reporter.py).
 *
 * The in-game error reporter (scripts/re/error_report_caves.py) dies with the process, so a native crash, a
 * low-memory kill or an ANR never reaches the server. Android keeps its own record of why each process ended
 * (ActivityManager.getHistoricalProcessExitReasons, API 30+), including the ANR dump and, on API 31+, the native
 * tombstone. This provider runs at process start (before Application.onCreate, no game code touched), and on a
 * background thread uploads every abnormal exit it has not reported yet, plus the tail of the app's own logcat, as
 * one plain-text POST to <server>/diag/upload (the existing diagnostic intake: private files on the VPS in
 * .local/diag-uploads, 4 MiB cap, nginx 2 req/min per IP). The server URL comes from the manifest meta-data
 * "kf.exit_report_url". Nothing is shown to the player.
 *
 * Bookkeeping: SharedPreferences "kf_exit_report" / "last_ts" = newest exit timestamp already handled. It only
 * advances after an HTTP 200 (or when there is nothing abnormal), so a failed upload is retried next launch. On the
 * first run the window is the last 3 days, which also catches crashes of the previous build (the exit history
 * survives an install -r).
 */
public final class ExitReportProvider extends ContentProvider {
    private static final String TAG = "KFEXIT";
    private static final String PREFS = "kf_exit_report";
    private static final long FIRST_RUN_WINDOW_MS = 3L * 24 * 60 * 60 * 1000;
    private static final int MAX_BODY = 3_900_000;
    private static final int MAX_TRACE = 1_500_000;
    private static final int MAX_LOGCAT = 1_200_000;

    @Override
    public boolean onCreate() {
        final Context context = getContext();
        if (context == null || Build.VERSION.SDK_INT < 30) return true;
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    report(context.getApplicationContext() != null ? context.getApplicationContext() : context);
                } catch (Throwable t) {
                    Log.w(TAG, "exit report failed", t);
                }
            }
        }, "kf-exit-report");
        worker.setDaemon(true);
        worker.start();
        return true;
    }

    private static boolean isAbnormal(ApplicationExitInfo e) {
        switch (e.getReason()) {
            case 2:  // SIGNALED: background SIGKILLs (importance 300+ = cached) are routine OEM/system trimming; only a
                     // kill while foreground/visible/perceptible (<= 230) is reported
                return e.getImportance() <= 230;
            case 0:  // UNKNOWN
            case 3:  // LOW_MEMORY
            case 4:  // CRASH (Java)
            case 5:  // CRASH_NATIVE
            case 6:  // ANR
            case 7:  // INITIALIZATION_FAILURE
            case 9:  // EXCESSIVE_RESOURCE_USAGE
            case 14: // FREEZER
                return true;
            default: // EXIT_SELF, PERMISSION_CHANGE, USER_REQUESTED, USER_STOPPED, DEPENDENCY_DIED, OTHER, PACKAGE_*
                return false;
        }
    }

    private static String reasonName(int reason) {
        switch (reason) {
            case 0: return "UNKNOWN";
            case 1: return "EXIT_SELF";
            case 2: return "SIGNALED";
            case 3: return "LOW_MEMORY";
            case 4: return "CRASH";
            case 5: return "CRASH_NATIVE";
            case 6: return "ANR";
            case 7: return "INITIALIZATION_FAILURE";
            case 8: return "PERMISSION_CHANGE";
            case 9: return "EXCESSIVE_RESOURCE_USAGE";
            case 10: return "USER_REQUESTED";
            case 11: return "USER_STOPPED";
            case 12: return "DEPENDENCY_DIED";
            case 13: return "OTHER";
            case 14: return "FREEZER";
            case 15: return "PACKAGE_STATE_CHANGE";
            case 16: return "PACKAGE_UPDATED";
            default: return "REASON_" + reason;
        }
    }

    private static void report(Context context) throws Exception {
        String url = null;
        ApplicationInfo appInfo = context.getPackageManager()
                .getApplicationInfo(context.getPackageName(), PackageManager.GET_META_DATA);
        if (appInfo.metaData != null) url = appInfo.metaData.getString("kf.exit_report_url");
        if (url == null || url.isEmpty()) {
            Log.i(TAG, "no kf.exit_report_url, disabled");
            return;
        }

        ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
        List<ApplicationExitInfo> exits = am.getHistoricalProcessExitReasons(null, 0, 16);
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        long now = System.currentTimeMillis();
        long last = prefs.getLong("last_ts", now - FIRST_RUN_WINDOW_MS);
        long newest = last;

        StringBuilder entries = new StringBuilder();
        int abnormal = 0;
        // newest first; walk oldest -> newest so the report reads chronologically
        for (int i = exits.size() - 1; i >= 0; i--) {
            ApplicationExitInfo e = exits.get(i);
            if (e.getTimestamp() <= last) continue;
            newest = Math.max(newest, e.getTimestamp());
            if (!isAbnormal(e)) continue;
            abnormal++;
            entries.append("\n=== exit ").append(abnormal).append(" ===\n")
                    .append("time: ").append(iso(e.getTimestamp())).append('\n')
                    .append("reason: ").append(reasonName(e.getReason())).append(" (").append(e.getReason()).append(")\n")
                    .append("status: ").append(e.getStatus()).append('\n')
                    .append("importance: ").append(e.getImportance()).append('\n')
                    .append("process: ").append(e.getProcessName()).append(" pid ").append(e.getPid()).append('\n')
                    .append("pss_kb: ").append(e.getPss()).append(" rss_kb: ").append(e.getRss()).append('\n')
                    .append("description: ").append(e.getDescription()).append('\n');
            appendTrace(entries, e);
        }

        if (abnormal == 0) {
            if (newest != last) prefs.edit().putLong("last_ts", newest).apply();
            Log.i(TAG, "no abnormal exits since " + iso(last) + " (" + exits.size() + " in history)");
            return;
        }

        StringBuilder body = new StringBuilder(entries.length() + MAX_LOGCAT + 1024);
        body.append("KF-EXIT-REPORT v1\n");
        body.append("user_id: ").append(userId(context)).append('\n');
        try {
            PackageInfo pi = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            body.append("app: ").append(pi.versionName).append(" (").append(pi.versionCode).append(")\n");
        } catch (Throwable ignored) {
        }
        body.append("device: ").append(Build.MANUFACTURER).append(' ').append(Build.MODEL)
                .append(" (").append(Build.DEVICE).append(")\n")
                .append("android: ").append(Build.VERSION.RELEASE).append(" / API ").append(Build.VERSION.SDK_INT)
                .append(" / ").append(Build.FINGERPRINT).append('\n')
                .append("abis: ").append(join(Build.SUPPORTED_ABIS)).append('\n')
                .append("reported_at: ").append(iso(now)).append('\n')
                .append("window_from: ").append(iso(last)).append('\n')
                .append("abnormal_exits: ").append(abnormal).append('\n');
        body.append(entries);
        appendLogcat(body, MAX_BODY - body.length() - 64);

        byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
        if (bytes.length > MAX_BODY) {
            byte[] cut = new byte[MAX_BODY];
            System.arraycopy(bytes, 0, cut, 0, MAX_BODY);
            bytes = cut;
        }
        int code = post(url.replaceAll("/+$", "") + "/diag/upload", bytes);
        Log.i(TAG, "uploaded " + abnormal + " abnormal exit(s), " + bytes.length + " bytes -> HTTP " + code);
        if (code == 200) prefs.edit().putLong("last_ts", newest).apply();
    }

    private static void appendTrace(StringBuilder out, ApplicationExitInfo e) {
        InputStream in = null;
        try {
            in = e.getTraceInputStream();
            if (in == null) {
                out.append("trace: none\n");
                return;
            }
            byte[] data = readAll(in, MAX_TRACE);
            if (e.getReason() == 5) {
                // API 31+: the native tombstone is a protobuf (Tombstone in system/core/debuggerd/proto);
                // decode offline with `base64 -d | protoc --decode_raw`.
                out.append("trace: tombstone-proto-base64 ").append(data.length).append(" bytes\n")
                        .append(Base64.encodeToString(data, Base64.DEFAULT));
            } else {
                out.append("trace: text ").append(data.length).append(" bytes\n")
                        .append(new String(data, StandardCharsets.UTF_8));
            }
            out.append("\n=== end trace ===\n");
        } catch (Throwable t) {
            out.append("trace: error ").append(t).append('\n');
        } finally {
            if (in != null) try { in.close(); } catch (Throwable ignored) { }
        }
    }

    private static void appendLogcat(StringBuilder out, int budget) {
        if (budget < 4096) return;
        Process p = null;
        try {
            // An app can read its own UID's log lines, which still include the previous process's last moments
            // when the ring buffer has not wrapped.
            p = new ProcessBuilder("logcat", "-d", "-v", "threadtime", "-t", "6000")
                    .redirectErrorStream(true).start();
            byte[] data = readAll(p.getInputStream(), 8_000_000);
            String text = new String(data, StandardCharsets.UTF_8);
            int limit = Math.min(budget, MAX_LOGCAT);
            if (text.length() > limit) text = text.substring(text.length() - limit);
            out.append("\n=== logcat (own uid, tail) ===\n").append(text).append("\n=== end logcat ===\n");
        } catch (Throwable t) {
            out.append("\nlogcat: error ").append(t).append('\n');
        } finally {
            if (p != null) p.destroy();
        }
    }

    private static int post(String url, byte[] body) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        try {
            c.setConnectTimeout(15000);
            c.setReadTimeout(30000);
            c.setDoOutput(true);
            c.setRequestMethod("POST");
            c.setRequestProperty("Content-Type", "text/plain; charset=utf-8");
            c.setFixedLengthStreamingMode(body.length);
            OutputStream os = c.getOutputStream();
            os.write(body);
            os.close();
            int code = c.getResponseCode();
            InputStream in = code < 400 ? c.getInputStream() : c.getErrorStream();
            if (in != null) {
                Log.i(TAG, "server: " + new String(readAll(in, 4096), StandardCharsets.UTF_8));
                in.close();
            }
            return code;
        } finally {
            c.disconnect();
        }
    }

    private static String userId(Context context) {
        try {
            // Repro SDK keeps the logged-in Kick-Flight user id (same number the server logs as userId)
            String id = context.getSharedPreferences("io.repro.user", Context.MODE_PRIVATE).getString("id", null);
            return id != null ? id : "unknown";
        } catch (Throwable t) {
            return "unknown";
        }
    }

    private static byte[] readAll(InputStream in, int max) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) {
            if (bos.size() + n > max) {
                bos.write(buf, 0, max - bos.size());
                break;
            }
            bos.write(buf, 0, n);
        }
        return bos.toByteArray();
    }

    private static String iso(long ms) {
        SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return f.format(new Date(ms));
    }

    private static String join(String[] parts) {
        StringBuilder sb = new StringBuilder();
        for (String s : parts) {
            if (sb.length() > 0) sb.append(',');
            sb.append(s);
        }
        return sb.toString();
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        return null;
    }

    @Override
    public String getType(Uri uri) {
        return null;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
