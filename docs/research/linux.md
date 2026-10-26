# Linux research: capture, render, audio, stack, distribution, WSL dev loop

_2026-10-23. Two research passes (the WSL2/WSLg dev loop; the Linux capture/UI/audio/packaging stack) plus a survey of this PC and our code. Anything marked *unconfirmed* came from a single source or from reasoning, not a measurement, and has to be checked in the spike that owns it. Decisions go into `docs/spec.md` §14 and, after the spikes, `docs/adr/0006-linux-stack.md` (task 112)._

**Proposed decisions (2026-10-23; to be confirmed by the user and ADR 0006):**
- **Stack:** C# on .NET 10 with **GTK4 + libadwaita** through **Gir.Core**. The app reuses `HDMICaptain.Core` (the Windows port of the pure logic) unchanged, and reuses the Android **C++ GLES 3 renderer** inside a `GtkGLArea`.
- **Dev loop:** build and run inside **WSL2 (Ubuntu 24.04) on the Windows dev PC**. The window appears on the Windows desktop through WSLg. The capture card goes to WSL with **usbipd-win**. A real Linux install is still needed for the final latency and soak numbers.
- **Distribution:** Flatpak (Flathub) first, plus a `.deb`/`.rpm` that can install a udev rule. Pro is a **licence key** sold outside the store, because Flathub has no payments.

## 1. Summary

- **Capture:** raw **V4L2** through P/Invoke `ioctl`:
  - mmap buffers (≥ 4), `poll`, newest frame wins;
  - drop buffers with `bytesused == 0` or `V4L2_BUF_FLAG_ERROR`;
  - an unplug shows up as `ENODEV`/`POLLERR`.
  - The PipeWire camera portal is a second backend, added later for a stricter sandbox. That's what GNOME Snapshot and OBS 30.1+ "Video Capture Device (PipeWire)" use. Whether the portal passes MJPEG/NV12 at 60 fps is unconfirmed.
  - OBS `linux-v4l2` uses libv4l2 and libudev. We don't need libv4lconvert, because we convert YUYV/NV12/MJPEG ourselves.
- **MJPEG:** libjpeg-turbo (system `libturbojpeg`) to planar YCbCr on the CPU, the same as Windows 090. VA-API JPEG decode is an opt-in idea to benchmark later.
  - **Linux gives us no decoded twin.** Windows hands the UGREEN-25854 to us as decoded NV12 1080p60 (`windows.md` §2b), but on Linux uvcvideo exposes only the card's native types. This card is USB 2 high speed (Actions Micro `1de1:f105`, not MacroSilicon), so 1080p60 is MJPEG only and our decoder is on the hot path from day one.
- **Render:** the Android GLES 3 renderer (`Apps/Android/core/render/src/main/cpp/renderer.cpp`, two passes: convert at source size, then scale), moved into a platform-neutral C++ library. On Linux it runs in a `GtkGLArea` set to GLES (`SetAllowedApis`, GTK 4.12+).
  - Frames are uploaded from the mmap buffers; zero-copy dmabuf isn't worth it at 1080p60.
  - A later step is `GtkGraphicsOffload` + `GdkDmabufTextureBuilder` (GTK 4.14+) for direct scanout in full screen. Gir.Core binds the builder's setters but not `Build()`, so that needs one hand-written P/Invoke.
- **Audio:** the card's UAC capture to the default output, through the **PulseAudio API** (`libpulse`). It works on PipeWire desktops (pipewire-pulse) and in WSLg (`PULSE_SERVER=unix:/mnt/wslg/PulseServer`).
  - Capture may need **ALSA `hw:` directly** where no sound server owns the card (WSL), so 111 tries both.
  - Ring buffer, drift controller, resampler, channel modes and meters are reused from `HDMICaptain.Core/Audio`.
- **Shell:** GTK4 + libadwaita through Gir.Core (0.8.x stable; 0.9 previews target GNOME 50 / GTK 4.22 / Adw 1.9; .NET 10 since 0.7).
  - Gir.Core is pre-1.0, and NativeAOT support is unconfirmed. Plan on JIT + ReadyToRun.
