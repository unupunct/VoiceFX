# VoiceFX Studio

Real-time microphone voice effects for Windows 10/11 (64-bit): six one-click presets in a small, floating, resizable window, with the processed voice delivered to other apps (OBS, Discord, Audacity, browsers…) as a **virtual microphone**. Everything runs locally – no cloud, no accounts, no internet needed.

## Install

Run `VoiceFXStudio-Setup-1.0.0.exe` (per-user install, no admin needed, self-contained – no .NET install required).

### Required once: a virtual audio cable (default: VB-CABLE)
Windows cannot create a microphone device from a normal app, so VoiceFX feeds an established virtual cable driver. It never installs drivers for you.

1. Download **VB-CABLE** (free): <https://vb-audio.com/Cable/>, unzip, run `VBCABLE_Setup_x64.exe` **as administrator**, reboot.
2. Start VoiceFX. Output defaults to **CABLE Input** automatically (you can pick any other output device in the Output list). The banner turns green: *"Virtual mic ready"*.
3. In your app choose **CABLE Output (VB-Audio Virtual Cable)** as the microphone:

| App | Where |
|---|---|
| OBS Studio | Settings ▸ Audio ▸ Mic/Auxiliary Audio = *CABLE Output*; or add an *Audio Input Capture* source with that device |
| Discord | User Settings ▸ Voice & Video ▸ Input Device = *CABLE Output* (turn off Automatic Input Sensitivity if gating oddly) |
| Audacity | Device toolbar ▸ Recording device = *CABLE Output* (Windows WASAPI host) |
| Browsers / web recorders | Allow mic access, then pick *CABLE Output* in the site's mic menu (or Windows Settings ▸ System ▸ Sound ▸ Input) |
| Teams / Zoom / Meet | Audio settings ▸ Microphone = *CABLE Output* |

If the cable is missing, VoiceFX shows a first-run prompt (can be dismissed permanently) and a banner with setup steps.

## Using it
* **Six presets** (2×3 grid): Natural, Deep Radio, Bright, Telephone, Robot, Alien. Switching is instant (30 ms cross-fade, no engine restart).
* **Intensity** = how strongly the effect is applied (pitch depth / EQ amounts / dry-wet depending on preset).
* **Mute**, **Bypass** (original mic through the limiter), **IN/OUT meters**, **CLIP** lamp, 📌 always-on-top, ▭ compact mode, ⚙ settings.
* Settings panel: input/master gain, noise gate, basic noise suppression, buffer size, per-preset parameters (saved), monitoring, tray/startup options, *Reset to defaults*.
* The window never takes keyboard focus when you click it (`WS_EX_NOACTIVATE`), so your game/stream/editor keeps focus.
* **Monitoring** ("hear my processed voice") is **off by default** and refuses virtual devices; use headphones.
* Tray icon: show, presets, mute, bypass, exit. Closing can hide to the tray (option). Launch-at-startup is optional.

### Preset DSP (all genuine real-time processing, nothing pre-recorded)
| Preset | Chain |
|---|---|
| Natural | 4th-order high-pass, light expander, gentle compression |
| Deep Radio | Time-domain pitch shifter (dual-tap crossfade, Hermite interpolation; speed preserved) –3.5 st, low-shelf + low-mid warmth, broadcast compression |
| Bright | HP + body cut, 3 kHz presence, 8 kHz air shelf, split-band de-esser, compression |
| Telephone | 4th-order band-pass, mid peak, tanh saturation, optional filtered hiss (switchable) |
| Robot | Ring modulator (default 70 Hz), metallic comb, distortion |
| Alien | Pitch-up (+6 st) with LFO warble, formant resonances, 1.1 kHz ring shimmer, soft distortion |

Final stage for every preset: master gain → 1.5 ms look-ahead limiter (–1 dBFS) → hard clamp. Parameter changes are smoothed.

## Architecture
```
Mic ─WASAPI shared (event)→ AudioCaptureEngine → AudioProcessingEngine ─→ VirtualMicrophoneRouter ─WASAPI→ CABLE Input
                              (mono float)        gain·gate·preset·limiter     lock-free ring + drift-compensating resampler   (+ optional monitor)
```
`src/VoiceFX.Core` (audio engine, DSP, services – no UI dependency): `AudioDeviceManager`, `AudioCaptureEngine`, `AudioProcessingEngine`, `VoicePresetManager`, `VirtualMicrophoneRouter`, `AudioMeterService`, `SettingsService`, `DiagnosticsService`, `AudioPipeline` (supervisor with auto-recovery). `src/VoiceFX.App` (WPF, DI via Microsoft.Extensions.DependencyInjection): `MainWindow`, `TrayIconService`, `StartupService`. The audio callback is allocation-free and lock-free; the capture thread gets MMCSS "Pro Audio" priority.

Device unplug / stream failure → engine goes to *Reconnecting* and retries every 2 s (and immediately on device-change notifications). Logs: `%APPDATA%\VoiceFX\logs\voicefx.log` (never contains audio). Settings: `%APPDATA%\VoiceFX\settings.json`.

## Build from source
Requirements: Windows 10/11 x64, .NET 8 SDK, (installer) Inno Setup 6.
```
dotnet build VoiceFX.sln -c Release
dotnet run --project tests/VoiceFX.SelfTest -c Release          # offline DSP tests
dotnet run --project tests/VoiceFX.SelfTest -c Release -- --live "" 6   # live mic smoke test (no output)
powershell installer\build.ps1                                   # publishes + builds dist\VoiceFXStudio-Setup-1.0.0.exe
```
Dependencies: NAudio.Wasapi 2.2.1 (MIT), Microsoft.Extensions.DependencyInjection 8.0.1.

## Latency
Estimate shown live in Settings = capture period (10 ms) + output ring fill + output buffer + effect latency. Measured on the development machine (USB mic/USB output, 20 ms buffer): ≈ 21 ms to the processing output, ≈ 45–65 ms through a USB speaker endpoint; pitch presets add ≈ 15–18 ms. WASAPI shared mode has a ~10 ms floor per stage; lower the Buffer setting for less delay if your device has no underruns. A hardware loopback measurement through VB-CABLE has **not** been done yet.

## Troubleshooting
* *"No virtual microphone found"* – install VB-CABLE and reboot; click *Restart audio engine*.
* *Other apps hear nothing* – they must use **CABLE Output** as mic; Windows privacy settings must allow desktop apps mic access.
* *Crackling / underruns counter rising* – increase Buffer (30–40 ms), close CPU-heavy apps.
* *Echo / feedback* – turn monitoring off or use headphones; never set Monitor to the cable.
* *Mic busy* – VoiceFX uses shared mode, so other apps can share the mic; exclusive-mode apps may block it (status shows *Reconnecting…*).
* Logs: Settings ▸ *Open log folder*.

## Status
Built and tested: DSP (distinctness, pitch accuracy, glitch-free switching, limiter, mute/bypass, parameter extremes), live capture and render on real devices, UI, settings. **Not yet verified:** end-to-end capture in OBS through VB-CABLE (driver not installed on the dev machine), extended multi-hour stability, Windows 10.
