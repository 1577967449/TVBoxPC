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