- **Outputs:**
  - Screenshots from the clean pass-1 texture (project rule).
  - Recording and Save Recent through our own **LGPL FFmpeg** build (VA-API `h264_vaapi`, NVENC through ffnvcodec, native `aac`, OpenH264 fallback) via P/Invoke. GStreamer through Gir.Core is the alternative.
- **Ship:**
  - **Flatpak** (GNOME runtime, `org.freedesktop.Sdk.Extension.dotnet10`, offline NuGet sources from `flatpak-dotnet-generator`, `--device=all`).
  - A **.deb/.rpm** that also installs a udev rule (autosuspend off, group access) and optionally v4l2loopback.
  - Pro is a licence key (Lemon Squeezy License API, or Paddle + Keygen). Flathub's policy allows apps that unlock features with a payment made elsewhere, if the cost is stated up front and reviewers get a key.

## 2. Capture

### 2a. API choice

| API | Verdict | Why |
|---|---|---|
| Raw V4L2 ioctls (mmap, poll) | **Primary** | Full control of format, buffer count and errors. Exposes every native type and interval. No extra library. Needs `/dev/video*`, which means `--device=all` in Flatpak |
| libv4l2 | No | Mostly passthrough plus libv4lconvert, which we don't need |
| PipeWire camera portal (`org.freedesktop.portal.Camera` → `OpenPipeWireRemote`) | **Second backend, later** | Sandbox-friendly and the GNOME direction. No per-device picker (you choose among PipeWire nodes). An OBS 32.x issue shows negotiation failing on an I420-only device. MJPEG/NV12 at 60 fps for capture cards is *unconfirmed* |
| GStreamer `v4l2src` | No | Adds a pipeline and its own buffering between us and the frame |

### 2b. Formats, quirks, power

- **Enumeration:** `VIDIOC_QUERYCAP` (`bus_info` = USB path, stable across replug on the same port), `ENUM_FMT` / `ENUM_FRAMESIZES` / `ENUM_FRAMEINTERVALS`. `FormatSelector` already understands `NV12`, `YUYV`/`YUY2` and `MJPG` (`docs/fixtures/README.md`).
- **USB 2 limit:** isochronous transfer peaks at about 24.5 MB/s, and YUYV 1080p60 needs 249 MB/s. So USB 2 cards (MS2109, MS2131, UGREEN-25854) do 1080p30/60 as MJPEG only. `FormatSelector`'s link-bandwidth rule applies. The speed is in sysfs (`/sys/bus/usb/devices/<port>/speed`).
- **MS2109 (`534d:2109`):** the kernel quirk (mainline ~5.8, backported to 4.14+) reinterprets 96 kHz mono as 48 kHz stereo and fixes the half-frame channel phase.
  - On old kernels, our `AudioFormatFix` (fixture `ms2109-audio.json`) does it instead.
  - Some MS2109 firmware revisions show no video node because snd-usb-audio grabs the device first. A udev rule fixes that.
- **MS2130/MS2131 (`345f:2130`/`2131`):** UVC 1.00, 2 video nodes, an ALSA card, HID. YUYV 1080p60 on USB 3 is confirmed on Linux. Repeated 0-byte buffers have been seen; drop them. No snd-usb-audio quirk is known (*unconfirmed*).
- **Autosuspend:**
  - uvcvideo enables USB autosuspend at probe, and some cards then power-cycle and disconnect.
  - The `.deb`/`.rpm` ships a udev rule setting `power/control=on` for known capture VID:PIDs. In Flatpak we detect `power/control=auto` and show advice. TLP and `powertop --auto-tune` turn autosuspend back on.
  - For USB 3 dropouts the known fix is `usbcore.quirks=VID:PID:k` (no LPM).
- **Hot-plug:**
  - On the host: a libudev monitor on `video4linux` and `sound`.
  - **Inside Flatpak the netlink monitor is unreliable** (no uid 0 in the user namespace; SDL and Jami hit this). Use inotify on `/dev` plus a `/sys/class/video4linux` rescan.
  - Identify the device by VID:PID + serial (or name), with the USB path as the tiebreaker. Never use `/dev/videoN`: the number changes on replug and resume.
- **In use:** a second opener gets `EBUSY` on `VIDIOC_S_FMT`/`STREAMON`. Map it to the existing "in use" pause with retry, as on Windows.

