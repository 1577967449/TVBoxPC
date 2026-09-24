package com.google.net.cronet.okhttptransport;

/** CronetInterceptor 的 Builder 占位（部分代码用 Builder 形态构造）。 */
public class CronetInterceptorBuilder {

    private Object engine;

    public CronetInterceptorBuilder(Object engine) {
        this.engine = engine;
    }

    public CronetInterceptor build() {
        return new CronetInterceptor(engine);
    }
}
