package dalvik.system;

import java.util.Collections;
import java.util.Enumeration;

/**
 * DexFile stub —— 桌面版没有 Dalvik，动态 dex 加载降级为 Class.forName。
 * <p>蜘蛛里出现它的地方通常是"壳"分支（壳在本进程里加载另一个 dex），
 * 桌面桥已由 dex2jar 把 dex 静态转成了 .class，因此这里的 loadDex 只是
 * 让分支「不抛异常地走通」，真正取类走 loadClass。
 */
public final class DexFile {

    private String name;
    private ClassLoader loader;

    private DexFile() {
    }

    public static DexFile loadDex(String sourcePathName, String outputPathName, int flags)
            throws java.io.IOException {
        if (sourcePathName == null || !new java.io.File(sourcePathName).exists()) {
            throw new java.io.IOException("dex file not found: " + sourcePathName);
        }
        DexFile f = new DexFile();
        f.name = sourcePathName;
        return f;
    }

    public Class<?> loadClass(String name, ClassLoader loader) {
        this.loader = loader;
        try {
            ClassLoader cl = loader != null ? loader : DexFile.class.getClassLoader();
            return Class.forName(name, false, cl);
        } catch (ClassNotFoundException e) {
            return null;
        }
    }

    public Class<?> loadClass(String name) {
        return loadClass(name, null);
    }

    public String getName() {
        return name;
    }

    public void close() throws java.io.IOException {
    }

    public Enumeration<String> entries() {
        return Collections.emptyEnumeration();
    }

    public boolean isDexOptNeeded(String fileName) {
        return false;
    }
}
