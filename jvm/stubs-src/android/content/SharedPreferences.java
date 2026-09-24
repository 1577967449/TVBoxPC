package android.content;

import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * SharedPreferences stub —— 内存实现（桌面版无持久化必要）。
 *
 * ★ 必须是 interface（与 AOSP 一致）：蜘蛛字节码对 edit()/getString() 用的是
 *   invoke-interface，若写成 class 会抛 IncompatibleClassChangeError。
 * ★ 必须真实存取：不少蜘蛛把 token / 时间戳写进去再读回来做增量判断，
 *   若 put 空实现、get 返默认值，表现为"每次都是首次运行"的隐蔽错误。
 */
public interface SharedPreferences {

    Map<String, ?> getAll();

    String getString(String key, String defValue);

    int getInt(String key, int defValue);

    long getLong(String key, long defValue);

    float getFloat(String key, float defValue);

    boolean getBoolean(String key, boolean defValue);

    boolean contains(String key);

    Editor edit();

    void registerOnSharedPreferenceChangeListener(Object listener);

    void unregisterOnSharedPreferenceChangeListener(Object listener);

    interface Editor {
        Editor putString(String key, String value);

        Editor putInt(String key, int value);

        Editor putLong(String key, long value);

        Editor putFloat(String key, float value);

        Editor putBoolean(String key, boolean value);

        Editor remove(String key);

        Editor clear();

        boolean commit();

        void apply();
    }

    /** 按名字取（同名共享同一实例），供 Context.getSharedPreferences 调用。 */
    class Mem {

        private static final Map<String, SharedPreferences> MAP = new ConcurrentHashMap<String, SharedPreferences>();

        public static SharedPreferences get(String name) {
            String key = name == null ? "default" : name;
            synchronized (MAP) {
                SharedPreferences sp = MAP.get(key);
                if (sp == null) {
                    sp = new Impl(key);
                    MAP.put(key, sp);
                }
                return sp;
            }
        }

        static final class Impl implements SharedPreferences {

            private final Map<String, Object> data = new HashMap<String, Object>();
            private final String name;

            Impl(String name) {
                this.name = name;
            }

            @Override
            public Map<String, ?> getAll() {
                synchronized (data) {
                    return new HashMap<String, Object>(data);
                }
            }

            @Override
            public String getString(String key, String defValue) {
                Object v = get(key);
                return v instanceof String ? (String) v : defValue;
            }

            @Override
            public int getInt(String key, int defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).intValue() : defValue;
            }

            @Override
            public long getLong(String key, long defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).longValue() : defValue;
            }

            @Override
            public float getFloat(String key, float defValue) {
                Object v = get(key);
                return v instanceof Number ? ((Number) v).floatValue() : defValue;
            }

            @Override
            public boolean getBoolean(String key, boolean defValue) {
                Object v = get(key);
                return v instanceof Boolean ? (Boolean) v : defValue;
            }

            @Override
            public boolean contains(String key) {
                synchronized (data) {
                    return data.containsKey(key);
                }
            }

            @Override
            public Editor edit() {
                return new EditorImpl(this);
            }

            @Override
            public void registerOnSharedPreferenceChangeListener(Object listener) {
            }

            @Override
            public void unregisterOnSharedPreferenceChangeListener(Object listener) {
            }

            private Object get(String key) {
                synchronized (data) {
                    return data.get(key);
                }
            }

            @Override
            public String toString() {
                return "SharedPreferences(" + name + ")";
            }
        }

        static final class EditorImpl implements Editor {

            private final Impl owner;
            private final Map<String, Object> staged = new HashMap<String, Object>();
            private boolean cleared;

            EditorImpl(Impl owner) {
                this.owner = owner;
            }

            @Override
            public Editor putString(String key, String value) {
                staged.put(key, value);
                return this;
            }

            @Override
            public Editor putInt(String key, int value) {
                staged.put(key, Integer.valueOf(value));
                return this;
            }

            @Override
            public Editor putLong(String key, long value) {
                staged.put(key, Long.valueOf(value));
                return this;
            }

            @Override
            public Editor putFloat(String key, float value) {
                staged.put(key, Float.valueOf(value));
                return this;
            }

            @Override
            public Editor putBoolean(String key, boolean value) {
                staged.put(key, Boolean.valueOf(value));
                return this;
            }

            @Override
            public Editor remove(String key) {
                staged.put(key, null);
                return this;
            }

            @Override
            public Editor clear() {
                cleared = true;
                return this;
            }

            @Override
            public boolean commit() {
                apply();
                return true;
            }

            @Override
            public void apply() {
                synchronized (owner.data) {
                    if (cleared) {
                        owner.data.clear();
                        cleared = false;
                    }
                    for (Map.Entry<String, Object> e : staged.entrySet()) {
                        if (e.getValue() == null) {
                            owner.data.remove(e.getKey());
                        } else {
                            owner.data.put(e.getKey(), e.getValue());
                        }
                    }
                    staged.clear();
                }
            }
        }
    }
}