### 2c. Error table (108/113)

Measured 2026-10-26 with the UGREEN-25854 over usbip in WSL (kernel 6.6, uvcvideo), Jetson Nano as the source:

| Event | What V4L2 / ALSA report | State |
|---|---|---|
| usbipd detach while streaming (WSL; the same path as a cable pull) | `DQBUF` → `ENODEV` within 0.2 s; ALSA `readi` → `EIO` first | recovering → waiting for a card; back to live 2.9 s after the next attach |
| Another app streams the card | `VIDIOC_STREAMON`/open → `EBUSY` | paused: in use, retried every 3 s; live again as soon as it is released |
| No permission on `/dev/videoN` (not in the `video` group) | open → `EACCES`, also when enumerating | can't open (`usermod` hint); live within 3 s once access is granted. Was "no card" until the catalog listed denied nodes (fixed) |
| Start of a stream | first `DQBUF` after `STREAMON` can take ~1 s; sometimes one non-JPEG buffer | counted as rejected; no state change |
| Card unplugged on a real install | *pending (124)*; expected the same `ENODEV` path | recovering → waiting |
| HDMI source unplugged (card stays) | *pending* (card dependent: frozen, black or no buffers) | no signal |
| Suspend/resume | *pending (124)* (expect re-enumeration, new `/dev/videoN`) | recovering |

The Windows app on the same PC sees the card leave on attach (`MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED`, then
"unplugged, waiting"). On detach it reopened the card at +0.7 s, got no frames for 6 s (the card restarting after
re-enumeration), and its no-frames watchdog reopened it: live at +7.2 s.

## 3. Render

- **Reuse the Android renderer.** `renderer.cpp`/`renderer.h`/`frame.h`/`geometry.h`/`test_pattern.cpp` are ~1.2 k lines of GLES 3 + EGL. Only three things tie them to Android:
  - `ANativeWindow`;
  - `android/log.h`;
  - `android/performance_hint.h` / `trace.h`.
- **Plan (107):**
  - Move the code into a shared `native/render-gles/` with a small platform header for logging, tracing and the surface.
  - Android keeps its JNI glue. Linux adds a C ABI for P/Invoke.
  - On Linux the GL context belongs to `GtkGLArea`. The renderer renders into its framebuffer instead of owning an EGL window surface, so the "own the surface" part becomes optional.
  - If touching Android is too risky, fork it and log why.
- **Shader maths** match `MetalRenderer`: BT.601/709, auto/limited/full range, adjustments, `.cube` LUT, nearest/linear, fit/fill/stretch/integer. Geometry comes from `HDMICaptain.Core.RenderGeometry` (fixtures) or the C++ `geometry.h`, which have to agree.
- **Wayland limits:**
  - **No aspect-locked resize:** GTK4 removed geometry hints, so we letterbox instead.
  - **No always-on-top:** there is no standard protocol (wayland-protocols MR !369 is open, status *unconfirmed*). GNOME and KDE offer it in the window menu. Under X11 we set `_NET_WM_STATE_ABOVE` ourselves.
  - **Full screen:** works.
  - **Idle inhibit:** `gtk_application_inhibit(IDLE)` or the Inhibit portal.
  - **Global shortcuts:** GlobalShortcuts portal v2 (KDE yes; GNOME *unconfirmed*).
