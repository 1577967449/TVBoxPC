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
