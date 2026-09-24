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
