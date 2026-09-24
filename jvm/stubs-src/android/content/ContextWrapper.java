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
