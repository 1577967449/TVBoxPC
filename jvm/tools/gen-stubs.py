# -*- coding: utf-8 -*-
"""
补齐桌面 JVM 蜘蛛桥所需的 Android / catvod 桩类。

为什么需要本脚本：
  fan.txt（CatVod 蜘蛛 DEX）经 dex2jar 转换后要在桌面 JRE 上跑，它引用了
  65 个 android/dalvik 类。上游 Win-box 的 stubs-src 已提供 132 个桩源，
  但仍有 25 个（多为内部类与工具类）缺失，缺一个就是
  NoSuchMethodError / NoSuchFieldError，表现为"蜘蛛悄悄返回空结果"。

契约来源：直接扫描 fan.txt 的 method_ids / field_ids，逐条取
  被引用成员的「名字 + 描述符」，按真实 Android 语义实现。

用法：python gen-stubs.py <stubs-src 根目录>
"""
import io
import os
import sys

FILES = {}

# ------------------------------------------------------------------ android.app

FILES['android/app/Application.java'] = r'''
package android.app;

import android.content.Context;

/**
 * Application stub —— 进程级 Application。
 * <p>SpiderRunner 会 new 一个并注入 ActivityThread / Init，作为蜘蛛的宿主 Context。
 * 因此必须继承 Context，且 getCacheDir()/getFilesDir() 等落到 Context 的实现上。
 */
public class Application extends Context {

    public Application() {
        super();
    }

    public void onCreate() {
    }

    public void onTerminate() {
    }

    public void onLowMemory() {
    }

    public void registerActivityLifecycleCallbacks(Object cb) {
    }

    public void unregisterActivityLifecycleCallbacks(Object cb) {
    }
}
'''

# --------------------------------------------------------------- android.content

FILES['android/content/ContextWrapper.java'] = r'''
package android.content;

/**
 * ContextWrapper stub —— Context 的"委托壳"。
 * <p>蜘蛛 dex2jar 产物里出现 {@code invoke-virtual ContextWrapper.getBaseContext()}
 * 且会 {@code check-cast ContextWrapper}（典型：从 Activity 反推 Application）。
 * 桌面版没有真壳，getBaseContext() 返回自身，保证链不断。
 */
public class ContextWrapper extends Context {

    private Context mBase;

    public ContextWrapper() {
        this(null);
    }

    public ContextWrapper(Context base) {
        super();
        this.mBase = base;
    }

    protected void attachBaseContext(Context base) {
        this.mBase = base;
    }

    @Override
    public Context getBaseContext() {
        return mBase != null ? mBase : this;
    }

    @Override
    public Context getApplicationContext() {
        return this;
    }
}
'''

FILES['android/content/SharedPreferences.java'] = r'''
package android.content;

import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * SharedPreferences stub —— 内存实现（桌面版无持久化必要）。
 *
 * ★ 必须是 interface（与 AOSP 一致）：蜘蛛字节码对 edit()/getString() 用的是
 *   invoke-interface，若写成 class 会抛 IncompatibleClassChangeError。
 * ★ 必须真实存取：不少蜘蛛把 token / 时间戳写进去再读回来做增量判断，
 *   若 put 空实现、get 返默认值，表现为"每次都是首次运行"的隐蔽错误。
 */
public interface SharedPreferences {

    Map<String, ?> getAll();

    String getString(String key, String defValue);

    int getInt(String key, int defValue);

    long getLong(String key, long defValue);

    float getFloat(String key, float defValue);

    boolean getBoolean(String key, boolean defValue);

    boolean contains(String key);

    Editor edit();

    void registerOnSharedPreferenceChangeListener(Object listener);

    void unregisterOnSharedPreferenceChangeListener(Object listener);

    interface Editor {
        Editor putString(String key, String value);

        Editor putInt(String key, int value);

        Editor putLong(String key, long value);

        Editor putFloat(String key, float value);

        Editor putBoolean(String key, boolean value);

        Editor remove(String key);

        Editor clear();

        boolean commit();

        void apply();
    }

    /** 按名字取（同名共享同一实例），供 Context.getSharedPreferences 调用。 */
    class Mem {

        private static final Map<String, SharedPreferences> MAP = new ConcurrentHashMap<String, SharedPreferences>();

        public static SharedPreferences get(String name) {
            String key = name == null ? "default" : name;
            synchronized (MAP) {
                SharedPreferences sp = MAP.get(key);
                if (sp == null) {
                    sp = new Impl(key);
                    MAP.put(key, sp);
                }
                return sp;
            }
        }

        static final class Impl implements SharedPreferences {

            private final Map<String, Object> data = new HashMap<String, Object>();
            private final String name;

            Impl(String name) {
                this.name = name;
            }

            @Override
            public Map<String, ?> getAll() {
                synchronized (data) {
                    return new HashMap<String, Object>(data);
                }
            }

            @Override
            public String getString(String key, String defValue) {
                Object v = get(key);
                return v instanceof String ? (String) v : defValue;
            }

            @Override
            public int getInt(String key, int defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).intValue() : defValue;
            }

            @Override
            public long getLong(String key, long defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).longValue() : defValue;
            }

            @Override
            public float getFloat(String key, float defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).floatValue() : defValue;
            }

            @Override
            public boolean getBoolean(String key, boolean defValue) {
                Object v = get(key);
                return v instanceof Boolean ? (Boolean) v : defValue;
            }

            @Override
            public boolean contains(String key) {
                synchronized (data) {
                    return data.containsKey(key);
                }
            }

            @Override
            public Editor edit() {
                return new EditorImpl(this);
            }

            @Override
            public void registerOnSharedPreferenceChangeListener(Object listener) {
            }

            @Override
            public void unregisterOnSharedPreferenceChangeListener(Object listener) {
            }

            private Object get(String key) {
                synchronized (data) {
                    return data.get(key);
                }
            }

            @Override
            public String toString() {
                return "SharedPreferences(" + name + ")";
            }
        }

        static final class EditorImpl implements Editor {

            private final Impl owner;
            private final Map<String, Object> staged = new HashMap<String, Object>();
            private boolean cleared;

            EditorImpl(Impl owner) {
                this.owner = owner;
            }

            @Override
            public Editor putString(String key, String value) {
                staged.put(key, value);
                return this;
            }

            @Override
            public Editor putInt(String key, int value) {
                staged.put(key, Integer.valueOf(value));
                return this;
            }

            @Override
            public Editor putLong(String key, long value) {
                staged.put(key, Long.valueOf(value));
                return this;
            }

            @Override
            public Editor putFloat(String key, float value) {
                staged.put(key, Float.valueOf(value));
                return this;
            }

            @Override
            public Editor putBoolean(String key, boolean value) {
                staged.put(key, Boolean.valueOf(value));
                return this;
            }

            @Override
            public Editor remove(String key) {
                staged.put(key, null);
                return this;
            }

            @Override
            public Editor clear() {
                cleared = true;
                return this;
            }

            @Override
            public boolean commit() {
                apply();
                return true;
            }

            @Override
            public void apply() {
                synchronized (owner.data) {
                    if (cleared) {
                        owner.data.clear();
                        cleared = false;
                    }
                    for (Map.Entry<String, Object> e : staged.entrySet()) {
                        if (e.getValue() == null) {
                            owner.data.remove(e.getKey());
                        } else {
                            owner.data.put(e.getKey(), e.getValue());
                        }
                    }
                    staged.clear();
                }
            }
        }
    }
}
'''

