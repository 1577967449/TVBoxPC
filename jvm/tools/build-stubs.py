# -*- coding: utf-8 -*-
"""
构建 stubs.jar —— 桌面 JVM 蜘蛛桥的 Android / com.catvod 仿真类 + SpiderRunner。

产物：<jvm>/stubs/stubs.jar

设计要点（踩过的坑都在这里）：
  1. **编译只要 ecj + JRE**，不需要完整 JDK。
     ecj（Eclipse Compiler for Java，3MB 单 jar）能直接跑在 JRE 17 上，
     省掉 ~180MB 的 JDK 依赖；这一步只在本机构建期跑一次，产物随包分发。
  2. **必须带 -sourcepath**：桩类之间大量互相引用（ViewGroup ↔ MarginLayoutParams、
     SQLiteDatabase ↔ DatabaseErrorHandler），只给 -cp 会"找不到符号"。
  3. **依赖 jar 必须上 classpath**：SpiderApi 依赖 gson + okhttp，Spider 依赖
     okhttp 的 Dns/OkHttpClient；缺了报"程序包 com.google.gson 不存在"。
  4. **显式清单打包**：只收 .class / META-INF，避免把 files.txt、out.jar 自己
     打进去（会产生嵌套损坏条目 → jar xf 报 EOFException）。
  5. 参数：python build-stubs.py <jvm 目录> [<java 可执行文件>]

用法：
    python build-stubs.py C:\\...\\TVBoxPC\\jvm
"""
import io
import os
import subprocess
import sys


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    jvm = os.path.abspath(sys.argv[1])
    java = sys.argv[2] if len(sys.argv) > 2 else None

    stubs_src = os.path.join(jvm, 'stubs-src')
    tools = os.path.join(jvm, 'tools')
    libs = os.path.join(jvm, 'libs')
    out_dir = os.path.join(jvm, 'stubs')
    work = os.path.join(jvm, '.build')

    if java is None:
        cand = os.path.join(jvm, 'jre', 'bin', 'java.exe')
        if not os.path.exists(cand):
            cand = os.path.join(jvm, 'jre', 'bin', 'java')
        java = cand
    if not os.path.exists(java):
        print('找不到 java 可执行文件：%s' % java)
        return 3

    ecj = os.path.join(tools, 'ecj.jar')
    if not os.path.exists(ecj):
        print('缺少编译器 %s' % ecj)
        return 4

    # ---------- 1) 收集源码（stubs-src 全部 + tools/SpiderRunner.java）----------
    # ★ 排除 stubs-src/org/json —— 那套桩不全（缺 JSONException/JSONTokener），
    #   改用官方 org.json:json-20231013.jar（在 libs/ 里），API 与 Android 版一致。
    sources = []
    skip_prefix = os.path.join(stubs_src, 'org', 'json') + os.sep
    for root, _dirs, files in os.walk(stubs_src):
        for f in files:
            if f.endswith('.java'):
                p = os.path.join(root, f)
                if p.startswith(skip_prefix):
                    continue
                sources.append(p)
    runner = os.path.join(tools, 'SpiderRunner.java')
    if os.path.exists(runner):
        sources.append(runner)
    sources.sort()
    print('源码文件：%d 个' % len(sources))

    # ---------- 2) classpath ----------
    libjars = []
    if os.path.isdir(libs):
        for f in sorted(os.listdir(libs)):
            if f.lower().endswith('.jar'):
                libjars.append(os.path.join(libs, f))
    print('依赖 jar：%d 个' % len(libjars))

    # ---------- 3) 编译 ----------
    if os.path.isdir(work):
        for root, dirs, files in os.walk(work, topdown=False):
            for f in files:
                os.remove(os.path.join(root, f))
            for d in dirs:
                os.rmdir(os.path.join(root, d))
    else:
        os.makedirs(work)

    files_txt = os.path.join(work, 'files.txt')
    with io.open(files_txt, 'w', encoding='utf-8', newline='\n') as f:
        for s in sources:
            f.write(s.replace('\\', '/') + '\n')

    cmd = [java, '-jar', ecj,
           '-source', '17', '-target', '17',
           '-encoding', 'UTF-8', '-nowarn',
           '-proceedOnError:Fatal',
           '-cp', ';'.join(libjars),
           '-sourcepath', stubs_src,
           '-d', work,
           '@' + files_txt]
    print('编译中…')
    p = subprocess.run(cmd, capture_output=True)
    out = (p.stdout or b'').decode('utf-8', 'replace')
    err = (p.stderr or b'').decode('utf-8', 'replace')
    errs = [l for l in (out + '\n' + err).splitlines() if ' ERROR in ' in l]
    if errs:
        print('❌ 编译错误 %d 处，拒绝出包（半成品 stubs.jar 会导致运行期 NoSuchMethodError）：' % len(errs))
        detail = [l for l in (out + '\n' + err).splitlines()
                  if l.strip().startswith(tuple('%d.' % i for i in range(1, 200)))]
        for l in detail[:60]:
            print('   ', l.strip())
        return 7
    ncls = 0
    for root, _dirs, files in os.walk(work):
        ncls += sum(1 for f in files if f.endswith('.class'))
    print('产出 .class：%d 个' % ncls)
    if ncls == 0:
        print(out[-4000:])
        print(err[-4000:])
        return 5

    # ---------- 4) 打包（显式清单，只收 .class 与 META-INF）----------
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_jar = os.path.join(out_dir, 'stubs.jar')
    if os.path.exists(out_jar):
        os.remove(out_jar)

    # ★ 用 Python 的 zipfile 打包，而不是 JDK 的 jar 工具：
    #   Temurin 的 **JRE** 发行版不含 bin/jar（只有 java/keytool/jfr 等），
    #   为了不额外背一个 180MB 的 JDK 依赖，直接用 zip 格式写 jar（格式完全等价）。
    #   注意条目名必须用正斜杠、且不加目录条目，否则部分工具读不出。
    import zipfile

    entries = []
    for root, _dirs, files in os.walk(work):
        for f in files:
            if not f.endswith('.class'):
                continue
            full = os.path.join(root, f)
            entries.append(os.path.relpath(full, work).replace('\\', '/'))
    entries.sort()
    with zipfile.ZipFile(out_jar, 'w', zipfile.ZIP_DEFLATED) as z:
        for e in entries:
            z.write(os.path.join(work, e.replace('/', os.sep)), e)
    if not os.path.exists(out_jar):
        print('打包失败')
        return 6

    print('✅ stubs.jar 已生成：%s (%.1f MB, %d 类)'
          % (out_jar, os.path.getsize(out_jar) / 1048576.0, ncls))
    return 0


if __name__ == '__main__':
    sys.exit(main())
