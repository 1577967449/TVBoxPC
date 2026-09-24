package com.thegrizzlylabs.sardineandroid;

import java.util.ArrayList;
import java.util.Date;
import java.util.List;

/**
 * DavResource stub —— WebDAV 资源模型（AList / WebDAV 源用）。
 * <p>桌面版不做 WebDAV 传输（也无需付 4MB 依赖），但字节码契约要求类存在；
 * 字段按 Sardine 真实语义给默认值，保证 getter 不返回 null。
 */
public class DavResource {

    private String path;
    private String name;
    private String contentType;
    private String etag;
    private Date modified;
    private Date created;
    private long contentLength;
    private List<String> privileges = new ArrayList<String>();

    public String getPath() {
        return path == null ? "" : path;
    }

    public void setPath(String path) {
        this.path = path;
    }

    public String getName() {
        return name == null ? "" : name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public String getContentType() {
        return contentType == null ? "" : contentType;
    }

    public void setContentType(String contentType) {
        this.contentType = contentType;
    }

    public String getEtag() {
        return etag == null ? "" : etag;
    }

    public void setEtag(String etag) {
        this.etag = etag;
    }

    public Date getModified() {
        return modified;
    }

    public void setModified(Date modified) {
        this.modified = modified;
    }

    public Date getCreation() {
        return created;
    }

    public void setCreation(Date created) {
        this.created = created;
    }

    public long getContentLength() {
        return contentLength;
    }

    public void setContentLength(long contentLength) {
        this.contentLength = contentLength;
    }

    public List<String> getPrivileges() {
        return privileges;
    }

    public boolean isDirectory() {
        return contentType != null && contentType.endsWith("directory");
    }

    @Override
    public String toString() {
        return getPath();
    }
}
