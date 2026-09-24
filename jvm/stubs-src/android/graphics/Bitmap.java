package android.graphics;

/**
 * Bitmap stub —— 真实现（可读写像素），而非"返回 null 的空壳"。
 * <p>蜘蛛用它把二维码 / 判断图片尺寸，做 null 兜底反而更安全。
 * <p>★ Bitmap$Config 必须是 enum：蜘蛛字节码里对 ARGB_8888 用的是
 *   sget-object，枚举常量编译后就是 static 字段，字段名必须完全一致。
 */
public class Bitmap {

    public enum Config {
        ALPHA_8,
        RGB_565,
        ARGB_4444,
        ARGB_8888,
        HARDWARE
    }

    private final int width;
    private final int height;
    private final Config config;
    private int[] pixels;
    private boolean recycled;

    private Bitmap(int width, int height, Config config) {
        this.width = width < 0 ? 0 : width;
        this.height = height < 0 ? 0 : height;
        this.config = config == null ? Config.ARGB_8888 : config;
        this.pixels = new int[this.width * this.height];
    }

    public static Bitmap createBitmap(int width, int height, Config config) {
        return new Bitmap(width, height, config);
    }

    public static Bitmap createBitmap(Bitmap source, int x, int y, int width, int height) {
        Bitmap b = new Bitmap(width, height, source == null ? Config.ARGB_8888 : source.config);
        if (source != null) {
            for (int j = 0; j < height; j++) {
                for (int i = 0; i < width; i++) {
                    b.setPixel(i, j, source.getPixel(x + i, y + j));
                }
            }
        }
        return b;
    }

    public static Bitmap createBitmap(int[] colors, int width, int height, Config config) {
        Bitmap b = new Bitmap(width, height, config);
        int n = Math.min(colors == null ? 0 : colors.length, width * height);
        for (int i = 0; i < n; i++) {
            b.pixels[i] = colors[i];
        }
        return b;
    }

    public void setPixels(int[] pixels, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null) {
            return;
        }
        int idx = offset;
        for (int j = 0; j < height; j++) {
            for (int i = 0; i < width; i++) {
                if (idx >= 0 && idx < pixels.length) {
                    setPixel(x + i, y + j, pixels[idx]);
                }
                idx++;
            }
            idx += stride - width;
        }
    }

    public void getPixels(int[] pixels, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null) {
            return;
        }
        int idx = offset;
        for (int j = 0; j < height; j++) {
            for (int i = 0; i < width; i++) {
                if (idx >= 0 && idx < pixels.length) {
                    pixels[idx] = getPixel(x + i, y + j);
                }
                idx++;
            }
            idx += stride - width;
        }
    }

    public int getPixel(int x, int y) {
        if (x < 0 || y < 0 || x >= width || y >= height) {
            return 0;
        }
        return pixels[y * width + x];
    }

    public void setPixel(int x, int y, int color) {
        if (x < 0 || y < 0 || x >= width || y >= height) {
            return;
        }
        pixels[y * width + x] = color;
    }

    public int getWidth() {
        return width;
    }

    public int getHeight() {
        return height;
    }

    public Config getConfig() {
        return config;
    }

    public boolean isRecycled() {
        return recycled;
    }

    public void recycle() {
        recycled = true;
    }

    public int getByteCount() {
        return width * height * 4;
    }

    public int getRowBytes() {
        return width * 4;
    }

    public static Bitmap createScaledBitmap(Bitmap src, int dstWidth, int dstHeight, boolean filter) {
        return createBitmap(dstWidth, dstHeight, src == null ? Config.ARGB_8888 : src.config);
    }

    @Override
    public String toString() {
        return "Bitmap(" + width + "x" + height + "," + config + ")";
    }
}
