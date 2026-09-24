package com.thegrizzlylabs.sardineandroid;

import java.io.IOException;
import java.io.InputStream;
import java.util.Collections;
import java.util.List;

/**
 * Sardine stub —— WebDAV 客户端（桌面桥不做传输）。
 * <p>所有方法都"优雅失败"：读操作返回空列表、写操作抛 IOException（与真实
 * 网络不可达一致），让上层走既有的错误分支，而不是伪装成功之后在下游 NPE。
 */
public class Sardine {

    public Sardine() {
    }

    public void setCredentials(String username, String password) {
    }

    public void setCustomizedVerifier(Object verifier) {
    }

    public void enablePreemptiveAuthentication(String host) {
    }

    public void disablePreemptiveAuthentication() {
    }

    public List<DavResource> list(String url) throws IOException {
        return Collections.emptyList();
    }

    public List<DavResource> list(String url, int depth) throws IOException {
        return Collections.emptyList();
    }

    public List<DavResource> getResources(String url) throws IOException {
        return Collections.emptyList();
    }

    public DavResource get(String url) throws IOException {
        return null;
    }

    public boolean exists(String url) throws IOException {
        return false;
    }

    public InputStream get(String url, java.util.Map<String, String> headers) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, byte[] data) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, byte[] data, String contentType) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void put(String url, InputStream dataStream) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void createDirectory(String url) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void delete(String url) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void move(String sourceUrl, String destinationUrl) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }

    public void copy(String sourceUrl, String destinationUrl) throws IOException {
        throw new IOException("WebDAV transport unavailable on desktop bridge");
    }
}
