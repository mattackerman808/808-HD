# Third-party notices

808 HD is licensed under the GNU General Public License v3.0 or later (see `LICENSE`).

The release package contains `libnrsc5.dll`, built from the `nrsc5` submodule of this
repository. It statically links the libraries below, each downloaded at the pinned
version by nrsc5's own build (`nrsc5/CMakeLists.txt`). The complete corresponding
source is this repository at the release tag plus those pinned upstream sources;
`build.ps1` rebuilds the exact binary.

| Component | Version | License | Source |
|---|---|---|---|
| nrsc5 (libnrsc5) | commit `9beb2c77171472c707f285f51a1385ac4c4a8be6` | GPL-3.0-or-later | https://github.com/theori-io/nrsc5 |
| FAAD2 (with nrsc5's HDC patch, `nrsc5/support/faad2-hdc-support.patch`) | 2.11.2 | GPL-2.0-or-later | https://github.com/knik0/faad2 |
| FFTW | 3.3.10 | GPL-2.0-or-later | https://www.fftw.org/ |
| librtlsdr (Osmocom) | v2.0.2 | GPL-2.0-or-later | https://gitea.osmocom.org/sdr/rtl-sdr |
| libusb | v1.0.27 | LGPL-2.1-or-later | https://github.com/libusb/libusb |
| libao | commit `cafce902a73c1050474a62feff83e428bbbee5f4` | GPL-2.0-or-later | https://github.com/xiph/libao |
| MinGW-w64 runtime | as shipped with MSYS2 UCRT64 | permissive (ZPL/MIT-style) | https://www.mingw-w64.org/ |

## Not included

- **SDR# (SDRSharp)** and the **SDR# plugin SDK** are proprietary software by Airspy. They are
  not part of this repository or its releases. The plugin is compiled against the SDK's
  reference assemblies, which you download yourself from https://airspy.com/download.

## Trademarks

HD Radio is a registered trademark of Xperi Inc. (iBiquity Digital Corporation). SDR# and
Airspy are trademarks of Airspy. 808 HD is an independent project, not affiliated with,
endorsed by, or certified by either company. These names are used only to describe what
the software works with.
