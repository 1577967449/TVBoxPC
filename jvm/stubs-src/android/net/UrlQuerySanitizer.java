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
