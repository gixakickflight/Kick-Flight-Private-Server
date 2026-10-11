# Luxon uses C++23 explicit-object parameters and std::format, requiring GCC 14.
FROM gcc:14-bookworm AS build
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends cmake ninja-build git patch \
    && rm -rf /var/lib/apt/lists/*
COPY submodules/luxonserver/ ./
COPY deploy/photon/mbedtls-gcc14.patch /tmp/mbedtls-gcc14.patch
# Kick-Flight ENet transport patches for the nested Luxon submodule (pinned at 5401810): 0006 = 4096-entry reliable
# receive window, 0007 = 32 resends / 30 s. The other server patches (0001-0005, 0008-0010) are already commits in the
# luxonserver pin. `git apply` is all-or-nothing and rejects context drift, so a submodule bump that conflicts with a
# patch fails the build instead of shipping without it; the grep checks prove the values actually landed.
COPY deploy/photon/luxon-patches/ /tmp/luxon-patches/
RUN set -eu; \
    cd Luxon; \
    for p in /tmp/luxon-patches/*.patch; do \
      echo "Applying ${p##*/}"; \
      git apply --verbose "$p"; \
    done; \
    grep -q 'sliding_flat_map<uint32_t, EnetCommand, 4096> incoming_reliable;' include/luxon/enet_peer.hpp; \
    grep -q 'uint8_t max_resends = 32;' include/luxon/enet_peer.hpp; \
    grep -q 'int disconnect_timeout_ms = 30000;' include/luxon/enet_peer.hpp
RUN cmake -S . -B build -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DLUXON_SERVER_USE_SANMAKE=OFF \
      -DLUXON_SERVER_ENABLE_MULTIPROCESSING=OFF \
    && patch --directory=build/_deps/mbedtls-src --strip=1 --forward --batch --fuzz=0 < /tmp/mbedtls-gcc14.patch \
    && cmake --build build --target luxon_server --parallel 2 \
    && mkdir -p /opt/photon-runtime \
    && cp -L "$(g++ -print-file-name=libstdc++.so.6)" /opt/photon-runtime/libstdc++.so.6 \
    && cp -L "$(g++ -print-file-name=libgcc_s.so.1)" /opt/photon-runtime/libgcc_s.so.1

FROM debian:bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends gettext-base libstdc++6 ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /etc/luxon
COPY --from=build /src/build/luxon_server /usr/local/bin/luxon_server
COPY --from=build /opt/photon-runtime/ /usr/local/lib/photon-runtime/
ENV LD_LIBRARY_PATH=/usr/local/lib/photon-runtime
WORKDIR /etc/luxon
ENTRYPOINT ["/usr/local/bin/luxon_server"]