# -------------------------------------------------------------- android.graphics

FILES['android/graphics/Bitmap.java'] = r'''
package android.graphics;

/**
 * Bitmap stub —— 真实现（可读写像素），而非"返回 null 的空壳"。
 * <p>蜘蛛用它把二维码 / 判断图片尺寸，做 null 兜底反而更安全。
 * <p>★ Bitmap$Config 必须是 enum：蜘蛛字节码里对 ARGB_8888 用的是
 *   sget-object，枚举常量编译后就是 static 字段，字段名必须完全一致。
 */
public class Bitmap {

    public enum Config {
        ALPHA_8,
        RGB_565,
        ARGB_4444,
        ARGB_8888,
        HARDWARE
    }

    private final int width;
    private final int height;
    private final Config config;
    private int[] pixels;
    private boolean recycled;

    private Bitmap(int width, int height, Config config) {
        this.width = width < 0 ? 0 : width;
        this.height = height < 0 ? 0 : height;
        this.config = config == null ? Config.ARGB_8888 : config;
        this.pixels = new int[this.width * this.height];
    }

    public static Bitmap createBitmap(int width, int height, Config config) {
        return new Bitmap(width, height, config);
    }

    public static Bitmap createBitmap(Bitmap source, int x, int y, int width, int height) {
        Bitmap b = new Bitmap(width, height, source == null ? Config.ARGB_8888 : source.config);
        if (source != null) {
            for (int j = 0; j < height; j++) {
                for (int i = 0; i < width; i++) {
                    b.setPixel(i, j, source.getPixel(x + i, y + j));
                }
            }
        }
        return b;
    }

    public static Bitmap createBitmap(int[] colors, int width, int height, Config config) {
        Bitmap b = new Bitmap(width, height, config);
        int n = Math.min(colors == null ? 0 : colors.length, width * height);
        for (int i = 0; i < n; i++) {
            b.pixels[i] = colors[i];
        }
        return b;
    }

    public void setPixels(int[] pixels, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null) {
            return;
        }
        int idx = offset;
        for (int j = 0; j < height; j++) {
            for (int i = 0; i < width; i++) {
                if (idx >= 0 && idx < pixels.length) {
                    setPixel(x + i, y + j, pixels[idx]);
                }
                idx++;
            }
            idx += stride - width;
        }
    }

    public void getPixels(int[] pixels, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null) {
            return;
        }
        int idx = offset;
        for (int j = 0; j < height; j++) {
            for (int i = 0; i < width; i++) {
                if (idx >= 0 && idx < pixels.length) {
                    pixels[idx] = getPixel(x + i, y + j);
                }
                idx++;
            }
            idx += stride - width;
        }
    }

    public int getPixel(int x, int y) {
        if (x < 0 || y < 0 || x >= width || y >= height) {
            return 0;
        }
        return pixels[y * width + x];
    }

    public void setPixel(int x, int y, int color) {
        if (x < 0 || y < 0 || x >= width || y >= height) {
            return;
        }
        pixels[y * width + x] = color;
    }

    public int getWidth() {
        return width;
    }

    public int getHeight() {
        return height;
    }

    public Config getConfig() {
        return config;
    }

    public boolean isRecycled() {
        return recycled;
    }

    public void recycle() {
        recycled = true;
    }

    public int getByteCount() {
        return width * height * 4;
    }

    public int getRowBytes() {
        return width * 4;
    }

    public static Bitmap createScaledBitmap(Bitmap src, int dstWidth, int dstHeight, boolean filter) {
        return createBitmap(dstWidth, dstHeight, src == null ? Config.ARGB_8888 : src.config);
    }

    @Override
    public String toString() {
        return "Bitmap(" + width + "x" + height + "," + config + ")";
    }
}
'''

