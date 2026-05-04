# SoundPad Clone

A Windows soundboard prototype built with Python and PyQt5. It plays MP3/WAV clips to two output routes at the same time:

- Monitor Device: your headphones or speakers
- Injection Device: a virtual audio cable input used as your microphone source in voice chat apps

## Current Status

Implemented:

- Modern PyQt5 desktop GUI
- Sound library table with search
- Add MP3/WAV files
- Monitor and injection device selectors
- Separate monitor and injection volume controls
- Dual-output playback through `sounddevice`
- Normalization through `pydub`
- Local `config.json` preference saving

Planned next:

- Global hotkeys with `pynput`
- Auto-PTT key press/release with `pyautogui`
- Sound removal/editing from the library
- Optional packaged `.exe` build

## Requirements

- Windows
- Python 3.10 or newer
- FFmpeg installed and available on `PATH` for MP3 support
- A virtual audio cable driver such as VB-CABLE for microphone injection

Python packages:

```powershell
python -m pip install PyQt5 sounddevice pydub numpy audioop-lts
```

`audioop-lts` is needed on Python 3.13+ because the old standard-library `audioop` module was removed.

## Virtual Cable Setup

1. Install a virtual audio cable such as VB-CABLE.
2. Restart Windows if the installer asks.
3. Open your voice chat app.
4. Set the app microphone/input device to the cable output device, usually named something like `CABLE Output`.
5. In this app, choose the cable input/playback endpoint as the Injection Device, usually named something like `CABLE Input`.
6. Choose your headphones as the Monitor Device.

The exact names vary by driver and Windows audio backend.

## Run The App

From the project folder:

```powershell
python main.py
```

Then:

1. Select your Monitor Device.
2. Select your Injection Device.
3. Click Add Sounds and choose MP3 or WAV files.
4. Select a sound in the table.
5. Click Play.

Double-clicking a row also plays it.

## Backend Diagnostics

List available output devices:

```powershell
python audio_handler.py
```

or:

```powershell
python audio_handler.py --list-devices
```

Test one clip without the GUI:

```powershell
python audio_handler.py --file path\to\clip.wav --monitor 3 --injection 8
```

Use the device indexes printed by the diagnostics command.

## Project Structure

```text
SoundPadCLone/
  main.py            PyQt5 GUI and app-level logic
  audio_handler.py   Dual-output playback, normalization, device listing
  config.json        Local device, volume, and sound library preferences
```

## Notes

Two separate Windows audio devices are not guaranteed to be sample-locked to each other. The app minimizes application-side delay by decoding the clip first and scheduling both streams together, but tiny drift can still happen if the devices use different hardware clocks.

If MP3 files fail to load, install FFmpeg and make sure `ffmpeg.exe` is available from PowerShell:

```powershell
ffmpeg -version
```

