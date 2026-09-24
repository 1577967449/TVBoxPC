package test;

import android.app.Activity;
import android.os.Bundle;

/**
 * test.MainActivity stub —— 反混淆残留的调试入口类。
 * <p>某些工具链（如 fty 壳）在 <clinit> 里 Class.forName 它做自检；
 * 缺失会抛 ClassNotFoundException（多数被 catch，但个别会污染类初始化状态）。
 * 这里提供一个空 Activity，让自检"通过但不做事"。
 */
public class MainActivity extends Activity {

    /**
     * 注意：桩 Activity 没有 onCreate(Bundle)，这里**不能加 @Override**，
     * 也不要调 super —— 否则编译期就报「The method onCreate(Bundle) is undefined」。
     */
    public void onCreate(Bundle savedInstanceState) {
    }
}
