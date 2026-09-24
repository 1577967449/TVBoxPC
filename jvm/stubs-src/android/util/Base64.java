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
