# LineCast

LineCast is a Windows soundboard and mic mixer built with Python, PyQt5, and `sounddevice`. It lets you play MP3/WAV clips into meeting apps while still talking through your real microphone.

The intended routing is:

```text
Your real mic + LineCast sounds -> CABLE Input -> CABLE Output -> meeting app microphone
Meeting app audio -> your headphones/speakers
```

This works with apps that can choose a microphone device, including Discord, Zoom, Google Meet, Slack, Teams, and browser-based meeting apps.

## Features

- Modern dark PyQt5 desktop GUI
- Sound library table with search, rename, path change, and delete controls
- Add MP3/WAV files
- Review slider with time display for previewing and seeking inside clips
- Monitor, mic input, and injection device selectors
- Simplified recommended device list, with optional advanced devices
- Separate monitor, mic, video/system, and sound injection volume controls
- Mic passthrough/mixing into a virtual cable
- Optional WASAPI loopback device/app audio input for browser, YouTube, VLC, or local media audio
- Soundboard playback through `sounddevice`
- Clip normalization through `pydub`
- Per-user preference saving under Windows AppData
- Custom app icon
- Repeatable one-file `.exe` build script

## Requirements

- Windows
- Python 3.10 or newer
- FFmpeg installed and available on `PATH` for MP3 support
- A virtual audio cable driver, such as VB-CABLE

Install Python packages:

```powershell
python -m pip install PyQt5 sounddevice pydub numpy audioop-lts pyinstaller PyAudioWPatch
```

`audioop-lts` is needed on Python 3.13+ because Python removed the old standard-library `audioop` module.

## Quick Start

Run from the project folder:

```powershell
python main.py
```

Then choose:

```text
Monitor Device = your headphones/speakers
Mic Input = your real microphone
Injection Device = CABLE Input (VB-Audio Virtual Cable)
Mix mic into virtual input = ON
Mix device/app audio into virtual input = OFF until you need it
```

In Discord, Zoom, Meet, Slack, Teams, or another meeting app:

```text
Microphone/Input = CABLE Output (VB-Audio Virtual Cable)
Speaker/Output = your headphones/speakers
```

Keep LineCast open while using the meeting app. LineCast is acting as the mixer that forwards your real mic plus soundboard clips into the virtual microphone.

LineCast remembers your selected devices, volumes, and sound list between runs.

## Device Setup

LineCast shows a simplified recommended device list by default. Turn on `Show advanced audio devices` only if you need to see every Windows/PortAudio backend entry.

For normal use, prefer `WASAPI` devices when available.

Common device meanings:

```text
CABLE Input = where LineCast sends audio
CABLE Output = what meeting apps use as the microphone
Mic Input = your real microphone, never CABLE Output
Monitor Device = where you personally hear the soundboard
```

Avoid these selections:

```text
LineCast Mic Input = CABLE Output
LineCast Mic Input = Stereo Mix
LineCast Injection Device = CABLE In 16ch
Meeting app Speaker/Output = CABLE Input or CABLE Output
```

The duplicate-looking advanced device names come from Windows exposing the same hardware through different audio APIs:

```text
MME = old Windows audio
DirectSound = older game/media audio
WASAPI = modern Windows audio, usually best
WDM-KS = low-level driver access, can be unstable or confusing
```

## Using Sounds

1. Click `Add Sounds`.
2. Choose one or more `.mp3` or `.wav` files.
3. Select a sound in the table.
4. Use the `Review` slider to preview position and seek while a clip is playing.
5. Click `Play`.

Double-clicking a row also plays it. Use `Rename`, `Change Path`, and `Delete` to manage the selected sound. `Delete` removes it from the LineCast library only; it does not delete the audio file from disk.

Volume controls:

```text
Monitor Volume = how loud the clip is for you
Mic Volume = how loud your voice is in the virtual microphone
Device/App Volume = how loud captured browser/video audio is for meeting apps
Sound Injection Volume = how loud the clip is for meeting apps
```

## Browser, YouTube, And Local Video Audio