# --------------------------------------------------------------------- android.net

FILES['android/net/Uri.java'] = r'''
package android.net;

import java.util.ArrayList;
import java.util.List;

/**
 * Uri stub —— 真解析。
 * <p>调用面：{@code Uri.parse(String)} / {@code getPath()} / {@code getLastPathSegment()}。
 * 蜘蛛用 getLastPathSegment() 从分享链里取文件 id，返回 null 会让后续拼串变 "null"，
 * 属于"不报错但结果错"的高危形态 —— 因此按 RFC 语义真实现。
 */
public class Uri {

    private final String raw;

    private Uri(String raw) {
        this.raw = raw == null ? "" : raw;
    }

    public static Uri parse(String uriString) {
        return new Uri(uriString);
    }

    public static Uri fromFile(java.io.File file) {
        return new Uri(file == null ? "" : file.getAbsolutePath());
    }

    public static Uri EMPTY = new Uri("");

    public String getScheme() {
        int i = raw.indexOf(':');
        return i > 0 ? raw.substring(0, i).toLowerCase() : null;
    }

    public String getPath() {
        int q = raw.indexOf('?');
        String s = q >= 0 ? raw.substring(0, q) : raw;
        int i = s.indexOf("://");
        if (i > 0) {
            int slash = s.indexOf('/', i + 3);
            return slash >= 0 ? s.substring(slash) : "";
        }
        int colon = s.indexOf(':');
        return colon >= 0 ? s.substring(colon + 1) : s;
    }

    public String getQuery() {
        int q = raw.indexOf('?');
        if (q < 0) {
            return null;
        }
        int h = raw.indexOf('#', q);
        return h >= 0 ? raw.substring(q + 1, h) : raw.substring(q + 1);
    }

    public String getLastPathSegment() {
        String path = getPath();
        if (path == null || path.isEmpty()) {
            return null;
        }
        while (path.endsWith("/")) {
            path = path.substring(0, path.length() - 1);
        }
        int i = path.lastIndexOf('/');
        String seg = i >= 0 ? path.substring(i + 1) : path;
        return seg.isEmpty() ? null : seg;
    }

    public List<String> getPathSegments() {
        List<String> out = new ArrayList<String>();
        String path = getPath();
        if (path == null) {
            return out;
        }
        for (String s : path.split("/")) {
            if (!s.isEmpty()) {
                out.add(s);
            }
        }
        return out;
    }

    public String getHost() {
        int i = raw.indexOf("://");
        if (i < 0) {
            return null;
        }
        int start = i + 3;
        int end = raw.length();
        for (int k = start; k < raw.length(); k++) {
            char c = raw.charAt(k);
            if (c == '/' || c == '?' || c == '#' || c == ':') {
                end = k;
                break;
            }
        }
        return raw.substring(start, end);
    }

    public String getQueryParameter(String key) {
        String q = getQuery();
        if (q == null || key == null) {
            return null;
        }
        for (String kv : q.split("&")) {
            int eq = kv.indexOf('=');
            if (eq > 0 && kv.substring(0, eq).equals(key)) {
                return kv.substring(eq + 1);
            }
        }
        return null;
    }

    public boolean isHierarchical() {
        return raw.contains("://");
    }

    @Override
    public String toString() {
        return raw;
    }

    @Override
    public int hashCode() {
        return raw.hashCode();
    }

    @Override
    public boolean equals(Object o) {
        return o instanceof Uri && raw.equals(((Uri) o).raw);
    }
}
'''

FILES['android/net/UrlQuerySanitizer.java'] = r'''
package android.net;

import java.io.UnsupportedEncodingException;
import java.net.URLDecoder;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

/**
 * UrlQuerySanitizer stub —— 真解析 query string。
 * <p>调用面：{@code new UrlQuerySanitizer(String)} + {@code getValue(String)}。
 * 蜘蛛用它从播放地址里抽 token/sign，返回 null 会让请求缺参 → 播放/接口 403。
 * 因此必须真实现（不是空壳）。
 */
public class UrlQuerySanitizer {

    private final Map<String, String> values = new HashMap<String, String>();
    private final Set<String> params = new HashSet<String>();

    public UrlQuerySanitizer() {
    }

    public UrlQuerySanitizer(String url) {
        if (url != null) {
            parseUrl(url);
        }
    }

    public void parseUrl(String url) {
        if (url == null) {
            return;
        }
        int q = url.indexOf('?');
        if (q < 0) {
            return;
        }
        int h = url.indexOf('#', q);
        String query = h >= 0 ? url.substring(q + 1, h) : url.substring(q + 1);
        parseQuery(query);
    }

    public void parseQuery(String query) {
        if (query == null || query.isEmpty()) {
            return;
        }
        for (String kv : query.split("&")) {
            if (kv.isEmpty()) {
                continue;
            }
            int eq = kv.indexOf('=');
            String k = eq >= 0 ? kv.substring(0, eq) : kv;
            String v = eq >= 0 ? kv.substring(eq + 1) : "";
            k = decode(k);
            v = decode(v);
            params.add(k);
            values.put(k, v);
        }
    }

    public String getValue(String parameter) {
        return values.get(parameter);
    }

    public Set<String> getParameterSet() {
        return new HashSet<String>(params);
    }

    public void setAllowUnregisteredParamaters(boolean allow) {
    }

    public void addSanitizedEntry(String parameter, String value) {
        params.add(parameter);
        values.put(parameter, value);
    }

    public void clear() {
        values.clear();
        params.clear();
    }

    private static String decode(String s) {
        try {
            return URLDecoder.decode(s, "UTF-8");
        } catch (UnsupportedEncodingException e) {
            return s;
        } catch (IllegalArgumentException e) {
            return s;
        }
    }
}
'''

