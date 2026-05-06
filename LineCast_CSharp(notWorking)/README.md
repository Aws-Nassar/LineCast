# LineCast C#

A C# / WPF port of the Python LineCast soundboard and mic-mixer application.

## Stack

| Python original         | C# replacement                        |
|-------------------------|---------------------------------------|
| PyQt5                   | WPF (.NET 8)                          |
| sounddevice             | NAudio (WasapiOut / WasapiCapture)    |
| pydub / FFmpeg          | NAudio.AudioFileReader (MP3 + WAV)    |
| pyaudiowpatch loopback  | NAudio WasapiLoopbackCapture          |
| json / pathlib          | Newtonsoft.Json / System.IO           |
| PyInstaller             | dotnet publish --self-contained       |

## Project layout

```
LineCast_CSharp/
  LineCast.csproj      .NET 8 WPF project file
  App.xaml / .cs       Application entry point
  AppConfig.cs         Config load/save (mirrors config.json)
  AudioHandler.cs      Audio engine (playback + mic passthrough)
  MainWindow.xaml      WPF UI layout
  MainWindow.xaml.cs   UI logic / event handlers
  InputDialog.cs       Simple text-input dialog
  Assets/              linecast.ico (copy from the Python assets/ folder)
  Tools/
    build_exe.ps1      dotnet publish build script
```

## Requirements

- Windows 10 or 11
- .NET 8 SDK  →  https://dotnet.microsoft.com/download
- A virtual audio cable driver (VB-CABLE recommended)
- Copy `assets/linecast.ico` from the Python project into `LineCast_CSharp/Assets/`

## Quick start

```powershell
cd LineCast_CSharp
dotnet run
```

## Build an EXE

```powershell
.\Tools\build_exe.ps1           # one-file EXE  → dist\LineCast.exe
.\Tools\build_exe.ps1 -Mode OneDir  # folder build → dist\LineCast\LineCast.exe
```

## Feature parity with the Python version

- Dark WPF UI with the same layout and colour palette
- Monitor / Mic / Device-audio / Injection device selectors
- Per-route volume sliders
- Mic passthrough (WasapiCapture → WasapiOut injection device)
- WASAPI loopback capture for system/app audio (WasapiLoopbackCapture)
- Sound library table with search, rename, change-path, and delete
- Dual-stream playback (monitor + injection simultaneously)
- Review slider with seek-while-playing
- Config persisted to `%APPDATA%\LineCast\config.json`

## Audio routing (same as Python version)

```
Your real mic  ──┐
LineCast sounds──┤──► CABLE Input ──► CABLE Output ──► meeting app mic
System audio  ──┘
```

Set the meeting app microphone to **CABLE Output (VB-Audio Virtual Cable)**.