LineCast can mix audio playing on a Windows output device into the same virtual microphone. This uses WASAPI loopback through `PyAudioWPatch`, not `Stereo Mix`. It is useful when you need meeting participants to hear a YouTube video, browser tab, VLC video, or local media that you cannot download.

Recommended safe setup:

```text
LineCast Mic Input = your real microphone
LineCast Device/App Audio Source = Speakers (Realtek(R) Audio) -> device/app audio
LineCast Injection Device = CABLE Input
Meeting app Microphone/Input = CABLE Output
Meeting app Speaker/Output = headphones or another device you are NOT capturing
```

Turn on:

```text
Mix device/app audio into virtual input = ON
```

If you want YouTube/browser audio captured, make that app play through the same output device selected as `Device/App Audio Source`. For example, if LineCast captures `Speakers (Realtek(R) Audio)`, set the browser output to `Speakers (Realtek(R) Audio)` in Windows volume mixer.

Avoid capturing the meeting app's own speaker output. If Discord, Zoom, Meet, Slack, or Teams plays through the same device you are capturing, its sounds can loop back into the meeting microphone.

## Build The EXE

Generate the icon assets if needed:

```powershell
python tools\generate_icon.py
```

Build the runnable one-file app:

```powershell
.\tools\build_exe.ps1
```

The executable is created at:

```text
dist\LineCast.exe
```

The one-file build is easiest to share, but it starts slower because PyInstaller unpacks the app to a temporary folder every time it launches. For faster startup, build the folder version:

```powershell
.\tools\build_exe.ps1 -Mode OneDir
```

That creates:

```text
dist\LineCast\LineCast.exe
```

Ship the whole `dist\LineCast` folder if you use this mode.

FFmpeg still needs to be installed on the target PC for MP3 support.

The `.exe` stores user settings here:

```text
%APPDATA%\LineCast\config.json
```

That file is created automatically on each PC. Do not ship your personal `config.json` with the app.

## Backend Diagnostics

List available input and output devices:

```powershell
python audio_handler.py
```

or:

```powershell
python audio_handler.py --list-devices
```

Test one clip without the GUI:

```powershell
python audio_handler.py --file path\to\clip.wav --monitor 19 --injection 14
```

Use the device indexes printed by the diagnostics command. The numbers above are only an example.

## Troubleshooting

If the meeting app detects no mic input:

1. Make sure LineCast is open.
2. Make sure `Mix mic into virtual input` is ON.
3. Make sure LineCast `Mic Input` is your real microphone.
4. Make sure LineCast `Injection Device` is `CABLE Input`.
5. Make sure the meeting app microphone is `CABLE Output`.
6. Toggle `Mix mic into virtual input` off and on once.

If the meeting app does not hear browser/video audio:

1. Make sure `Mix device/app audio into virtual input` is ON.
2. Select `Stereo Mix` or another real capture source as `Device/App Audio Source`.
3. Make sure the browser or video player is outputting to the device that source captures.
4. Try playing audio before starting the meeting-app mic test.

If you hear a join/leave sound or meeting audio looping:

1. In the meeting app, set speaker/output directly to your headphones.
2. Do not use `Windows Default` while testing.
3. Do not set the meeting app speaker/output to any cable device.
4. Do not set LineCast Mic Input to `CABLE Output` or `Stereo Mix`.
5. If video/system audio capture is enabled, make sure it is not capturing the meeting app speaker/output.

If MP3 files fail to load, install FFmpeg and confirm it is on `PATH`:

```powershell
ffmpeg -version
```

## Project Structure

```text
LineCast/
  main.py              PyQt5 GUI and app-level logic
  audio_handler.py     Mic mixing, playback, normalization, device listing
  assets/              App icon assets
  tools/
    build_exe.ps1      PyInstaller build script
    generate_icon.py   Rebuilds app icon assets
  config.json          Optional legacy local config; new saves use AppData
```

## Planned Next

- Global hotkeys with `pynput`
- Auto-PTT key press/release with `pyautogui`
- Sound removal/editing from the library
