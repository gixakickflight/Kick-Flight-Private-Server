package kf;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;

/**
 * Loads libkfpatch.so (native hook library, docs/re-notes/kfpatch-design.md) as early as possible: a provider's static
 * initializer runs in Application.onCreate's pre-phase, before Unity maps libil2cpp.so. The library's constructor starts
 * a watcher thread that fills the trampoline slot table once libil2cpp is mapped.
 */
public final class KfPatchProvider extends ContentProvider {
    static {
        try {
            System.loadLibrary("kfpatch");
        } catch (Throwable t) {
            android.util.Log.e("KFPATCH", "loadLibrary failed", t);
        }
    }

    @Override public boolean onCreate() { return true; }
    @Override public Cursor query(Uri u, String[] p, String s, String[] a, String o) { return null; }
    @Override public String getType(Uri u) { return null; }
    @Override public Uri insert(Uri u, ContentValues v) { return null; }
    @Override public int delete(Uri u, String s, String[] a) { return 0; }
    @Override public int update(Uri u, ContentValues v, String s, String[] a) { return 0; }
}
