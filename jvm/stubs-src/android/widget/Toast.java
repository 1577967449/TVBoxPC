package android.widget;

import android.content.Context;
import android.view.View;

/**
 * Toast stub —— 桌面无 UI，日志化处理（不炸、不阻塞）。
 */
public class Toast {

    public static final int LENGTH_SHORT = 0;
    public static final int LENGTH_LONG = 1;

    private CharSequence text = "";
    private int duration = LENGTH_SHORT;

    public static Toast makeText(Context context, CharSequence text, int duration) {
        Toast t = new Toast();
        t.text = text;
        t.duration = duration;
        return t;
    }

    public static Toast makeText(Context context, int resId, int duration) {
        return makeText(context, "", duration);
    }

    public void show() {
        if (text != null && text.length() > 0) {
            android.util.Log.i("Toast", text.toString());
        }
    }

    public void cancel() {
    }

    public void setText(CharSequence s) {
        this.text = s;
    }

    public void setDuration(int d) {
        this.duration = d;
    }

    public void setGravity(int gravity, int xOffset, int yOffset) {
    }

    public void setMargin(float horizontal, float vertical) {
    }

    public void setView(View view) {
    }

    public View getView() {
        return null;
    }
}
