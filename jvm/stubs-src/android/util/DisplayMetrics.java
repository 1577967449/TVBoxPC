package android.util;

/**
 * DisplayMetrics stub —— 桌面无 UI，给一组**自洽**的固定值。
 * <p>返回 0 会让按密度换算的逻辑（dp→px）算出 NaN/0 并污染请求参数，
 * 因此给 1080p / density=3 的合理默认。
 */
public class DisplayMetrics {

    public static final int DENSITY_LOW = 120;
    public static final int DENSITY_MEDIUM = 160;
    public static final int DENSITY_HIGH = 240;
    public static final int DENSITY_XHIGH = 320;
    public static final int DENSITY_XXHIGH = 480;
    public static final int DENSITY_XXXHIGH = 640;
    public static final int DENSITY_DEFAULT = DENSITY_MEDIUM;

    public int widthPixels = 1920;
    public int heightPixels = 1080;
    public float density = 3.0f;
    public float scaledDensity = 3.0f;
    public float xdpi = 480.0f;
    public float ydpi = 480.0f;
    public int densityDpi = DENSITY_XXHIGH;
    public int noncompatWidthPixels = 1920;
    public int noncompatHeightPixels = 1080;
    public float noncompatDensity = 3.0f;
    public int noncompatDensityDpi = DENSITY_XXHIGH;

    public void setTo(DisplayMetrics other) {
        if (other == null) {
            return;
        }
        widthPixels = other.widthPixels;
        heightPixels = other.heightPixels;
        density = other.density;
        scaledDensity = other.scaledDensity;
        xdpi = other.xdpi;
        ydpi = other.ydpi;
        densityDpi = other.densityDpi;
    }

    public void setToDefaults() {
    }

    @Override
    public String toString() {
        return "DisplayMetrics{" + widthPixels + "x" + heightPixels + ", density=" + density + "}";
    }
}