- **Toolkit alternatives considered:**
  - **Avalonia 12.1:** native Wayland is experimental and opt-in, and the default is XWayland. It wouldn't look native on GNOME.
  - **Qt 6 / QML:** solid, but a C++ app that shares nothing with our C# code.
  - **Rust + gtk4-rs:** the best Linux bindings, but a fourth port of the pure logic.
  - **Decision:** GTK4 + Gir.Core reuses the most code (C# Core, C++ renderer) and looks native on GNOME.

## 4. Audio

- PipeWire owns the devices on mainstream desktops, so opening ALSA `hw:` directly fails or fights while PipeWire holds the card. The Pulse API through pipewire-pulse is the portable choice. A native `pw_stream` (C shim; the SPA API is mostly macros) is the low-latency upgrade if Pulse can't hold ~20 ms.
- **Pairing:** match the card's ALSA card and its V4L2 node by their common USB parent in sysfs or udev `ID_PATH`. PipeWire exposes `api.v4l2.cap.bus_info` on video nodes. `device.bus-path` on audio nodes is likely but *unconfirmed*, so check with `pw-dump`. OBS doesn't pair; users pick the audio by hand.
- **WSL:** WSLg runs PulseAudio in its system distro, bridged to Windows over RDP, so playback works with any Pulse client. That server can't see ALSA devices in our distro, so **card capture in WSL means ALSA `hw:` directly** (nothing else holds it there). Expect tens of ms extra on the RDP leg. Crackle reports were fixed with `PULSE_LATENCY_MSEC=60`. WSL latency doesn't count toward targets.

## 5. Outputs & Pro

- **Encoding:** our own LGPL FFmpeg build (`h264_vaapi`, `h264_nvenc` through ffnvcodec, `aac`, OpenH264). Flatpak runtimes leave out patent-encumbered codecs; `org.freedesktop.Platform.ffmpeg-full` and codecs-extra are the runtime route, and Mesa VA-API codecs now have to be enabled explicitly. 120 decides whether to bundle FFmpeg or use the runtime extension.
- **Save Recent:** an encoded ring trimmed at keyframes and remuxed on save, like `RollingRecorder`.
- **Virtual camera (later, hidden like Windows):**
  - v4l2loopback (DKMS, root, `.deb` only; recent breakage with 0.14.0 and PipeWire negotiation in 2025).
  - Or a PipeWire `Video/Source` node with the Camera role, which portal/PipeWire apps see and raw-V4L2 apps don't.
  - As a dev test source, the PipeWire node is the realistic option in WSL. The WSL kernel has no v4l2loopback.
- **Pro:** a licence key.
  - **Flathub:** payments drafted with Stripe but not live (Nov 2025; no 2026 launch found). Paid unlocks are allowed if disclosed, priced reasonably and reviewable.
  - **Snap Store:** no paid snaps.
  - **Vendors:** Lemon Squeezy License API (activate → instance id → validate; owned by Stripe), or Paddle + Keygen.
  - Same single gate as `EntitlementStore.gate`; cached for offline use; DEBUG `-debugUnlockPro YES`.
  - ADR 0004 (monetization) needs a Linux section. The vendor is the user's decision.

## 6. Lifecycle

- **Sleep/resume:**
  - Take a logind delay inhibitor (`sleep`/`delay`). On `PrepareForSleep(true)` close the stream cleanly and release the lock.
  - On `PrepareForSleep(false)` go to `recovering`, which re-finds the device by key. That already covers the new `/dev/videoN`.
- **While live:** inhibit idle (Inhibit portal flag 8, or `gtk_application_inhibit`). While recording, also inhibit suspend (flag 4; not every desktop honours it).
- **Never stop capture** for focus, minimize, workspace or occlusion changes (§5.3). When GTK reports the surface hidden, stop presenting but keep capturing.
- **Thread priority:** the capture/audio threads ask rtkit (or the Realtime portal in Flatpak) for real-time priority. If that's refused, fall back to nice −10. Never fail because of it.

## 7. Dev loop: WSL2 + WSLg on the Windows dev PC

### 7a. What this PC has (checked 2026-10-23)

- WSL 2.5.10, kernel `6.6.87.2-microsoft-standard-WSL2`, WSLg 1.0.66. Ubuntu-24.04 is the default distro and is running, with `systemd=true`.
- The kernel ships `uvcvideo.ko`, `snd-usb-audio.ko` and `vhci-hcd.ko` as modules (`CONFIG_USB_VIDEO_CLASS=m`, `CONFIG_SND_USB_AUDIO=m`, `CONFIG_USBIP_VHCI_HCD=m`). No custom kernel is needed; old guides saying otherwise predate WSL 2.3.11. The kernel has no `UDMABUF`, no `SND_ALOOP` and no v4l2loopback.
- `/dev/dxg` is present, along with Mesa 25.2.8 with `d3d12_dri.so` (OpenGL/GLES over D3D12 on the RTX 5070 Ti) and `d3d12_drv_video.so` (VA-API over D3D12). There is no `dzn` Vulkan ICD, so Vulkan falls back to llvmpipe. Keep GTK on GL (`GSK_RENDERER=ngl`, then `gl` if ngl misrenders).
- `WAYLAND_DISPLAY=wayland-0`, `DISPLAY=:0`, and the Pulse server at `/mnt/wslg/PulseServer`.
- The user `kataii` is in groups `video`, `audio` and `plugdev`. `sudo` needs a password, so installing packages is a one-time step the user runs (or `wsl -u root`).
- Not installed yet: usbipd-win (Windows), GTK4/libadwaita dev packages, the .NET SDK, libturbojpeg, libpulse-dev, v4l-utils, mesa-utils.
- 16 cores, 30 GB RAM, 928 GB free on the distro's ext4.

### 7b. The loop

1. **Edit on Windows** (`D:\Projects\hdmi-capture`) as now.
2. **Sync to ext4 before building.** Builds on `/mnt/d` go over 9P, which is slow. `tools/linux/wsl-sync.sh` rsyncs the working tree (without `bin/`, `obj/`, `.build/` or `.git/`) to `~/src/hdmi-capture`. Task 105 measures that against building in place on `/mnt/d` with `--artifacts-path` on ext4, and keeps the faster one.
3. **Build and run from Git Bash:** `make linux-build | linux-test | linux-run LINUX_ARGS="-testPattern smpteBars"` wrap `wsl -d Ubuntu-24.04 --cd ~/src/hdmi-capture -- make …`. The same targets run natively on a real Linux box.
4. **The window shows on the Windows desktop** as an `msrdc.exe` RAIL window, so it appears in the Parsec session too.
   - Bring it to the front with the same SwitchToThisWindow snippet we use for the Windows app, matched by the msrdc owner, since both apps are titled "HDMI Captain".
   - Screenshot it with `PrintWindow` (*unconfirmed* for RAIL windows; 105 checks this) or with the app's own `-qaAction screenshot`, which saves the clean frame. `grim` doesn't work (Weston has no wlr-screencopy).
5. **Card to WSL:**
   - Once, elevated: `winget install --interactive --exact dorssel.usbipd-win` (5.3.0), then `usbipd bind --busid <id>`. Binding persists across reboots and doesn't take the card from Windows.
   - Each session, not elevated: `usbipd attach --wsl --busid <id>` (`--auto-attach` keeps re-attaching after replug). `usbipd detach` gives the card back.
   - **While attached, Windows can't use the card, so our running Windows app shows it as disconnected.** Attach only when the user agrees. Detach/attach doubles as a scripted unplug/replug test.
6. **Logs:** `~/.local/state/hdmicaptain/logs/capture.log` (`XDG_STATE_HOME`); `make linux-logs` tails it.

### 7c. Risks of the WSL loop

- **Live capture over usbip is the big unknown.** No MS2109/MS2130/MS2131 report in WSL was found. Webcam reports are mixed:
  - one works in cheese;
  - one gets only 640×480 MJPEG with corrupt regions;
  - one, on kernel 6.6.87.1 with usbipd 5.0.0, times out on `STREAMON` with `vhci_hcd: urb->status -104`.
  - MJPEG on our USB 2 cards is only ~5–15 MB/s, which gives it the best chance. YUYV at USB 3 rates (~2 Gbit/s over a Hyper-V socket) probably drops frames.
  - **108 measures this.** If it fails, WSL stays the loop for build, unit tests, test patterns and UI, and live capture moves to a real Linux install.
- **WSLg is not a real Linux desktop:**
  - Weston + RDP composites through shared memory, so there's no dmabuf/scanout and no true vsync. It runs at 60 fps by default; `WESTON_RDP_MONITOR_REFRESH_RATE` raises it.
  - The Pulse leg adds latency.
  - It's GNOME-less, so there are no portals.
  - Use it for correctness, not for latency or soak numbers.
- **GTK 4.14's `ngl` renderer** misrenders on some virtual GPUs (Launchpad 2061584, VirtualBox). 105 checks it on d3d12 and picks a `GSK_RENDERER` default for WSL.
- **HiDPI** needs `C:\ProgramData\Microsoft\WSL\.wslgconfig` `[system-distro-env]` `WESTON_RDP_HI_DPI_SCALING`, and the whole-number scales look best. The Parsec display is at 125 %.

### 7d. Real Linux for sign-off

- Final latency, the 30-min soak, Wayland/X11 window behaviour, portals, Flatpak and sleep/resume need a real install. Options:
  - Ubuntu 24.04 LTS or Fedora (GNOME, Wayland) on a spare disk or live USB on this PC;
  - or a second machine.
- The user decides which (task 124).

## 8. Sources

- usbipd-win releases, WSL support wiki: https://github.com/dorssel/usbipd-win/releases · https://github.com/dorssel/usbipd-win/wiki/WSL-support · https://learn.microsoft.com/en-us/windows/wsl/connect-usb
- WSL kernel config: https://raw.githubusercontent.com/microsoft/WSL2-Linux-Kernel/linux-msft-wsl-6.6.y/arch/x86/configs/config-wsl · https://www.phoronix.com/news/Microsoft-WSL-2.3.11
- usbip webcam reports: https://github.com/microsoft/WSL/discussions/11230 · https://github.com/InternLM/InternLM-XComposer/issues/487 · https://github.com/dorssel/usbipd-win/issues/530
- WSLg architecture and fps: https://github.com/microsoft/wslg · https://github.com/microsoft/wslg/discussions/312 · https://github.com/microsoft/wslg/issues/38
- GTK4 ngl on virtual GPUs: https://bugs.launchpad.net/ubuntu/+source/gtk4/+bug/2061584
- WSL filesystems, systemd: https://learn.microsoft.com/en-us/windows/wsl/filesystems · https://learn.microsoft.com/en-us/windows/wsl/systemd
- OBS v4l2 and PipeWire sources: https://raw.githubusercontent.com/obsproject/obs-studio/master/plugins/linux-v4l2/CMakeLists.txt · https://www.phoronix.com/news/OBS-Studio-30.1
- Camera portal: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Camera.html
- Flatpak devices: https://man7.org/linux/man-pages/man1/flatpak-build-finish.1.html · https://feaneron.com/2025/01/14/flatpak-1-16-is-out/
- Flatpak hot-plug: https://hg.libsdl.org/SDL/rev/8ef36baf9731 · https://git.jami.net/savoirfairelinux/jami-daemon/-/issues/379
- MS2109 quirk: https://lkml.iu.edu/hypermail/linux/kernel/2007.1/07921.html · https://github.molgen.mpg.de/mariux64/linux/commit/14a720dc1f5332f3bdf30a23a3bc549e81be974c · https://www.mjt.me.uk/posts/fixing-missing-macrosilicon-ms2109/
- MS2130 on Linux: https://lists.cinelerra-gg.org/pipermail/cin/2023-February/006409.html
- GTK graphics offload, memory formats: https://blog.gtk.org/2023/11/15/introducing-graphics-offload/ · https://docs.gtk.org/gdk4/enum.MemoryFormat.html
- Gir.Core: https://github.com/gircore/gir.core/releases · https://gircore.github.io/api/Gtk.GLArea.html · https://gircore.github.io/api/Gdk.DmabufTextureBuilder.html
- Avalonia 12.1: https://avaloniaui.net/blog/release-12-1
- Wayland always-on-top: https://gitlab.freedesktop.org/wayland/wayland-protocols/-/merge_requests/369
- Inhibit, GlobalShortcuts portals: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Inhibit.html · https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html
- vajpegdec: https://gstreamer.freedesktop.org/documentation/va/vajpegdec.html
- NVENC on Linux (OBS 30.2): https://gamingonlinux.com/2024/07/obs-studio-302-is-out-now-with-native-nvenc-encode-for-linux
- Flathub payments and policy: https://discourse.flathub.org/t/when-are-paid-applications-supported/7757 · https://tim.siosm.fr/blog/2025/11/24/building-better-app-store-flathub/ · https://docs.flathub.org/docs/for-app-authors/requirements
- .NET in Flatpak: https://github.com/flathub/org.freedesktop.Sdk.Extension.dotnet10 · https://docs.flatpak.org/en/latest/dotnet.html
- Snap interfaces: https://snapcraft.io/docs/interfaces
- Lemon Squeezy licences: https://docs.lemonsqueezy.com/api/license-api