# ----------------------------------------------------------------------- android.os

FILES['android/os/Environment.java'] = r'''
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
'''

FILES['android/os/Looper.java'] = r'''
package android.os;

/**
 * Looper stub —— 桌面常驻进程只有一个"主线程"Looper，惰性单例。
 * <p>蜘蛛用它做 isMainThread 判断（主线程直调、否则 post Handler）。
 * 返回 null 会走错分支或直接 NPE，因此必须给非 null 实例。
 */
public class Looper {

    private static final Looper MAIN = new Looper();

    public static Looper getMainLooper() {
        return MAIN;
    }

    public static Looper myLooper() {
        return MAIN;
    }

    public static void prepare() {
    }

    public static void prepareMainLooper() {
    }

    public static void loop() {
    }

    public Thread getThread() {
        return Thread.currentThread();
    }

    public boolean isCurrentThread() {
        return true;
    }

    public void quit() {
    }

    public void quitSafely() {
    }

    public static MessageQueue getQueue() {
        return MessageQueue.INSTANCE;
    }

    /** MessageQueue 占位（真实类在 android.os，蜘蛛可能 Class.forName）。 */
    public static class MessageQueue {
        static final MessageQueue INSTANCE = new MessageQueue();

        public boolean isIdle() {
            return true;
        }

        public Message next() {
            return null;
        }

        public void quit(boolean safe) {
        }
    }
}
'''

# --------------------------------------------------------------------- android.util

FILES['android/util/Base64.java'] = r'''
package android.util;

/**
 * Base64 stub —— 必须真实现。
 * <p>蜘蛛大量用它解码 ext / 接口返回的 base64 blob；空实现会让"能连上但解析不出内容"。
 * <p>语义对齐 AOSP：默认 **带 padding、每 76 字符换行**；NO_WRAP/NO_PADDING/URL_SAFE
 * /CRLF 四个 flag 按位生效；解码失败抛 IllegalArgumentException。
 */
public final class Base64 {

    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int CRLF = 4;
    public static final int URL_SAFE = 8;
    public static final int NO_CLOSE = 16;

    private Base64() {
    }

    public static byte[] decode(String str, int flags) {
        if (str == null) {
            return null;
        }
        return decode(str.getBytes(java.nio.charset.StandardCharsets.US_ASCII), flags);
    }

    public static byte[] decode(byte[] input, int flags) {
        if (input == null) {
            return null;
        }
        String s = new String(input, java.nio.charset.StandardCharsets.US_ASCII);
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\n' || c == '\r' || c == ' ' || c == '\t') {
                continue;
            }
            if ((flags & URL_SAFE) != 0) {
                if (c == '-') {
                    c = '+';
                } else if (c == '_') {
                    c = '/';
                }
            }
            sb.append(c);
        }
        String body = sb.toString();
        int rem = body.length() % 4;
        if (rem != 0) {
            if (rem == 1) {
                throw new IllegalArgumentException("bad base-64");
            }
            if ((flags & NO_PADDING) == 0) {
                while (body.length() % 4 != 0) {
                    body = body + "=";
                }
            }
        }
        try {
            java.util.Base64.Decoder d = (flags & URL_SAFE) != 0
                    ? java.util.Base64.getUrlDecoder()
                    : java.util.Base64.getMimeDecoder();
            return d.decode(body);
        } catch (IllegalArgumentException e) {
            throw new IllegalArgumentException("bad base-64");
        }
    }

    public static byte[] encode(byte[] input, int flags) {
        return encodeToString(input, flags).getBytes(java.nio.charset.StandardCharsets.US_ASCII);
    }

    public static String encodeToString(byte[] input, int flags) {
        if (input == null) {
            return null;
        }
        java.util.Base64.Encoder enc = (flags & URL_SAFE) != 0
                ? java.util.Base64.getUrlEncoder()
                : java.util.Base64.getEncoder();
        String out = enc.encodeToString(input);
        if ((flags & NO_PADDING) != 0) {
            while (out.endsWith("=")) {
                out = out.substring(0, out.length() - 1);
            }
        }
        if ((flags & NO_WRAP) == 0) {
            String nl = (flags & CRLF) != 0 ? "\r\n" : "\n";
            StringBuilder sb = new StringBuilder(out.length() + out.length() / 76 * 2);
            for (int i = 0; i < out.length(); i += 76) {
                int end = Math.min(i + 76, out.length());
                sb.append(out, i, end);
                if (end < out.length()) {
                    sb.append(nl);
                }
            }
            out = sb.toString();
        }
        return out;
    }
}
'''

