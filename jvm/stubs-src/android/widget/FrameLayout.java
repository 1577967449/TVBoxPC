package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.view.ViewGroup;

/**
 * FrameLayout stub —— 容器控件（蜘蛛用它承载 WebView/ImageView）。
 * ★ 内嵌 LayoutParams 必须继承 ViewGroup.MarginLayoutParams：
 *   蜘蛛字节码对 FrameLayout$LayoutParams 的父类型有硬编码假设（check-cast），
 *   继承链错了会抛 ClassCastException。
 */
public class FrameLayout extends ViewGroup {

    public FrameLayout(Context context) {
        super(context);
    }

    public FrameLayout(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    @Override
    public void addView(View child) {
    }

    @Override
    public void addView(View child, int index) {
    }

    public void addView(View child, LayoutParams params) {
    }

    public void removeAllViews() {
    }

    public View getChildAt(int index) {
        return null;
    }

    public int getChildCount() {
        return 0;
    }

    public void setForeground(android.graphics.drawable.Drawable d) {
    }

    public void setPadding(int l, int t, int r, int b) {
    }

    public static class LayoutParams extends ViewGroup.MarginLayoutParams {

        public LayoutParams(int width, int height) {
            super(width, height);
        }

        public LayoutParams(Context c, AttributeSet attrs) {
            super(c, attrs);
        }

        public LayoutParams(ViewGroup.LayoutParams source) {
            super(source);
        }

        public LayoutParams(LayoutParams source) {
            super(source);
        }
    }
}
