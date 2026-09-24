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