FILES['android/util/Log.java'] = r'''
package android.util;

/**
 * Log stub —— 真输出到 stderr。
 * <p>蜘蛛的调试日志是排查"返回空结果"的唯一线索，吞掉它等于自断手脚。
 * 格式与 logcat 对齐： {@code D/Tag: msg}。
 */
public final class Log {

    public static final int VERBOSE = 2;
    public static final int DEBUG = 3;
    public static final int INFO = 4;
    public static final int WARN = 5;
    public static final int ERROR = 6;
    public static final int ASSERT = 7;

    private static volatile boolean enabled = true;

    private Log() {
    }

    public static void setEnabled(boolean on) {
        enabled = on;
    }

    public static int v(String tag, String msg) {
        return println(VERBOSE, tag, msg);
    }

    public static int v(String tag, String msg, Throwable tr) {
        return println(VERBOSE, tag, msg + '\n' + stack(tr));
    }

    public static int d(String tag, String msg) {
        return println(DEBUG, tag, msg);
    }

    public static int d(String tag, String msg, Throwable tr) {
        return println(DEBUG, tag, msg + '\n' + stack(tr));
    }

    public static int i(String tag, String msg) {
        return println(INFO, tag, msg);
    }

    public static int i(String tag, String msg, Throwable tr) {
        return println(INFO, tag, msg + '\n' + stack(tr));
    }

    public static int w(String tag, String msg) {
        return println(WARN, tag, msg);
    }

    public static int w(String tag, String msg, Throwable tr) {
        return println(WARN, tag, msg + '\n' + stack(tr));
    }

    public static int w(String tag, Throwable tr) {
        return println(WARN, tag, stack(tr));
    }

    public static int e(String tag, String msg) {
        return println(ERROR, tag, msg);
    }

    public static int e(String tag, String msg, Throwable tr) {
        return println(ERROR, tag, msg + '\n' + stack(tr));
    }

    public static int wtf(String tag, String msg) {
        return println(ASSERT, tag, msg);
    }

    public static int wtf(String tag, Throwable tr) {
        return println(ASSERT, tag, stack(tr));
    }

    public static boolean isLoggable(String tag, int level) {
        return enabled;
    }

    public static String getStackTraceString(Throwable tr) {
        return stack(tr);
    }

    public static int println(int priority, String tag, String msg) {
        if (!enabled) {
            return 0;
        }
        String p;
        switch (priority) {
            case VERBOSE: p = "V"; break;
            case DEBUG:   p = "D"; break;
            case INFO:    p = "I"; break;
            case WARN:    p = "W"; break;
            case ERROR:   p = "E"; break;
            default:      p = "A"; break;
        }
        System.err.println(p + "/" + tag + ": " + msg);
        return 0;
    }

    private static String stack(Throwable tr) {
        if (tr == null) {
            return "";
        }
        java.io.StringWriter sw = new java.io.StringWriter();
        tr.printStackTrace(new java.io.PrintWriter(sw));
        return sw.toString();
    }
}
'''

FILES['android/util/DisplayMetrics.java'] = r'''
package android.util;

/**
 * DisplayMetrics stub —— 桌面无 UI，给一组**自洽**的固定值。
 * <p>返回 0 会让按密度换算的逻辑（dp→px）算出 NaN/0 并污染请求参数，
 * 因此给 1080p / density=3 的合理默认。
 */
public class DisplayMetrics {

    public static final int DENSITY_LOW = 120;
    public static final int DENSITY_MEDIUM = 160;
    public static final int DENSITY_HIGH = 240;
    public static final int DENSITY_XHIGH = 320;
    public static final int DENSITY_XXHIGH = 480;
    public static final int DENSITY_XXXHIGH = 640;
    public static final int DENSITY_DEFAULT = DENSITY_MEDIUM;

    public int widthPixels = 1920;
    public int heightPixels = 1080;
    public float density = 3.0f;
    public float scaledDensity = 3.0f;
    public float xdpi = 480.0f;
    public float ydpi = 480.0f;
    public int densityDpi = DENSITY_XXHIGH;
    public int noncompatWidthPixels = 1920;
    public int noncompatHeightPixels = 1080;
    public float noncompatDensity = 3.0f;
    public int noncompatDensityDpi = DENSITY_XXHIGH;

    public void setTo(DisplayMetrics other) {
        if (other == null) {
            return;
        }
        widthPixels = other.widthPixels;
        heightPixels = other.heightPixels;
        density = other.density;
        scaledDensity = other.scaledDensity;
        xdpi = other.xdpi;
        ydpi = other.ydpi;
        densityDpi = other.densityDpi;
    }

    public void setToDefaults() {
    }

    @Override
    public String toString() {
        return "DisplayMetrics{" + widthPixels + "x" + heightPixels + ", density=" + density + "}";
    }
}
'''

# -------------------------------------------------------------------- android.webkit

FILES['android/webkit/ValueCallback.java'] = r'''
package android.webkit;

/**
 * ValueCallback stub —— WebView.evaluateJavascript 的回调接口。
 * ★ 必须是 interface（调用点用 invoke-interface）。
 */
public interface ValueCallback<T> {
    void onReceiveValue(T value);
}
'''

# --------------------------------------------------------------------- android.widget

FILES['android/widget/FrameLayout.java'] = r'''
package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.view.ViewGroup;

/**
 * FrameLayout stub —— 容器控件（蜘蛛用它承载 WebView/ImageView）。
 * ★ 内嵌 LayoutParams 必须继承 ViewGroup.MarginLayoutParams：
 *   蜘蛛字节码对 FrameLayout$LayoutParams 的父类型有硬编码假设（check-cast），
 *   继承链错了会抛 ClassCastException。
 */
public class FrameLayout extends ViewGroup {

    public FrameLayout(Context context) {
        super(context);
    }

    public FrameLayout(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    @Override
    public void addView(View child) {
    }

    @Override
    public void addView(View child, int index) {
    }

    public void addView(View child, LayoutParams params) {
    }

    public void removeAllViews() {
    }

    public View getChildAt(int index) {
        return null;
    }

    public int getChildCount() {
        return 0;
    }

    public void setForeground(android.graphics.drawable.Drawable d) {
    }

    public void setPadding(int l, int t, int r, int b) {
    }

    public static class LayoutParams extends ViewGroup.MarginLayoutParams {

        public LayoutParams(int width, int height) {
            super(width, height);
        }

        public LayoutParams(Context c, AttributeSet attrs) {
            super(c, attrs);
        }

        public LayoutParams(ViewGroup.LayoutParams source) {
            super(source);
        }

        public LayoutParams(LayoutParams source) {
            super(source);
        }
    }
}
'''

