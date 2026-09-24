package android.widget;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.drawable.Drawable;
import android.util.AttributeSet;
import android.view.View;

/**
 * ImageView stub —— 图片控件。
 * ★ 内嵌 ScaleType 必须是 enum：蜘蛛对 CENTER_CROP/FIT_CENTER 用的是
 *   sget-object，枚举常量 = static 字段，名字必须齐全。
 */
public class ImageView extends View {

    public enum ScaleType {
        MATRIX,
        FIT_XY,
        FIT_START,
        FIT_CENTER,
        FIT_END,
        CENTER,
        CENTER_CROP,
        CENTER_INSIDE
    }

    public ImageView(Context context) {
        super(context);
    }

    public ImageView(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    public void setImageBitmap(Bitmap bm) {
    }

    public void setImageDrawable(Drawable drawable) {
    }

    public void setImageResource(int resId) {
    }

    public void setImageURI(android.net.Uri uri) {
    }

    public void setScaleType(ScaleType scaleType) {
    }

    public ScaleType getScaleType() {
        return ScaleType.FIT_CENTER;
    }

    public void setAdjustViewBounds(boolean adjustViewBounds) {
    }

    public Drawable getDrawable() {
        return null;
    }

    public void setMaxWidth(int maxWidth) {
    }

    public void setMaxHeight(int maxHeight) {
    }
}
