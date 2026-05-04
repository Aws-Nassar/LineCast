"""
Audio playback and routing for the SoundPad clone.

This module owns the low-level audio path:
- Decode MP3/WAV files with pydub.
- Normalize loudness before playback.
- Play clips to your monitor/headphones.
- Mix your real microphone and clips into an injection/virtual cable output.

Install notes:
    pip install sounddevice pydub numpy

MP3 support in pydub requires FFmpeg to be installed and available on PATH.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Optional

import numpy as np
import sounddevice as sd

try:
    import pyaudiowpatch as pyaudio
except ModuleNotFoundError:
    pyaudio = None

try:
    from pydub import AudioSegment
    import pydub.utils as pydub_utils
except ModuleNotFoundError as exc:
    if exc.name in {"audioop", "pyaudioop"}:
        raise RuntimeError(
            "pydub needs the audioop compatibility package on Python 3.13+. "
            "Install it with: python -m pip install audioop-lts"
        ) from exc
    raise


PlaybackCallback = Callable[[str, Optional[BaseException]], None]
UNSET = object()


def _hide_ffmpeg_windows() -> None:
    if sys.platform != "win32":
        return

    original_popen = subprocess.Popen

    def hidden_popen(*args: object, **kwargs: object) -> subprocess.Popen:
        startupinfo = kwargs.get("startupinfo")
        if startupinfo is None:
            startupinfo = subprocess.STARTUPINFO()
            kwargs["startupinfo"] = startupinfo

        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startupinfo.wShowWindow = subprocess.SW_HIDE
        kwargs["creationflags"] = (
            int(kwargs.get("creationflags", 0)) | subprocess.CREATE_NO_WINDOW
        )
        return original_popen(*args, **kwargs)

    subprocess.Popen = hidden_popen
    pydub_utils.Popen = hidden_popen


_hide_ffmpeg_windows()


@dataclass(frozen=True)
class AudioDevice:
    """A simplified view of a sounddevice audio device."""

    index: int | str
    name: str
    hostapi: str
    max_input_channels: int
    max_output_channels: int
    default_samplerate: float


@dataclass(frozen=True)
class PlaybackHandle:
    """Returned by play_file so callers can inspect or stop playback."""

    playback_id: int
    file_path: Path


class AudioHandler:
    """
    Plays normalized audio to a monitor device and an injection device.

    The two devices are opened as separate PortAudio output streams. True
    sample-locked sync is only possible when both routes share the same audio
    clock/device, but this implementation avoids application-level lag by:
    - decoding and normalizing before streams are opened,
    - starting both streams before audible playback begins,
    - using lightweight callbacks that only copy from prebuilt numpy arrays.
    """

    def __init__(
        self,
        monitor_device: Optional[int | str] = None,
        injection_device: Optional[int | str] = None,
        mic_device: Optional[int | str] = None,
        external_audio_device: Optional[int | str] = None,
        *,
        sample_rate: int = 48_000,
        channels: int = 2,
        blocksize: int = 1024,
        latency: str | float = "low",
        target_dbfs: float = -18.0,
        start_delay_seconds: float = 0.08,
    ) -> None:
        self.monitor_device = monitor_device
        self.injection_device = injection_device
        self.mic_device = mic_device
        self.external_audio_device = external_audio_device
        self.sample_rate = sample_rate
        self.channels = channels
        self.blocksize = blocksize
        self.latency = latency
        self.target_dbfs = target_dbfs
        self.start_delay_seconds = start_delay_seconds

        self.monitor_volume = 1.0
        self.injection_volume = 1.0
        self.mic_volume = 1.0
        self.external_audio_volume = 1.0
        self.mic_passthrough_enabled = False
        self.external_audio_enabled = False

        self._lock = threading.RLock()
        self._playback_id = 0
        self._active: Optional[_DualPlayback | _MixedPlayback] = None
        self._mic_mixer: Optional[_MicInjectionMixer] = None

    @staticmethod
    def list_output_devices() -> list[AudioDevice]:
        """Return output-capable audio devices for populating the UI."""

        hostapis = sd.query_hostapis()
        devices: list[AudioDevice] = []

        for index, device in enumerate(sd.query_devices()):
            output_channels = int(device.get("max_output_channels", 0))
            if output_channels <= 0:
                continue

            hostapi_index = int(device["hostapi"])
            devices.append(
                AudioDevice(
                    index=index,
                    name=str(device["name"]),
                    hostapi=str(hostapis[hostapi_index]["name"]),
                    max_input_channels=int(device.get("max_input_channels", 0)),
                    max_output_channels=output_channels,
                    default_samplerate=float(device["default_samplerate"]),
                )
            )

        return devices

    @staticmethod
    def list_input_devices() -> list[AudioDevice]:
        """Return input-capable audio devices for microphone selection."""

        hostapis = sd.query_hostapis()
        devices: list[AudioDevice] = []

        for index, device in enumerate(sd.query_devices()):
            input_channels = int(device.get("max_input_channels", 0))
            if input_channels <= 0:
                continue

            hostapi_index = int(device["hostapi"])
            devices.append(
                AudioDevice(
                    index=index,
                    name=str(device["name"]),
                    hostapi=str(hostapis[hostapi_index]["name"]),
                    max_input_channels=input_channels,
                    max_output_channels=int(device.get("max_output_channels", 0)),
                    default_samplerate=float(device["default_samplerate"]),
                )
            )

        return devices

    @staticmethod
    def list_loopback_devices() -> list[AudioDevice]:
        """Return WASAPI loopback capture devices for system/app audio."""

        if pyaudio is None:
            return []

        audio = pyaudio.PyAudio()
        try:
            devices: list[AudioDevice] = []
            for device in audio.get_loopback_device_info_generator():
                name = str(device.get("name", ""))
                if "vb-audio" in name.lower() or "cable" in name.lower():
                    continue

                devices.append(
                    AudioDevice(
                        index=f"loopback:{int(device['index'])}",
                        name=name,
                        hostapi="Windows WASAPI Loopback",
                        max_input_channels=int(device.get("maxInputChannels", 2)),
                        max_output_channels=0,
                        default_samplerate=float(device.get("defaultSampleRate", 48_000.0)),
                    )
                )

            return devices
        finally:
            audio.terminate()


    @staticmethod
    def test_input_device_open(
        device: int | str,
        *,
        sample_rate: int = 48_000,
        channels: int = 2,
        blocksize: int = 1024,
        latency: str | float = "high",
    ) -> None:
        """Open and close an input stream to confirm it is usable."""

        if _is_loopback_device(device):
            if pyaudio is None:
                raise RuntimeError("PyAudioWPatch is required for system/app audio capture")

            audio = pyaudio.PyAudio()
            stream = None

            def loopback_callback(
                in_data: bytes,
                frame_count: int,
                time_info: object,
                status: int,
            ) -> tuple[None, int]:
                del in_data, frame_count, time_info, status
                return (None, pyaudio.paContinue)

            try:
                stream = audio.open(
                    format=pyaudio.paFloat32,
                    channels=channels,
                    rate=sample_rate,
                    input=True,
                    input_device_index=_loopback_device_index(device),
                    frames_per_buffer=blocksize,
                    stream_callback=loopback_callback,
                )
                stream.start_stream()
                time.sleep(0.03)
            finally:
                if stream:
                    try:
                        stream.stop_stream()
                    except Exception:
                        pass
                    try:
                        stream.close()
                    except Exception:
                        pass
                audio.terminate()
            return

        def callback(
            indata: np.ndarray,
            frames: int,
            time_info: object,
            status: sd.CallbackFlags,
        ) -> None:
            del indata, frames, time_info, status

        stream: Optional[sd.InputStream] = None
        try:
            stream = sd.InputStream(
                samplerate=sample_rate,
                blocksize=blocksize,
                device=device,
                channels=channels,
                dtype="float32",
                latency=latency,
                callback=callback,
            )
            stream.start()
            time.sleep(0.03)
        finally:
            if stream:
                try:
                    stream.abort()
                except Exception:
                    pass
                try:
                    stream.close()
                except Exception:
                    pass

    def set_devices(
        self,
        *,
        monitor_device: object = UNSET,
        injection_device: object = UNSET,
        mic_device: object = UNSET,
        external_audio_device: object = UNSET,
    ) -> None:
        """Update the selected audio devices."""

        with self._lock:
            if monitor_device is not UNSET:
                self.monitor_device = monitor_device
            if injection_device is not UNSET:
                self.injection_device = injection_device
            if mic_device is not UNSET:
                self.mic_device = mic_device
            if external_audio_device is not UNSET:
                self.external_audio_device = external_audio_device

    def set_volumes(
        self,
        *,
        monitor: Optional[float] = None,
        injection: Optional[float] = None,
        mic: Optional[float] = None,
        external_audio: Optional[float] = None,
    ) -> None:
        """Set per-route volumes. Values are clamped to 0.0 through 1.0."""

        with self._lock:
            if monitor is not None:
                self.monitor_volume = self._clamp_volume(monitor)
                if self._active:
                    self._active.monitor_volume = self.monitor_volume

            if injection is not None:
                self.injection_volume = self._clamp_volume(injection)
                if self._active:
                    self._active.injection_volume = self.injection_volume
                if self._mic_mixer:
                    self._mic_mixer.sound_volume = self.injection_volume

            if mic is not None:
                self.mic_volume = self._clamp_volume(mic)
                if self._mic_mixer:
                    self._mic_mixer.mic_volume = self.mic_volume

            if external_audio is not None:
                self.external_audio_volume = self._clamp_volume(external_audio)
                if self._mic_mixer:
                    self._mic_mixer.external_audio_volume = self.external_audio_volume

    def set_mic_passthrough_enabled(self, enabled: bool) -> None:
        """Enable or disable continuous mic mixing into the injection device."""

        with self._lock:
            self.mic_passthrough_enabled = enabled

        if not enabled and not self.external_audio_enabled:
            self.stop_mic_passthrough()

    def set_external_audio_enabled(self, enabled: bool) -> None:
        """Enable or disable the optional external/video audio input."""

        restart = False
        with self._lock:
            if self.external_audio_enabled != enabled:
                restart = self._mic_mixer is not None
            self.external_audio_enabled = enabled

        if restart:
            self.stop_mic_passthrough()
            self.start_mic_passthrough()

    def start_mic_passthrough(self) -> None:
        """Start routing microphone input into the selected injection output."""

        with self._lock:
            route_mic = self.mic_passthrough_enabled
            route_external = self.external_audio_enabled
            if not route_mic and not route_external:
                return
            if route_mic and self.mic_device is None:
                raise ValueError("mic_device has not been selected")
            if route_external and self.external_audio_device is None:
                raise ValueError("external_audio_device has not been selected")
            if self.injection_device is None:
                raise ValueError("injection_device has not been selected")

            existing = self._mic_mixer
            if (
                existing
                and existing.is_running
                and existing.mic_device == self.mic_device
                and existing.injection_device == self.injection_device
                and existing.external_audio_device == self.external_audio_device
                and existing.mic_enabled == route_mic
                and existing.external_audio_enabled == route_external
            ):
                existing.mic_volume = self.mic_volume if route_mic else 0.0
                existing.sound_volume = self.injection_volume
                existing.external_audio_volume = self.external_audio_volume
                return

            self._mic_mixer = None

        if existing:
            existing.stop()

        mixer = _MicInjectionMixer(
            mic_device=self.mic_device if route_mic else None,
            injection_device=self.injection_device,
            external_audio_device=self.external_audio_device if route_external else None,
            mic_enabled=route_mic,
            sample_rate=self.sample_rate,
            channels=self.channels,
            blocksize=self.blocksize,
            latency=self.latency,
            mic_volume=self.mic_volume if route_mic else 0.0,
            external_audio_volume=self.external_audio_volume,
            external_audio_enabled=route_external,
            sound_volume=self.injection_volume,
        )
        mixer.start()

        with self._lock:
            self._mic_mixer = mixer

    def stop_mic_passthrough(self) -> None:
        """Stop the continuous microphone passthrough, if it is running."""

        with self._lock:
            mixer = self._mic_mixer
            self._mic_mixer = None

        if mixer:
            mixer.stop()

    @property
    def is_mic_passthrough_running(self) -> bool:
        with self._lock:
            return bool(self._mic_mixer and self._mic_mixer.is_running)

    def play_file(
        self,
        file_path: str | Path,
        *,
        monitor_volume: Optional[float] = None,
        injection_volume: Optional[float] = None,
        start_seconds: float = 0.0,
        on_finished: Optional[PlaybackCallback] = None,
        stop_current: bool = True,
    ) -> PlaybackHandle:
        """
        Decode, normalize, and play one audio file to both configured devices.

        Args:
            file_path: MP3/WAV/other FFmpeg-supported file.
            monitor_volume: Optional per-play override for headphones.
            injection_volume: Optional per-play override for virtual cable.
            start_seconds: Optional seek point inside the file.
            on_finished: Called from a background thread with
                (status, error). status is "finished", "stopped", or "error".
            stop_current: Stop any previous playback before starting this one.
        """

        path = Path(file_path).expanduser().resolve()
        if not path.exists():
            raise FileNotFoundError(path)

        if self.monitor_device is None:
            raise ValueError("monitor_device has not been selected")
        if self.injection_device is None:
            raise ValueError("injection_device has not been selected")
        if self.mic_passthrough_enabled or self.external_audio_enabled:
            self.start_mic_passthrough()

        audio = self._load_normalized_audio(path)
        if start_seconds > 0 and len(audio) > 0:
            start_frame = int(float(start_seconds) * self.sample_rate)
            start_frame = max(0, min(start_frame, len(audio) - 1))
            audio = np.ascontiguousarray(audio[start_frame:], dtype=np.float32)

        with self._lock:
            if stop_current:
                self.stop()
            elif self._active and self._active.is_running:
                raise RuntimeError("Audio is already playing")

            self._playback_id += 1
            playback_id = self._playback_id
            mixer = (
                self._mic_mixer
                if (self.mic_passthrough_enabled or self.external_audio_enabled)
                and self._mic_mixer
                else None
            )

            if mixer and mixer.is_running:
                playback = _MixedPlayback(
                    playback_id=playback_id,
                    samples=audio,
                    sample_rate=self.sample_rate,
                    channels=self.channels,
                    monitor_device=self.monitor_device,
                    mixer=mixer,
                    monitor_volume=(
                        self.monitor_volume
                        if monitor_volume is None
                        else self._clamp_volume(monitor_volume)
                    ),
                    injection_volume=(
                        self.injection_volume
                        if injection_volume is None
                        else self._clamp_volume(injection_volume)
                    ),
                    blocksize=self.blocksize,
                    latency=self.latency,
                    start_delay_seconds=self.start_delay_seconds,
                    on_finished=self._wrap_finished_callback(playback_id, on_finished),
                )
            else:
                playback = _DualPlayback(
                    playback_id=playback_id,
                    samples=audio,
                    sample_rate=self.sample_rate,
                    channels=self.channels,
                    monitor_device=self.monitor_device,
                    injection_device=self.injection_device,
                    monitor_volume=(
                        self.monitor_volume
                        if monitor_volume is None
                        else self._clamp_volume(monitor_volume)
                    ),
                    injection_volume=(
                        self.injection_volume
                        if injection_volume is None
                        else self._clamp_volume(injection_volume)
                    ),
                    blocksize=self.blocksize,
                    latency=self.latency,
                    start_delay_seconds=self.start_delay_seconds,
                    on_finished=self._wrap_finished_callback(playback_id, on_finished),
                )

            self._active = playback
            playback.start()

        return PlaybackHandle(playback_id=playback_id, file_path=path)

    def stop(self) -> None:
        """Stop the current playback, if any."""

        with self._lock:
            active = self._active
            self._active = None

        if active:
            active.stop()

    @property
    def is_playing(self) -> bool:
        with self._lock:
            return bool(self._active and self._active.is_running)

    def _load_normalized_audio(self, file_path: Path) -> np.ndarray:
        """
        Decode audio into float32 samples shaped as (frames, channels).

        pydub's normalize() is peak-based, which can still leave perceived
        loudness uneven. For soundboard clips, RMS targeting is usually more
        useful, so this adjusts each clip toward target_dbfs and leaves a
        little headroom to avoid clipping.
        """

        segment = AudioSegment.from_file(file_path)
        segment = segment.set_frame_rate(self.sample_rate).set_channels(self.channels)
        segment = segment.set_sample_width(2)

        if segment.rms > 0 and segment.dBFS != float("-inf"):
            gain = self.target_dbfs - segment.dBFS
            segment = segment.apply_gain(gain)

        samples = np.array(segment.get_array_of_samples())
        samples = samples.reshape((-1, self.channels)).astype(np.float32)
        samples /= float(1 << 15)

        peak = float(np.max(np.abs(samples))) if samples.size else 0.0
        if peak > 1.0:
            samples /= peak

        return np.ascontiguousarray(samples, dtype=np.float32)

    @staticmethod
    def get_file_duration_seconds(file_path: str | Path) -> float:
        """Return an audio file duration without starting playback."""

        segment = AudioSegment.from_file(Path(file_path).expanduser().resolve())
        return len(segment) / 1000.0

    def _wrap_finished_callback(
        self,
        playback_id: int,
        callback: Optional[PlaybackCallback],
    ) -> PlaybackCallback:
        def finished(status: str, error: Optional[BaseException]) -> None:
            with self._lock:
                if self._active and self._active.playback_id == playback_id:
                    self._active = None

            if callback:
                callback(status, error)

        return finished

    @staticmethod
    def _clamp_volume(value: float) -> float:
        return max(0.0, min(1.0, float(value)))


class _MixedPlayback:
    """One soundboard clip monitored locally and injected through a mic mixer."""

    def __init__(
        self,
        *,
        playback_id: int,
        samples: np.ndarray,
        sample_rate: int,
        channels: int,
        monitor_device: int | str,
        mixer: "_MicInjectionMixer",
        monitor_volume: float,
        injection_volume: float,
        blocksize: int,
        latency: str | float,
        start_delay_seconds: float,
        on_finished: PlaybackCallback,
    ) -> None:
        self.playback_id = playback_id
        self.samples = samples
        self.mixer = mixer
        self.injection_volume = injection_volume
        self.on_finished = on_finished

        self._finished_event = threading.Event()
        self._notify_lock = threading.Lock()
        self._notified = False
        self._monitor = _SinglePlayback(
            playback_id=playback_id,
            samples=samples,
            sample_rate=sample_rate,
            channels=channels,
            output_device=monitor_device,
            volume=monitor_volume,
            blocksize=blocksize,
            latency=latency,
            start_delay_seconds=start_delay_seconds,
            on_finished=self._monitor_finished,
        )

    @property
    def monitor_volume(self) -> float:
        return self._monitor.volume

    @monitor_volume.setter
    def monitor_volume(self, value: float) -> None:
        self._monitor.volume = value

    @property
    def is_running(self) -> bool:
        return not self._finished_event.is_set()

    def start(self) -> None:
        self.mixer.play_sound(
            self.playback_id,
            self.samples,
            volume=self.injection_volume,
        )
        try:
            self._monitor.start()
        except BaseException as exc:
            self.mixer.stop_sound(self.playback_id)
            self._finished_event.set()
            self._notify_finished("error", exc)
            raise

    def stop(self, *, report: bool = True) -> None:
        self.mixer.stop_sound(self.playback_id)
        self._monitor.stop(report=False)
        self._finished_event.set()
        if report:
            self._notify_finished("stopped", None)

    def _monitor_finished(
        self,
        status: str,
        error: Optional[BaseException],
    ) -> None:
        if status != "finished" or error:
            self.mixer.stop_sound(self.playback_id)

        self._finished_event.set()
        self._notify_finished(status, error)

    def _notify_finished(
        self,
        status: str,
        error: Optional[BaseException],
    ) -> None:
        with self._notify_lock:
            if self._notified:
                return
            self._notified = True

        try:
            self.on_finished(status, error)
        except Exception:
            pass


class _SinglePlayback:
    """Internal one-shot player for one output device."""

    def __init__(
        self,
        *,
        playback_id: int,
        samples: np.ndarray,
        sample_rate: int,
        channels: int,
        output_device: int | str,
        volume: float,
        blocksize: int,
        latency: str | float,
        start_delay_seconds: float,
        on_finished: PlaybackCallback,
    ) -> None:
        self.playback_id = playback_id
        self.samples = samples
        self.sample_rate = sample_rate
        self.channels = channels
        self.output_device = output_device
        self.volume = volume
        self.blocksize = blocksize
        self.latency = latency
        self.start_delay_seconds = start_delay_seconds
        self.on_finished = on_finished

        self._stop_event = threading.Event()
        self._finished_event = threading.Event()
        self._status_lock = threading.Lock()
        self._notify_lock = threading.Lock()
        self._error: Optional[BaseException] = None
        self._notified = False
        self._stream: Optional[sd.OutputStream] = None
        self._started_at = 0.0
        self._watcher: Optional[threading.Thread] = None

    @property
    def is_running(self) -> bool:
        return not self._finished_event.is_set()

    def start(self) -> None:
        self._started_at = time.perf_counter() + self.start_delay_seconds

        try:
            self._stream = sd.OutputStream(
                samplerate=self.sample_rate,
                blocksize=self.blocksize,
                device=self.output_device,
                channels=self.channels,
                dtype="float32",
                latency=self.latency,
                callback=self._make_callback(),
                finished_callback=self._stream_finished,
            )
            self._stream.start()
        except BaseException as exc:
            self._record_error(exc)
            self.stop(report=False)
            self._notify_finished("error", exc)
            raise

        self._watcher = threading.Thread(
            target=self._watch_until_done,
            name=f"SinglePlayback-{self.playback_id}",
            daemon=True,
        )
        self._watcher.start()

    def stop(self, *, report: bool = True) -> None:
        self._stop_event.set()

        if self._stream:
            try:
                self._stream.abort()
            except Exception:
                pass
            try:
                self._stream.close()
            except Exception:
                pass

        self._finished_event.set()
        if report:
            self._notify_finished("stopped", None)

    def _make_callback(
        self,
    ) -> Callable[[np.ndarray, int, object, sd.CallbackFlags], None]:
        state = {
            "target_dac_time": None,
            "fallback_started": False,
            "fallback_cursor": 0,
        }

        def callback(
            outdata: np.ndarray,
            frames: int,
            time_info: object,
            status: sd.CallbackFlags,
        ) -> None:
            if status:
                self._record_error(RuntimeError(str(status)))

            if self._stop_event.is_set():
                outdata.fill(0)
                raise sd.CallbackAbort

            output_dac_time = getattr(time_info, "outputBufferDacTime", None)
            if output_dac_time is None:
                self._fill_from_fallback_clock(outdata, frames, state)
                return

            if state["target_dac_time"] is None:
                delay = max(0.0, self._started_at - time.perf_counter())
                state["target_dac_time"] = float(output_dac_time) + delay

            target_dac_time = float(state["target_dac_time"])
            start_frame = int((float(output_dac_time) - target_dac_time) * self.sample_rate)

            if start_frame + frames <= 0:
                outdata.fill(0)
                return

            output_offset = max(0, -start_frame)
            sample_start = max(0, start_frame)

            self._fill_output(outdata, output_offset, sample_start, frames)

            if sample_start + frames - output_offset >= len(self.samples):
                raise sd.CallbackStop

        return callback

    def _fill_from_fallback_clock(
        self,
        outdata: np.ndarray,
        frames: int,
        state: dict[str, object],
    ) -> None:
        if not state["fallback_started"]:
            if time.perf_counter() < self._started_at:
                outdata.fill(0)
                return

            state["fallback_started"] = True

        sample_start = int(state["fallback_cursor"])
        state["fallback_cursor"] = sample_start + frames
        self._fill_output(outdata, 0, sample_start, frames)

        if sample_start + frames >= len(self.samples):
            raise sd.CallbackStop

    def _fill_output(
        self,
        outdata: np.ndarray,
        output_offset: int,
        sample_start: int,
        frames: int,
    ) -> None:
        outdata.fill(0)

        if sample_start >= len(self.samples) or output_offset >= frames:
            return

        copy_frames = min(frames - output_offset, len(self.samples) - sample_start)
        if copy_frames <= 0:
            return

        sample_end = sample_start + copy_frames
        output_end = output_offset + copy_frames
        outdata[output_offset:output_end] = (
            self.samples[sample_start:sample_end] * self.volume
        )

    def _watch_until_done(self) -> None:
        try:
            duration = len(self.samples) / float(self.sample_rate)
            timeout = duration + self.start_delay_seconds + 3.0
            deadline = time.perf_counter() + timeout

            while time.perf_counter() < deadline:
                if self._stop_event.is_set():
                    return

                if self._stream and not self._stream.active:
                    self._close_stream()
                    self._finished_event.set()
                    self._notify_finished("finished", self._error)
                    return

                time.sleep(0.025)

            raise TimeoutError("Playback did not finish before timeout")
        except BaseException as exc:
            self._record_error(exc)
            self._close_stream()
            self._finished_event.set()
            self._notify_finished("error", exc)

    def _stream_finished(self) -> None:
        pass

    def _close_stream(self) -> None:
        if self._stream:
            try:
                self._stream.close()
            except Exception:
                pass

    def _record_error(self, error: BaseException) -> None:
        with self._status_lock:
            if self._error is None:
                self._error = error

    def _notify_finished(
        self,
        status: str,
        error: Optional[BaseException],
    ) -> None:
        with self._notify_lock:
            if self._notified:
                return
            self._notified = True

        try:
            self.on_finished(status, error)
        except Exception:
            pass


class _RingAudioBuffer:
    """Thread-safe float32 ring buffer for audio callbacks."""

    def __init__(self, frames: int, channels: int) -> None:
        self.channels = channels
        self._buffer = np.zeros((frames, channels), dtype=np.float32)
        self._read_cursor = 0
        self._write_cursor = 0
        self._available = 0
        self._lock = threading.Lock()

    def write(self, samples: np.ndarray) -> None:
        if samples.ndim == 1:
            samples = samples.reshape((-1, 1))

        samples = self._fit_channels(samples)
        frames = min(len(samples), len(self._buffer))
        if frames <= 0:
            return

        samples = samples[-frames:]

        with self._lock:
            overflow = max(0, self._available + frames - len(self._buffer))
            if overflow:
                self._read_cursor = (self._read_cursor + overflow) % len(self._buffer)
                self._available -= overflow

            first = min(frames, len(self._buffer) - self._write_cursor)
            second = frames - first
            self._buffer[self._write_cursor : self._write_cursor + first] = samples[:first]
            if second:
                self._buffer[:second] = samples[first:]

            self._write_cursor = (self._write_cursor + frames) % len(self._buffer)
            self._available += frames

    def read(self, frames: int) -> np.ndarray:
        output = np.zeros((frames, self.channels), dtype=np.float32)

        with self._lock:
            read_frames = min(frames, self._available)
            if read_frames <= 0:
                return output

            first = min(read_frames, len(self._buffer) - self._read_cursor)
            second = read_frames - first
            output[:first] = self._buffer[self._read_cursor : self._read_cursor + first]
            if second:
                output[first : first + second] = self._buffer[:second]

            self._read_cursor = (self._read_cursor + read_frames) % len(self._buffer)
            self._available -= read_frames

        return output

    def _fit_channels(self, samples: np.ndarray) -> np.ndarray:
        if samples.shape[1] == self.channels:
            return np.ascontiguousarray(samples, dtype=np.float32)

        if self.channels == 1:
            return np.mean(samples, axis=1, keepdims=True).astype(np.float32)

        if samples.shape[1] == 1:
            return np.repeat(samples, self.channels, axis=1).astype(np.float32)

        return samples[:, : self.channels].astype(np.float32)


def _is_loopback_device(device: object) -> bool:
    return isinstance(device, str) and device.startswith("loopback:")


def _loopback_device_index(device: int | str) -> int:
    if isinstance(device, str) and device.startswith("loopback:"):
        return int(device.split(":", 1)[1])
    return int(device)


class _MicInjectionMixer:
    """Continuous microphone/video passthrough plus soundboard injection."""

    def __init__(
        self,
        *,
        mic_device: Optional[int | str],
        injection_device: int | str,
        external_audio_device: Optional[int | str],
        mic_enabled: bool,
        sample_rate: int,
        channels: int,
        blocksize: int,
        latency: str | float,
        mic_volume: float,
        external_audio_volume: float,
        external_audio_enabled: bool,
        sound_volume: float,
    ) -> None:
        self.mic_device = mic_device
        self.injection_device = injection_device
        self.external_audio_device = external_audio_device
        self.mic_enabled = mic_enabled
        self.sample_rate = sample_rate
        self.channels = channels
        self.blocksize = blocksize
        self.latency = latency
        self.mic_volume = mic_volume
        self.external_audio_volume = external_audio_volume
        self.external_audio_enabled = external_audio_enabled
        self.sound_volume = sound_volume

        self._stop_event = threading.Event()
        self._running = threading.Event()
        self._status_lock = threading.Lock()
        self._last_error: Optional[BaseException] = None
        self._mic_stream: Optional[sd.InputStream] = None
        self._external_stream: Optional[sd.InputStream] = None
        self._external_loopback_audio: object = None
        self._external_loopback_stream: object = None
        self._output_stream: Optional[sd.OutputStream] = None

        buffer_frames = max(blocksize * 12, sample_rate // 2)
        self._mic_buffer = _RingAudioBuffer(buffer_frames, 1)
        self._external_buffer = _RingAudioBuffer(buffer_frames, channels)

        self._sound_samples: Optional[np.ndarray] = None
        self._sound_cursor = 0
        self._sound_playback_id: Optional[int] = None
        self._sound_lock = threading.Lock()

    @property
    def is_running(self) -> bool:
        return self._running.is_set()

    def start(self) -> None:
        self._stop_event.clear()

        try:
            if self.mic_enabled and self.mic_device is not None:
                self._mic_stream = sd.InputStream(
                    samplerate=self.sample_rate,
                    blocksize=self.blocksize,
                    device=self.mic_device,
                    channels=1,
                    dtype="float32",
                    latency=self.latency,
                    callback=self._mic_input_callback,
                )

            if self.external_audio_enabled and self.external_audio_device is not None:
                if _is_loopback_device(self.external_audio_device):
                    self._start_loopback_input()
                else:
                    self._external_stream = sd.InputStream(
                        samplerate=self.sample_rate,
                        blocksize=self.blocksize,
                        device=self.external_audio_device,
                        channels=self.channels,
                        dtype="float32",
                        latency=self.latency,
                        callback=self._external_input_callback,
                    )

            self._output_stream = sd.OutputStream(
                samplerate=self.sample_rate,
                blocksize=self.blocksize,
                device=self.injection_device,
                channels=self.channels,
                dtype="float32",
                latency=self.latency,
                callback=self._output_callback,
            )

            if self._mic_stream:
                self._mic_stream.start()
            if self._external_stream:
                self._external_stream.start()
            if self._external_loopback_stream:
                self._external_loopback_stream.start_stream()
            time.sleep(min(0.08, (self.blocksize / float(self.sample_rate)) * 2.0))
            self._output_stream.start()
            self._running.set()
        except BaseException:
            self.stop()
            raise

    def stop(self) -> None:
        self._stop_event.set()

        for stream in (self._mic_stream, self._external_stream, self._output_stream):
            if not stream:
                continue
            try:
                stream.abort()
            except Exception:
                pass
            try:
                stream.close()
            except Exception:
                pass

        if self._external_loopback_stream:
            try:
                self._external_loopback_stream.stop_stream()
            except Exception:
                pass
            try:
                self._external_loopback_stream.close()
            except Exception:
                pass
            self._external_loopback_stream = None

        if self._external_loopback_audio:
            try:
                self._external_loopback_audio.terminate()
            except Exception:
                pass
            self._external_loopback_audio = None

        self._running.clear()

    def play_sound(
        self,
        playback_id: int,
        samples: np.ndarray,
        *,
        volume: float,
    ) -> None:
        with self._sound_lock:
            self.sound_volume = volume
            self._sound_samples = samples
            self._sound_cursor = 0
            self._sound_playback_id = playback_id

    def stop_sound(self, playback_id: Optional[int] = None) -> None:
        with self._sound_lock:
            if playback_id is not None and playback_id != self._sound_playback_id:
                return

            self._sound_samples = None
            self._sound_cursor = 0
            self._sound_playback_id = None

    def _mic_input_callback(
        self,
        indata: np.ndarray,
        frames: int,
        time_info: object,
        status: sd.CallbackFlags,
    ) -> None:
        del time_info

        if status:
            self._record_error(RuntimeError(str(status)))

        if self._stop_event.is_set():
            raise sd.CallbackAbort

        self._mic_buffer.write(indata[:frames, :1])

    def _start_loopback_input(self) -> None:
        if pyaudio is None:
            raise RuntimeError("PyAudioWPatch is required for system/app audio capture")

        device_index = _loopback_device_index(self.external_audio_device)
        self._external_loopback_audio = pyaudio.PyAudio()

        def callback(
            in_data: bytes,
            frame_count: int,
            time_info: object,
            status: int,
        ) -> tuple[None, int]:
            del time_info
            if status:
                self._record_error(RuntimeError(str(status)))
            if self._stop_event.is_set():
                return (None, pyaudio.paAbort)

            samples = np.frombuffer(in_data, dtype=np.float32)
            if samples.size:
                samples = samples.reshape((-1, self.channels))
                self._external_buffer.write(samples[:frame_count])
            return (None, pyaudio.paContinue)

        self._external_loopback_stream = self._external_loopback_audio.open(
            format=pyaudio.paFloat32,
            channels=self.channels,
            rate=self.sample_rate,
            input=True,
            input_device_index=device_index,
            frames_per_buffer=self.blocksize,
            stream_callback=callback,
        )

    def _external_input_callback(
        self,
        indata: np.ndarray,
        frames: int,
        time_info: object,
        status: sd.CallbackFlags,
    ) -> None:
        del time_info

        if status:
            self._record_error(RuntimeError(str(status)))

        if self._stop_event.is_set():
            raise sd.CallbackAbort

        self._external_buffer.write(indata[:frames])

    def _output_callback(
        self,
        outdata: np.ndarray,
        frames: int,
        time_info: object,
        status: sd.CallbackFlags,
    ) -> None:
        del time_info

        if status:
            self._record_error(RuntimeError(str(status)))

        if self._stop_event.is_set():
            outdata.fill(0)
            raise sd.CallbackAbort

        if self.mic_enabled:
            mic = self._mic_buffer.read(frames) * self.mic_volume
            if self.channels == 1:
                outdata[:] = mic
            else:
                outdata[:] = np.repeat(mic, self.channels, axis=1)
        else:
            outdata.fill(0)

        if self.external_audio_enabled:
            outdata += self._external_buffer.read(frames) * self.external_audio_volume

        outdata += self._read_sound(frames) * self.sound_volume
        np.clip(outdata, -1.0, 1.0, out=outdata)

    def _read_sound(self, frames: int) -> np.ndarray:
        output = np.zeros((frames, self.channels), dtype=np.float32)

        with self._sound_lock:
            if self._sound_samples is None:
                return output

            available = len(self._sound_samples) - self._sound_cursor
            copy_frames = min(frames, available)
            if copy_frames <= 0:
                self._sound_samples = None
                self._sound_cursor = 0
                self._sound_playback_id = None
                return output

            start = self._sound_cursor
            end = start + copy_frames
            output[:copy_frames] = self._sound_samples[start:end]
            self._sound_cursor = end

            if self._sound_cursor >= len(self._sound_samples):
                self._sound_samples = None
                self._sound_cursor = 0
                self._sound_playback_id = None

        return output

    def _record_error(self, error: BaseException) -> None:
        with self._status_lock:
            if self._last_error is None:
                self._last_error = error


class _DualPlayback:
    """Internal one-shot player that owns two sounddevice streams."""

    def __init__(
        self,
        *,
        playback_id: int,
        samples: np.ndarray,
        sample_rate: int,
        channels: int,
        monitor_device: int | str,
        injection_device: int | str,
        monitor_volume: float,
        injection_volume: float,
        blocksize: int,
        latency: str | float,
        start_delay_seconds: float,
        on_finished: PlaybackCallback,
    ) -> None:
        self.playback_id = playback_id
        self.samples = samples
        self.sample_rate = sample_rate
        self.channels = channels
        self.monitor_device = monitor_device
        self.injection_device = injection_device
        self.monitor_volume = monitor_volume
        self.injection_volume = injection_volume
        self.blocksize = blocksize
        self.latency = latency
        self.start_delay_seconds = start_delay_seconds
        self.on_finished = on_finished

        self._stop_event = threading.Event()
        self._finished_event = threading.Event()
        self._status_lock = threading.Lock()
        self._notify_lock = threading.Lock()
        self._error: Optional[BaseException] = None
        self._notified = False
        self._streams: list[sd.OutputStream] = []
        self._started_at = 0.0
        self._watcher: Optional[threading.Thread] = None

    @property
    def is_running(self) -> bool:
        return not self._finished_event.is_set()

    def start(self) -> None:
        # Shared wall-clock target. Each stream callback maps this to its own
        # PortAudio DAC clock so sample zero is scheduled at the same app time.
        self._started_at = time.perf_counter() + self.start_delay_seconds

        monitor_callback = self._make_callback(lambda: self.monitor_volume)
        injection_callback = self._make_callback(lambda: self.injection_volume)

        try:
            self._streams = [
                sd.OutputStream(
                    samplerate=self.sample_rate,
                    blocksize=self.blocksize,
                    device=self.monitor_device,
                    channels=self.channels,
                    dtype="float32",
                    latency=self.latency,
                    callback=monitor_callback,
                    finished_callback=self._stream_finished,
                ),
                sd.OutputStream(
                    samplerate=self.sample_rate,
                    blocksize=self.blocksize,
                    device=self.injection_device,
                    channels=self.channels,
                    dtype="float32",
                    latency=self.latency,
                    callback=injection_callback,
                    finished_callback=self._stream_finished,
                ),
            ]

            for stream in self._streams:
                stream.start()
        except BaseException as exc:
            self._record_error(exc)
            self.stop(report=False)
            self._notify_finished("error", exc)
            raise

        self._watcher = threading.Thread(
            target=self._watch_until_done,
            name=f"DualPlayback-{self.playback_id}",
            daemon=True,
        )
        self._watcher.start()

    def stop(self, *, report: bool = True) -> None:
        self._stop_event.set()

        for stream in self._streams:
            try:
                stream.abort()
            except Exception:
                pass
            try:
                stream.close()
            except Exception:
                pass

        self._finished_event.set()
        if report:
            self._notify_finished("stopped", None)

    def _make_callback(
        self,
        volume_getter: Callable[[], float],
    ) -> Callable[[np.ndarray, int, object, sd.CallbackFlags], None]:
        state = {
            "target_dac_time": None,
            "fallback_started": False,
            "fallback_cursor": 0,
        }

        def callback(
            outdata: np.ndarray,
            frames: int,
            time_info: object,
            status: sd.CallbackFlags,
        ) -> None:
            if status:
                # Avoid printing or logging from a real-time callback. Store it
                # so the watcher thread can report it if playback fails.
                self._record_error(RuntimeError(str(status)))

            if self._stop_event.is_set():
                outdata.fill(0)
                raise sd.CallbackAbort

            output_dac_time = getattr(time_info, "outputBufferDacTime", None)
            if output_dac_time is None:
                self._fill_from_fallback_clock(outdata, frames, volume_getter, state)
                return

            if state["target_dac_time"] is None:
                delay = max(0.0, self._started_at - time.perf_counter())
                state["target_dac_time"] = float(output_dac_time) + delay

            target_dac_time = float(state["target_dac_time"])
            start_frame = int((float(output_dac_time) - target_dac_time) * self.sample_rate)

            if start_frame + frames <= 0:
                outdata.fill(0)
                return

            output_offset = max(0, -start_frame)
            sample_start = max(0, start_frame)

            self._fill_output(outdata, output_offset, sample_start, frames, volume_getter)

            if sample_start + frames - output_offset >= len(self.samples):
                raise sd.CallbackStop

        return callback

    def _fill_from_fallback_clock(
        self,
        outdata: np.ndarray,
        frames: int,
        volume_getter: Callable[[], float],
        state: dict[str, object],
    ) -> None:
        if not state["fallback_started"]:
            if time.perf_counter() < self._started_at:
                outdata.fill(0)
                return

            state["fallback_started"] = True

        sample_start = int(state["fallback_cursor"])
        state["fallback_cursor"] = sample_start + frames
        self._fill_output(outdata, 0, sample_start, frames, volume_getter)

        if sample_start + frames >= len(self.samples):
            raise sd.CallbackStop

    def _fill_output(
        self,
        outdata: np.ndarray,
        output_offset: int,
        sample_start: int,
        frames: int,
        volume_getter: Callable[[], float],
    ) -> None:
        outdata.fill(0)

        if sample_start >= len(self.samples) or output_offset >= frames:
            return

        copy_frames = min(frames - output_offset, len(self.samples) - sample_start)
        if copy_frames <= 0:
            return

        sample_end = sample_start + copy_frames
        output_end = output_offset + copy_frames
        outdata[output_offset:output_end] = (
            self.samples[sample_start:sample_end] * volume_getter()
        )

    def _watch_until_done(self) -> None:
        try:
            duration = len(self.samples) / float(self.sample_rate)
            timeout = duration + self.start_delay_seconds + 3.0
            deadline = time.perf_counter() + timeout

            while time.perf_counter() < deadline:
                if self._stop_event.is_set():
                    return

                if self._all_streams_stopped():
                    self._close_streams()
                    self._finished_event.set()
                    self._notify_finished("finished", self._error)
                    return

                time.sleep(0.025)

            raise TimeoutError("Playback did not finish before timeout")
        except BaseException as exc:
            self._record_error(exc)
            self._close_streams()
            self._finished_event.set()
            self._notify_finished("error", exc)

    def _all_streams_stopped(self) -> bool:
        return bool(self._streams) and all(not stream.active for stream in self._streams)

    def _stream_finished(self) -> None:
        # PortAudio calls this from its own thread. The watcher owns cleanup.
        pass

    def _close_streams(self) -> None:
        for stream in self._streams:
            try:
                stream.close()
            except Exception:
                pass

    def _record_error(self, error: BaseException) -> None:
        with self._status_lock:
            if self._error is None:
                self._error = error

    def _notify_finished(
        self,
        status: str,
        error: Optional[BaseException],
    ) -> None:
        with self._notify_lock:
            if self._notified:
                return
            self._notified = True

        try:
            self.on_finished(status, error)
        except Exception:
            pass


def output_device_names(devices: Optional[Iterable[AudioDevice]] = None) -> list[str]:
    """
    Convenience helper for simple UIs or debugging.

    Returns strings like:
        "12: Headphones (Realtek Audio) [Windows WASAPI]"
    """

    devices = devices if devices is not None else AudioHandler.list_output_devices()
    return [
        f"{device.index}: {device.name} [{device.hostapi}]"
        for device in devices
    ]


def _run_cli() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Audio backend diagnostic. Run main.py to open the graphical "
            "soundboard interface."
        )
    )
    parser.add_argument(
        "--list-devices",
        action="store_true",
        help="List output devices and exit.",
    )
    parser.add_argument(
        "--file",
        type=Path,
        help="Optional audio file to test-play.",
    )
    parser.add_argument(
        "--monitor",
        type=int,
        help="Output device index for your headphones.",
    )
    parser.add_argument(
        "--injection",
        type=int,
        help="Output device index for the virtual audio cable input.",
    )
    args = parser.parse_args()

    if args.list_devices or not args.file:
        print("This file is the audio backend, not the GUI.")
        print("Open the app with: python main.py")
        print()
        print("Available input devices:")
        for device in AudioHandler.list_input_devices():
            print(f"  {device.index}: {device.name} [{device.hostapi}]")

        print()
        print("Available output devices:")
        for name in output_device_names():
            print(f"  {name}")

        if not args.file:
            print()
            print(
                "Backend test example: "
                "python audio_handler.py --file clip.wav --monitor 19 --injection 14"
            )
        return 0

    if args.monitor is None or args.injection is None:
        parser.error("--file requires both --monitor and --injection device indexes")

    done = threading.Event()

    def on_finished(status: str, error: Optional[BaseException]) -> None:
        print(f"Playback {status}")
        if error:
            print(f"Audio warning/error: {error}")
        done.set()

    handler = AudioHandler(
        monitor_device=args.monitor,
        injection_device=args.injection,
    )
    handler.play_file(args.file, on_finished=on_finished)
    done.wait()
    return 0


if __name__ == "__main__":
    raise SystemExit(_run_cli())



