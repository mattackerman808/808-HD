# Changelog

## v0.1.1

- Fix HD stereo being left/right mirrored relative to the analog audio, so the stereo image flipped
  at every analog/HD switch. On every station tested, nrsc5's HD output was swapped against a
  standards-correct analog stereo decode (likely a pan-sign difference in the DRM parametric-stereo
  code nrsc5 reuses for HDC). The plugin now swaps the HD channels.
- Fix occasional false time-alignment lock right after tuning, which made HD play out of step with
  the analog (heard as a jump or echo at the switch) for up to 20 s. The search now only considers
  HD running ahead of the analog, and the first lock needs two agreeing measurements. HD now takes
  over about 2 s later after tuning.

## v0.1.0 - first release

- HD Radio (NRSC-5, FM) decoding inside SDR# via libnrsc5, from any SDR source at 1 MS/s or more.
- HD audio plays through SDR#'s own audio chain: SDR#'s volume, mute and output device apply.
- Analog/HD blend like a real receiver:
  - automatic time alignment of HD to the analog audio (measured per station),
  - crossfades ahead of signal gaps, loudness matching,
  - weak-signal hysteresis so marginal stations don't flap,
  - "Auto HD / Analog" switch.
- Now-playing strip above the spectrum: station, slogan, song, album art / station logo,
  HD1-HD8 program buttons, MER signal meter, RDS fallback for analog stations.
- Weather/traffic maps and emergency alerts, where stations broadcast them.