FILES['android/widget/ImageView.java'] = r'''
package android.widget;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.drawable.Drawable;
import android.util.AttributeSet;
import android.view.View;

/**
 * ImageView stub —— 图片控件。
 * ★ 内嵌 ScaleType 必须是 enum：蜘蛛对 CENTER_CROP/FIT_CENTER 用的是
 *   sget-object，枚举常量 = static 字段，名字必须齐全。
 */
public class ImageView extends View {

    public enum ScaleType {
        MATRIX,
        FIT_XY,
        FIT_START,
        FIT_CENTER,
        FIT_END,
        CENTER,
        CENTER_CROP,
        CENTER_INSIDE
    }

    public ImageView(Context context) {
        super(context);
    }

    public ImageView(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    public void setImageBitmap(Bitmap bm) {
    }

    public void setImageDrawable(Drawable drawable) {
    }

    public void setImageResource(int resId) {
    }

    public void setImageURI(android.net.Uri uri) {
    }

    public void setScaleType(ScaleType scaleType) {
    }

    public ScaleType getScaleType() {
        return ScaleType.FIT_CENTER;
    }

    public void setAdjustViewBounds(boolean adjustViewBounds) {
    }

    public Drawable getDrawable() {
        return null;
    }

    public void setMaxWidth(int maxWidth) {
    }

    public void setMaxHeight(int maxHeight) {
    }
}
'''

FILES['android/widget/Toast.java'] = r'''
package android.widget;

import android.content.Context;
import android.view.View;

/**
 * Toast stub —— 桌面无 UI，日志化处理（不炸、不阻塞）。
 */
public class Toast {

    public static final int LENGTH_SHORT = 0;
    public static final int LENGTH_LONG = 1;

    private CharSequence text = "";
    private int duration = LENGTH_SHORT;

    public static Toast makeText(Context context, CharSequence text, int duration) {
        Toast t = new Toast();
        t.text = text;
        t.duration = duration;
        return t;
    }

    public static Toast makeText(Context context, int resId, int duration) {
        return makeText(context, "", duration);
    }

    public void show() {
        if (text != null && text.length() > 0) {
            android.util.Log.i("Toast", text.toString());
        }
    }

    public void cancel() {
    }

    public void setText(CharSequence s) {
        this.text = s;
    }

    public void setDuration(int d) {
        this.duration = d;
    }

    public void setGravity(int gravity, int xOffset, int yOffset) {
    }

    public void setMargin(float horizontal, float vertical) {
    }

    public void setView(View view) {
    }

    public View getView() {
        return null;
    }
}
'''

# ------------------------------------------------------------------ dalvik.annotation

for name, body in {
    'EnclosingClass': '    String value();',
    'EnclosingMethod': '    String value();',
    'InnerClass': '    String name();\n\n    int accessFlags();',
    'MemberClasses': '    String[] value();',
    'Signature': '    String[] value();',
}.items():
    FILES['dalvik/annotation/%s.java' % name] = r'''
package dalvik.annotation;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * %s —— 仅供 dex2jar 产物里的编译期注解使用。
 *
 * ★ 故意用 RetentionPolicy.CLASS（而非 AOSP 的 RUNTIME）：
 *   这些注解只在"反编译/反射元数据"层面有意义，桌面宿主从不读它。
 *   设为 CLASS 后 JVM 加载类时**不会**去解析注解值 —— 即使 dex2jar 把
 *   值写成另一种形态（String vs Class），也不会抛 AnnotationTypeMismatchException，
 *   把一类纯噪音故障消灭在源头。
 */
@Retention(RetentionPolicy.CLASS)
@Target({ElementType.TYPE, ElementType.METHOD, ElementType.CONSTRUCTOR, ElementType.FIELD})
public @interface %s {
%s
}
''' % (name, name, body)

# --------------------------------------------------------------------- dalvik.system

FILES['dalvik/system/DexFile.java'] = r'''
package dalvik.system;

import java.util.Collections;
import java.util.Enumeration;

/**
 * DexFile stub —— 桌面版没有 Dalvik，动态 dex 加载降级为 Class.forName。
 * <p>蜘蛛里出现它的地方通常是"壳"分支（壳在本进程里加载另一个 dex），
 * 桌面桥已由 dex2jar 把 dex 静态转成了 .class，因此这里的 loadDex 只是
 * 让分支「不抛异常地走通」，真正取类走 loadClass。
 */
public final class DexFile {

    private String name;
    private ClassLoader loader;

    private DexFile() {
    }

    public static DexFile loadDex(String sourcePathName, String outputPathName, int flags)
            throws java.io.IOException {
        if (sourcePathName == null || !new java.io.File(sourcePathName).exists()) {
            throw new java.io.IOException("dex file not found: " + sourcePathName);
        }
        DexFile f = new DexFile();
        f.name = sourcePathName;
        return f;
    }

    public Class<?> loadClass(String name, ClassLoader loader) {
        this.loader = loader;
        try {
            ClassLoader cl = loader != null ? loader : DexFile.class.getClassLoader();
            return Class.forName(name, false, cl);
        } catch (ClassNotFoundException e) {
            return null;
        }
    }

    public Class<?> loadClass(String name) {
        return loadClass(name, null);
    }

    public String getName() {
        return name;
    }

    public void close() throws java.io.IOException {
    }

    public Enumeration<String> entries() {
        return Collections.emptyEnumeration();
    }

    public boolean isDexOptNeeded(String fileName) {
        return false;
    }
}
'''

