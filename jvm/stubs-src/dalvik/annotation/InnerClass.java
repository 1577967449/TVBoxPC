package dalvik.annotation;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * InnerClass —— 仅供 dex2jar 产物里的编译期注解使用。
 *
 * ★ 故意用 RetentionPolicy.CLASS（而非 AOSP 的 RUNTIME）：
 *   这些注解只在"反编译/反射元数据"层面有意义，桌面宿主从不读它。
 *   设为 CLASS 后 JVM 加载类时**不会**去解析注解值 —— 即使 dex2jar 把
 *   值写成另一种形态（String vs Class），也不会抛 AnnotationTypeMismatchException，
 *   把一类纯噪音故障消灭在源头。
 */
@Retention(RetentionPolicy.CLASS)
@Target({ElementType.TYPE, ElementType.METHOD, ElementType.CONSTRUCTOR, ElementType.FIELD})
public @interface InnerClass {
    String name();

    int accessFlags();
}
