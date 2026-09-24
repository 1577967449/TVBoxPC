package android.os;

import java.io.File;

/**
 * Environment stub —— 外置存储目录（桌面版映射到沙箱目录）。
 */
public class Environment {

    public static final String MEDIA_MOUNTED = "mounted";
    public static final String MEDIA_REMOVED = "removed";
    public static final String MEDIA_UNMOUNTED = "unmounted";
    public static final String DIRECTORY_DOWNLOADS = "Download";
    public static final String DIRECTORY_MOVIES = "Movies";
    public static final String DIRECTORY_MUSIC = "Music";
    public static final String DIRECTORY_PICTURES = "Pictures";
    public static final String DIRECTORY_DCIM = "DCIM";

    public static File getExternalStorageDirectory() {
        return ensure(new File(android.content.Context.getBaseDir(), "external"));
    }

    public static File getExternalStoragePublicDirectory(String type) {
        return ensure(new File(getExternalStorageDirectory(), type == null ? "" : type));
    }

    public static String getExternalStorageState() {
        return MEDIA_MOUNTED;
    }

    public static boolean isExternalStorageEmulated() {
        return true;
    }

    public static boolean isExternalStorageRemovable() {
        return false;
    }

    public static File getDataDirectory() {
        return ensure(new File(android.content.Context.getBaseDir(), "data"));
    }

    public static File getDownloadCacheDirectory() {
        return ensure(new File(android.content.Context.getBaseDir(), "cache"));
    }

    public static File getRootDirectory() {
        return ensure(android.content.Context.getBaseDir());
    }

    private static File ensure(File f) {
        if (f != null && !f.exists()) {
            //noinspection ResultOfMethodCallIgnored
            f.mkdirs();
        }
        return f;
    }
}
