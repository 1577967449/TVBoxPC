package com.google.net.cronet.okhttptransport;

/**
 * CronetInterceptor stub —— Cronet 传输拦截器（Android 专有）。
 * <p>个别蜘蛛（Bili 等）用 okhttp3 的 Builder 链式挂它做降级；桌面版没有 Cronet，
 * 这里提供空的 Interceptor 实现，保证 addInterceptor(new CronetInterceptor(...)) 能编译期通过、
 * 运行期只用标准 Socket 传输。
 */
public class CronetInterceptor implements okhttp3.Interceptor {

    public CronetInterceptor(Object engine) {
    }

    public CronetInterceptor(Object engine, Object executor) {
    }

    @Override
    public okhttp3.Response intercept(okhttp3.Interceptor.Chain chain) throws java.io.IOException {
        return chain.proceed(chain.request());
    }
}