# --------------------------------------------------------------------- catvod 辅助

FILES['com/github/catvod/net/OkHttp.java'] = r'''
package com.github.catvod.net;

import java.io.IOException;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import okhttp3.Dns;
import okhttp3.Headers;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/**
 * OkHttp stub —— com.github.catvod 家族的 HTTP 门面。
 * <p>fan.txt 本体（spider/merge/parser）不直接用它（自带 merge 版 HTTP），
 * 但 {@code Spider.safeDns()/Spider.client()} 的字节码契约引用了它 ——
 * 缺这个类会让任何调用 Spider 静态方法的蜘蛛抛 NoClassDefFoundError。
 * <p>顺带把 string()/newCall() 做成真实现，方便后续 .jar 源复用。
 */
public class OkHttp {

    private static volatile OkHttpClient client;

    public static OkHttpClient client() {
        OkHttpClient c = client;
        if (c == null) {
            synchronized (OkHttp.class) {
                c = client;
                if (c == null) {
                    c = new OkHttpClient.Builder()
                            .connectTimeout(15, TimeUnit.SECONDS)
                            .readTimeout(20, TimeUnit.SECONDS)
                            .followRedirects(true)
                            .build();
                    client = c;
                }
            }
        }
        return c;
    }

    public static Dns dns() {
        return Dns.SYSTEM;
    }

    public static Response newCall(String url) throws IOException {
        return newCall(url, null);
    }

    public static Response newCall(String url, Map<String, String> headers) throws IOException {
        Request.Builder b = new Request.Builder()
                .url(url)
                .header("User-Agent", Util.CHROME);
        if (headers != null) {
            for (Map.Entry<String, String> e : headers.entrySet()) {
                if (e.getKey() != null && e.getValue() != null) {
                    b.header(e.getKey(), e.getValue());
                }
            }
        }
        return client().newCall(b.build()).execute();
    }

    public static String string(String url) {
        return string(url, null);
    }

    public static String string(String url, Map<String, String> headers) {
        Response resp = null;
        try {
            resp = newCall(url, headers);
            ResponseBody body = resp.body();
            return body == null ? "" : body.string();
        } catch (Throwable t) {
            return "";
        } finally {
            if (resp != null) {
                resp.close();
            }
        }
    }

    public static Headers headers(Map<String, String> headers) {
        Headers.Builder b = new Headers.Builder();
        if (headers != null) {
            for (Map.Entry<String, String> e : headers.entrySet()) {
                if (e.getKey() != null && e.getValue() != null) {
                    b.add(e.getKey(), e.getValue());
                }
            }
        }
        return b.build();
    }
}
'''

FILES['com/github/catvod/utils/Util.java'] = r'''
package com.github.catvod.utils;

/**
 * Util stub —— com.github.catvod 家族的常量与编码工具。
 * <p>只放字节码契约里真正会被引用的成员；实现均为真实现（不是空壳）。
 */
public class Util {

    public static final String CHROME =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public static final String CHROME_MAC =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public static final String ACCEPT =
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
    public static final String UA = CHROME;

    public static String md5(String src) {
        try {
            java.security.MessageDigest md = java.security.MessageDigest.getInstance("MD5");
            byte[] d = md.digest(src.getBytes("UTF-8"));
            StringBuilder sb = new StringBuilder();
            for (byte b : d) {
                sb.append(String.format("%02x", b));
            }
            return sb.toString();
        } catch (Exception e) {
            return "";
        }
    }

    public static String base64Encode(String s) {
        return AndroidBase64.encode(s);
    }

    public static String base64Decode(String s) {
        return AndroidBase64.decode(s);
    }

    public static String urlEncode(String s) {
        try {
            return java.net.URLEncoder.encode(s, "UTF-8");
        } catch (Exception e) {
            return s;
        }
    }

    public static String urlDecode(String s) {
        try {
            return java.net.URLDecoder.decode(s, "UTF-8");
        } catch (Exception e) {
            return s;
        }
    }

    /** 避免直接依赖 android.util.Base64 的包私有差异，这里做一层包装。 */
    static final class AndroidBase64 {
        static String encode(String s) {
            try {
                byte[] b = s.getBytes("UTF-8");
                return java.util.Base64.getEncoder().encodeToString(b);
            } catch (Exception e) {
                return "";
            }
        }

        static String decode(String s) {
            try {
                return new String(java.util.Base64.getMimeDecoder().decode(s), "UTF-8");
            } catch (Exception e) {
                return "";
            }
        }
    }
}
'''

# ------------------------------------------------------------------ 第三方库补齐桩

