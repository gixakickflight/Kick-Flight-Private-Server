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

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FileReader;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Arrays;
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
 * v2 (client 2.11.7): native crashes become locatable. ~2-60 s after start a snapshot of the /proc/self/maps lines
 * of libil2cpp/libunity/libmain/libcri is stored per pid in files/kf_maps/<pid>.txt (ASLR bases differ every run;
 * without them a tombstone pc cannot be turned into an RVA). Each exit record embeds the snapshot of its own pid,
 * a pid-filtered logcat (-b main,crash) and the uid-wide crash/fatal lines extracted before tailing, so the
 * 'Fatal signal' / 'E CRASH' / 'F DEBUG' blocks are not lost to the tail window. scripts/re/symbolize_exit_report.py
 * turns such a report into method names.
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
    private static final int MAX_PID_LOGCAT = 300_000;
    private static final int MAX_CRASH_LINES = 150_000;
    private static final int MAX_PID_LOGS = 4;
    private static final int KEEP_SNAPSHOTS = 12;
    private static final String[] MAP_LIBS = {"libil2cpp.so", "libunity.so", "libmain.so", "libcri"};

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
        Thread snap = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    snapshotMaps(context);
                } catch (Throwable t) {
                    Log.w(TAG, "maps snapshot failed", t);
                }
            }
        }, "kf-maps-snapshot");
        snap.setDaemon(true);
        snap.start();
        return true;
    }

    private static File mapsDir(Context context) {
        File d = new File(context.getFilesDir(), "kf_maps");
        d.mkdirs();
        return d;
    }

    /** Poll /proc/self/maps until libil2cpp and libunity are mapped (<= ~60 s), store their lines under our pid. */
    private static void snapshotMaps(Context context) throws Exception {
        int pid = android.os.Process.myPid();
        File dir = mapsDir(context);
        String[] files = dir.list();
        if (files != null && files.length > KEEP_SNAPSHOTS) {
            Arrays.sort(files);
            File[] all = dir.listFiles();
            if (all != null) {
                Arrays.sort(all, new java.util.Comparator<File>() {
                    @Override
                    public int compare(File a, File b) {
                        return Long.compare(a.lastModified(), b.lastModified());
                    }
                });
                for (int i = 0; i < all.length - KEEP_SNAPSHOTS; i++) all[i].delete();
            }
        }
        for (int attempt = 0; attempt < 30; attempt++) {
            Thread.sleep(attempt == 0 ? 2000 : 2000);
            StringBuilder sb = new StringBuilder();
            boolean il2cpp = false, unity = false;
            BufferedReader r = new BufferedReader(new FileReader("/proc/self/maps"));
            try {
                String line;
                while ((line = r.readLine()) != null) {
                    for (String lib : MAP_LIBS) {
                        if (line.contains(lib)) {
                            sb.append(line).append('\n');
                            if (lib.equals("libil2cpp.so")) il2cpp = true;
                            if (lib.equals("libunity.so")) unity = true;
                            break;
                        }
                    }
                }
            } finally {
                r.close();
            }
            if (il2cpp && unity) {
                FileOutputStream os = new FileOutputStream(new File(dir, pid + ".txt"));
                try {
                    os.write(sb.toString().getBytes(StandardCharsets.UTF_8));
                } finally {
                    os.close();
                }
                return;
            }
        }
    }

    private static void appendMaps(StringBuilder out, Context context, int pid) {
        try {
            File f = new File(mapsDir(context), pid + ".txt");
            if (!f.isFile()) {
                out.append("maps_snapshot: none for pid ").append(pid).append('\n');
                return;
            }
            FileInputStream in = new FileInputStream(f);
            try {
                out.append("maps_snapshot: pid ").append(pid).append(" (libil2cpp/libunity/libmain/libcri lines)\n")
                        .append(new String(readAll(in, 65536), StandardCharsets.UTF_8))
                        .append("=== end maps_snapshot ===\n");
            } finally {
                in.close();
            }
        } catch (Throwable t) {
            out.append("maps_snapshot: error ").append(t).append('\n');
        }
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
        int pidLogs = 0;
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
            appendMaps(entries, context, e.getPid());
            appendTrace(entries, e);
            if (pidLogs < MAX_PID_LOGS) {
                pidLogs++;
                appendPidLogcat(entries, e.getPid());
            }
        }

        if (abnormal == 0) {
            if (newest != last) prefs.edit().putLong("last_ts", newest).apply();
            Log.i(TAG, "no abnormal exits since " + iso(last) + " (" + exits.size() + " in history)");
            return;
        }

        StringBuilder body = new StringBuilder(entries.length() + MAX_LOGCAT + 1024);
        body.append("KF-EXIT-REPORT v2\n");
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

    /** Own-uid log lines of one process (exit pid), main + crash buffers; the app cannot read other uids (debuggerd). */
    private static void appendPidLogcat(StringBuilder out, int pid) {
        Process p = null;
        try {
            p = new ProcessBuilder("logcat", "-d", "-v", "threadtime", "-b", "main,crash", "--pid=" + pid, "-t", "5000")
                    .redirectErrorStream(true).start();
            byte[] data = readAll(p.getInputStream(), 4_000_000);
            String text = new String(data, StandardCharsets.UTF_8);
            if (text.length() > MAX_PID_LOGCAT) text = text.substring(text.length() - MAX_PID_LOGCAT);
            out.append("=== logcat pid ").append(pid).append(" (main,crash) ===\n").append(text)
                    .append("\n=== end logcat pid ").append(pid).append(" ===\n");
        } catch (Throwable t) {
            out.append("logcat pid ").append(pid).append(": error ").append(t).append('\n');
        } finally {
            if (p != null) p.destroy();
        }
    }

    private static boolean isCrashLine(String l) {
        return l.contains("Fatal signal") || l.contains(" CRASH") || l.contains(" DEBUG ") || l.contains("AndroidRuntime")
                || l.contains("backtrace:") || l.contains("signal 11") || l.contains("signal 6")
                || l.matches("^\\S+ +\\S+ +\\d+ +\\d+ F .*");
    }

    private static void appendLogcat(StringBuilder out, int budget) {
        if (budget < 4096) return;
        Process p = null;
        try {
            // An app can read its own UID's log lines, which still include the previous process's last moments
            // when the ring buffer has not wrapped.
            p = new ProcessBuilder("logcat", "-d", "-v", "threadtime", "-b", "main,crash", "-t", "12000")
                    .redirectErrorStream(true).start();
            byte[] data = readAll(p.getInputStream(), 8_000_000);
            String text = new String(data, StandardCharsets.UTF_8);
            // crash/fatal lines first, extracted from the whole capture before the tail cut drops them
            StringBuilder crash = new StringBuilder();
            for (String line : text.split("\n")) {
                if (isCrashLine(line)) crash.append(line).append('\n');
            }
            if (crash.length() > MAX_CRASH_LINES) crash.delete(0, crash.length() - MAX_CRASH_LINES);
            if (crash.length() > 0 && crash.length() < budget / 2) {
                out.append("\n=== crash/fatal lines (own uid) ===\n").append(crash).append("=== end crash lines ===\n");
                budget -= crash.length();
            }
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
