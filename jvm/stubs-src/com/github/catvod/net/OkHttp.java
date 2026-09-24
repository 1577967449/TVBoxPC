package com.github.catvod.net;

import com.github.catvod.utils.Util;

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
