# Linux tooling for scripts/build-direct-apk.sh (deploy/nas/apk/build.sh): a JRE, Debian's zipalign, and Python with
# keystone and capstone (the AI-role caves are assembled at build time). apktool and apksigner are the
# platform-independent jars, mounted at /tools by build.sh.
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends openjdk-17-jre-headless zipalign python3 python3-pip \
 && rm -rf /var/lib/apt/lists/* \
 && pip3 install --no-cache-dir --break-system-packages keystone-engine capstone \
 && python3 -c "import keystone, capstone"
RUN printf '#!/bin/sh\nexec java -Xmx2g -jar /tools/apktool_3.0.3.jar "$@"\n' > /usr/local/bin/apktool \
 && printf '#!/bin/sh\nexec java -jar /tools/apksigner.jar "$@"\n' > /usr/local/bin/apksigner \
 && chmod +x /usr/local/bin/apktool /usr/local/bin/apksigner