FILES['com/thegrizzlylabs/sardineandroid/DavResource.java'] = r'''
package com.thegrizzlylabs.sardineandroid;

import java.util.ArrayList;
import java.util.Date;
import java.util.List;

/**
 * DavResource stub —— WebDAV 资源模型（AList / WebDAV 源用）。
 * <p>桌面版不做 WebDAV 传输（也无需付 4MB 依赖），但字节码契约要求类存在；
 * 字段按 Sardine 真实语义给默认值，保证 getter 不返回 null。
 */
public class DavResource {

    private String path;
    private String name;
    private String contentType;
    private String etag;
    private Date modified;
    private Date created;
    private long contentLength;
    private List<String> privileges = new ArrayList<String>();

    public String getPath() {
        return path == null ? "" : path;
    }

    public void setPath(String path) {
        this.path = path;
    }

    public String getName() {
        return name == null ? "" : name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public String getContentType() {
        return contentType == null ? "" : contentType;
    }

    public void setContentType(String contentType) {
        this.contentType = contentType;
    }

    public String getEtag() {
        return etag == null ? "" : etag;
    }

    public void setEtag(String etag) {
        this.etag = etag;
    }

    public Date getModified() {
        return modified;
    }

    public void setModified(Date modified) {
        this.modified = modified;
    }

    public Date getCreation() {
        return created;
    }

    public void setCreation(Date created) {
        this.created = created;
    }

    public long getContentLength() {
        return contentLength;
    }

    public void setContentLength(long contentLength) {
        this.contentLength = contentLength;
    }

    public List<String> getPrivileges() {
        return privileges;
    }

    public boolean isDirectory() {
        return contentType != null && contentType.endsWith("directory");
    }

    @Override
    public String toString() {
        return getPath();
    }
}
'''

FILES['com/thegrizzlylabs/sardineandroid/Sardine.java'] = r'''
package com.thegrizzlylabs.sardineandroid;

import java.io.IOException;
import java.io.InputStream;
import java.util.Collections;
import java.util.List;

/**
 * Sardine stub —— WebDAV 客户端（桌面桥不做传输）。
 * <p>所有方法都"优雅失败"：读操作返回空列表、写操作抛 IOException（与真实
 * 网络不可达一致），让上层走既有的错误分支，而不是伪装成功之后在下游 NPE。
 */
public class Sardine {

    public Sardine() {
    }

    public void setCredentials(String username, String password) {
    }

    public void setCustomizedVerifier(Object verifier) {
    }

    public void enablePreemptiveAuthentication(String host) {
    }

    public void disablePreemptiveAuthentication() {
    }

    public List<DavResource> list(String url) throws IOException {
        return Collections.emptyList();
    }

    public List<DavResource> list(String url, int depth) throws IOException {
        return Collections.emptyList();
    }

    public List<DavResource> getResources(String url) throws IOException {
        return Collections.emptyList();
    }

    public DavResource get(String url) throws IOException {
        return null;
    }

    public boolean exists(String url) throws IOException {
        return false;
    }

    public InputStream get(String url, java.util.Map<String, String> headers) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, byte[] data) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, byte[] data, String contentType) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, InputStream dataStream) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void createDirectory(String url) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void delete(String url) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void move(String sourceUrl, String destinationUrl) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void copy(String sourceUrl, String destinationUrl) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }
}
'''

FILES['com/thegrizzlylabs/sardineandroid/impl/OkHttpSardine.java'] = r'''
package com.thegrizzlylabs.sardineandroid.impl;

import com.thegrizzlylabs.sardineandroid.Sardine;

/**
 * OkHttpSardine stub —— Sardine 的 okhttp 实现（占位）。
 * <p>继承 Sardine，行为即"传输不可用"。桌面桥无需 WebDAV。
 */
public class OkHttpSardine extends Sardine {

    public OkHttpSardine() {
        super();
    }

    public OkHttpSardine(Object client) {
        super();
    }
}
'''

FILES['com/google/net/cronet/okhttptransport/CronetInterceptor.java'] = r'''
package com.google.net.cronet.okhttptransport;

/**
 * CronetInterceptor stub —— Cronet 传输拦截器（Android 专有）。
 * <p>个别蜘蛛（Bili 等）用 okhttp3 的 Builder 链式挂它做降级；桌面版没有 Cronet，
 * 这里提供空的 Interceptor 实现，保证 addInterceptor(new CronetInterceptor(...)) 能编译期通过、
 * 运行期只用标准 Socket 传输。
 */
public class CronetInterceptor implements okhttp3.Interceptor {

    public CronetInterceptor(Object engine) {
    }

    public CronetInterceptor(Object engine, Object executor) {
    }

    @Override
    public okhttp3.Response intercept(okhttp3.Interceptor.Chain chain) throws java.io.IOException {
        return chain.proceed(chain.request());
    }
}
'''

FILES['com/google/net/cronet/okhttptransport/CronetInterceptorBuilder.java'] = r'''
package com.google.net.cronet.okhttptransport;

/** CronetInterceptor 的 Builder 占位（部分代码用 Builder 形态构造）。 */
public class CronetInterceptorBuilder {

    private Object engine;

    public CronetInterceptorBuilder(Object engine) {
        this.engine = engine;
    }

    public CronetInterceptor build() {
        return new CronetInterceptor(engine);
    }
}
'''

FILES['test/MainActivity.java'] = r'''
package test;

import android.app.Activity;
import android.os.Bundle;

/**
 * test.MainActivity stub —— 反混淆残留的调试入口类。
 * <p>某些工具链（如 fty 壳）在 <clinit> 里 Class.forName 它做自检；
 * 缺失会抛 ClassNotFoundException（多数被 catch，但个别会污染类初始化状态）。
 * 这里提供一个空 Activity，让自检"通过但不做事"。
 */
public class MainActivity extends Activity {

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
    }
}
'''


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else '.'
    written = 0
    skipped = 0
    for rel, content in FILES.items():
        path = os.path.join(root, rel.replace('/', os.sep))
        if os.path.exists(path):
            skipped += 1
            continue
        d = os.path.dirname(path)
        if d and not os.path.isdir(d):
            os.makedirs(d)
        with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(content.lstrip('\n'))
        written += 1
    print('生成 %d 个桩类，跳过已存在 %d 个' % (written, skipped))
    for rel in sorted(FILES):
        print('   ', rel)


if __name__ == '__main__':
    main()
