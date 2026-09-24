package android.webkit;

/**
 * ValueCallback stub —— WebView.evaluateJavascript 的回调接口。
 * ★ 必须是 interface（调用点用 invoke-interface）。
 */
public interface ValueCallback<T> {
    void onReceiveValue(T value);
}
