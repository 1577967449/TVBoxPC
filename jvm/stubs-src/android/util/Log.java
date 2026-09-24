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
