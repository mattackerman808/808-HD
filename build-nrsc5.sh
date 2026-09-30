#!/usr/bin/env bash
# Builds nrsc5 (CLI + libnrsc5.dll) from the nrsc5 submodule with the MSYS2 UCRT64 toolchain.
# Normally run by build.ps1. By hand, from an MSYS2 UCRT64 shell in the repo root:
#   ./build-nrsc5.sh
# One-time toolchain setup (MSYS2 UCRT64 shell):
#   pacman -S --needed autoconf automake git make patch mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-libtool
set -e
cd "$(dirname "$0")/nrsc5"
mkdir -p build
cd build
cmake -G "MSYS Makefiles" \
    -D USE_STATIC=ON \
    -D USE_SYSTEM_LIBUSB=OFF \
    -D USE_SYSTEM_RTLSDR=OFF \
    -D USE_SYSTEM_LIBAO=OFF \
    -D USE_SYSTEM_FFTW=OFF \
    -D USE_SSE=ON \
    ..
make -j"$(nproc)"
echo "=== built: $(pwd)/src/libnrsc5.dll"
