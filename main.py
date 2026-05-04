from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Optional

from PyQt5.QtCore import QTimer, Qt, pyqtSignal
from PyQt5.QtWidgets import (
    QApplication,
    QCheckBox,
    QComboBox,
    QFileDialog,
    QFrame,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QListView,
    QMainWindow,
    QMessageBox,
    QInputDialog,
    QPushButton,
    QSizePolicy,
    QScrollArea,
    QSlider,
    QStyle,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)
from PyQt5.QtGui import QIcon

from audio_handler import AudioDevice, AudioHandler


APP_NAME = "LineCast"
APP_ID = "LineCast.Soundboard"
AUDIO_FILTER = "Audio files (*.mp3 *.wav);;All files (*.*)"


def _runtime_dir() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def _resource_path(relative_path: str) -> Path:
    bundle_dir = getattr(sys, "_MEIPASS", None)
    if bundle_dir:
        return Path(bundle_dir) / relative_path
    return Path(__file__).resolve().parent / relative_path


def _config_path() -> Path:
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or os.environ.get("LOCALAPPDATA")
        if base:
            return Path(base) / APP_NAME / "config.json"

    return _runtime_dir() / "config.json"


CONFIG_PATH = _config_path()
LEGACY_CONFIG_PATH = _runtime_dir() / "config.json"
APP_ICON_PATH = _resource_path("assets/linecast.ico")


APP_STYLESHEET = """
* {
    font-family: "Segoe UI", "Inter", Arial, sans-serif;
    font-size: 10pt;
}

QMainWindow,
QWidget#appRoot {
    background: #101216;
    color: #eef2f7;
}

QFrame#topBar,
QGroupBox,
QFrame#libraryPanel {
    background: #181b21;
    border: 1px solid #2a3039;
    border-radius: 8px;
}

QFrame#topBar {
    background: #161a22;
}

QLabel#appTitle {
    color: #f8fafc;
    font-size: 20pt;
    font-weight: 700;
}

QLabel#fieldLabel {
    color: #98a2b3;
}

QLabel#statusChip {
    background: #1f2937;
    border: 1px solid #334155;
    border-radius: 8px;
    color: #dbeafe;
    font-weight: 600;
    padding: 7px 11px;
}

QLabel#volumeValue {
    color: #cbd5e1;
    min-width: 34px;
}

QLabel#reviewTime {
    color: #cbd5e1;
    min-width: 92px;
}

QGroupBox {
    color: #e5e7eb;
    font-weight: 700;
    margin-top: 14px;
    padding: 16px 14px 14px 14px;
}

QGroupBox::title {
    subcontrol-origin: margin;
    subcontrol-position: top left;
    left: 12px;
    padding: 0 6px;
    color: #f8fafc;
}

QComboBox,
QLineEdit {
    background: #0f1720;
    border: 1px solid #303846;
    border-radius: 7px;
    combobox-popup: 0;
    color: #f8fafc;
    min-height: 34px;
    padding: 0 10px;
    selection-background-color: #2563eb;
}

QComboBox:hover,
QLineEdit:hover {
    border-color: #3f4b5f;
}

QComboBox:focus,
QLineEdit:focus {
    border-color: #38bdf8;
}

QComboBox::drop-down {
    border: 0;
    width: 28px;
}

QComboBox QAbstractItemView {
    background: #11151b;
    border: 1px solid #334155;
    color: #eef2f7;
    outline: 0;
    padding: 5px;
    selection-background-color: #0ea5e9;
    selection-color: #06121f;
}

QComboBox QAbstractItemView::item {
    min-height: 28px;
    padding: 6px 10px;
}

QComboBox QAbstractItemView::item:hover {
    background: #1f2937;
    color: #f8fafc;
}

QCheckBox {
    color: #cbd5e1;
    spacing: 8px;
}

QCheckBox::indicator {
    background: #0f1720;
    border: 1px solid #303846;
    border-radius: 5px;
    height: 18px;
    width: 18px;
}

QCheckBox::indicator:hover {
    border-color: #38bdf8;
}

QCheckBox::indicator:checked {
    background: #22c55e;
    border-color: #86efac;
}

QPushButton {
    background: #232a36;
    border: 1px solid #354052;
    border-radius: 7px;
    color: #f8fafc;
    font-weight: 650;
    min-height: 34px;
    padding: 0 13px;
}

QPushButton:hover {
    background: #2c3544;
    border-color: #475569;
}

QPushButton:pressed {
    background: #1d2430;
}

QPushButton#primaryButton {
    background: #0ea5e9;
    border-color: #38bdf8;
    color: #06121f;
}

QPushButton#primaryButton:hover {
    background: #38bdf8;
}

QPushButton#dangerButton {
    background: #3a2028;
    border-color: #7f1d1d;
    color: #fecaca;
}

QPushButton#dangerButton:hover {
    background: #4a2530;
    border-color: #ef4444;
}

QTableWidget {
    background: #11151b;
    alternate-background-color: #151a22;
    border: 1px solid #29313d;
    border-radius: 8px;
    color: #e5e7eb;
    gridline-color: #252c36;
    selection-background-color: #123c55;
    selection-color: #ffffff;
}

QTableWidget::item {
    border-bottom: 1px solid #202732;
    padding: 8px;
}

QTableWidget::item:selected {
    background: #123c55;
}

QHeaderView::section {
    background: #1d2430;
    border: 0;
    border-right: 1px solid #2a3039;
    color: #aab6c5;
    font-weight: 700;
    padding: 8px;
}

QSlider::groove:horizontal {
    background: #29313d;
    border-radius: 3px;
    height: 6px;
}

QSlider::sub-page:horizontal {
    background: #22c55e;
    border-radius: 3px;
}

QSlider::handle:horizontal {
    background: #f8fafc;
    border: 2px solid #22c55e;
    border-radius: 8px;
    height: 16px;
    margin: -6px 0;
    width: 16px;
}

QScrollBar:vertical {
    background: #11151b;
    width: 10px;
}

QScrollBar::handle:vertical {
    background: #334155;
    border-radius: 5px;
    min-height: 30px;
}

QScrollBar::add-line:vertical,
QScrollBar::sub-line:vertical {
    height: 0;
}
"""


DEFAULT_CONFIG: dict[str, Any] = {
    "monitor_device": None,
    "injection_device": None,
    "mic_device": None,
    "external_audio_device": None,
    "monitor_volume": 85,
    "injection_volume": 85,
    "mic_volume": 85,
    "external_audio_volume": 85,
    "mic_passthrough_enabled": True,
    "external_audio_enabled": False,
    "show_advanced_devices": False,
    "sounds": [],
}


class SoundPadWindow(QMainWindow):
    playback_finished = pyqtSignal(str, object)

    def __init__(self) -> None:
        super().__init__()

        self.config = self._load_config()
        self._defer_mic_start = True
        self.output_devices: list[AudioDevice] = []
        self.input_devices: list[AudioDevice] = []
        self.audio = AudioHandler(
            monitor_device=self.config.get("monitor_device"),
            injection_device=self.config.get("injection_device"),
            mic_device=self.config.get("mic_device"),
            external_audio_device=self.config.get("external_audio_device"),
        )
        self.audio.set_mic_passthrough_enabled(
            bool(self.config.get("mic_passthrough_enabled", True))
        )
        self.audio.set_external_audio_enabled(
            bool(self.config.get("external_audio_enabled", False))
        )

        self.setWindowTitle(APP_NAME)
        self.setWindowIcon(QIcon(str(APP_ICON_PATH)))
        self.setMinimumSize(1040, 680)

        self.monitor_combo = QComboBox()
        self.injection_combo = QComboBox()
        self.mic_combo = QComboBox()
        self.external_audio_combo = QComboBox()
        self._setup_combo(self.monitor_combo)
        self._setup_combo(self.injection_combo)
        self._setup_combo(self.mic_combo)
        self._setup_combo(self.external_audio_combo)
        self.monitor_slider = QSlider(Qt.Horizontal)
        self.injection_slider = QSlider(Qt.Horizontal)
        self.mic_slider = QSlider(Qt.Horizontal)
        self.external_audio_slider = QSlider(Qt.Horizontal)
        self.mic_passthrough_check = QCheckBox("Mix mic into virtual input")
        self.external_audio_check = QCheckBox("Mix device/app audio into virtual input")
        self.advanced_devices_check = QCheckBox("Show advanced audio devices")
        self.search_input = QLineEdit()
        self.table = QTableWidget(0, 2)
        self.add_button = QPushButton("Add Sounds")
        self.rename_button = QPushButton("Rename")
        self.change_path_button = QPushButton("Change Path")
        self.delete_button = QPushButton("Delete")
        self.play_button = QPushButton("Play")
        self.stop_button = QPushButton("Stop")
        self.status_label = QLabel("Ready")
        self.monitor_value_label = QLabel("85%")
        self.injection_value_label = QLabel("85%")
        self.mic_value_label = QLabel("85%")
        self.external_audio_value_label = QLabel("85%")
        self.review_slider = QSlider(Qt.Horizontal)
        self.review_time_label = QLabel("00:00 / 00:00")
        self.playback_timer = QTimer(self)
        self._current_sound_path: Optional[Path] = None
        self._current_duration_ms = 0
        self._playback_start_offset_ms = 0
        self._playback_started_at = 0
        self._is_review_seeking = False
        self._ignore_next_stopped = False

        self._build_ui()
        self._set_initial_window_geometry()
        self._connect_signals()
        self._load_devices()
        self._load_sound_table()
        self._apply_saved_volumes()
        QTimer.singleShot(250, self._enable_startup_mic_passthrough)

    def _build_ui(self) -> None:
        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        scroll_area.setFrameShape(QFrame.NoFrame)

        central = QWidget()
        central.setObjectName("appRoot")
        root = QVBoxLayout(central)
        root.setContentsMargins(16, 14, 16, 14)
        root.setSpacing(10)

        top_bar = QFrame()
        top_bar.setObjectName("topBar")
        top_layout = QHBoxLayout(top_bar)
        top_layout.setContentsMargins(16, 12, 16, 12)
        top_bar.setMinimumHeight(70)

        title_block = QVBoxLayout()
        title_block.setSpacing(1)
        title = QLabel(APP_NAME)
        title.setObjectName("appTitle")
        title_block.addWidget(title)

        self.status_label.setObjectName("statusChip")
        self.status_label.setAlignment(Qt.AlignCenter)

        top_layout.addLayout(title_block, 1)
        top_layout.addWidget(self.status_label)
        root.addWidget(top_bar)

        device_group = QGroupBox("Audio Routing")
        device_layout = QGridLayout(device_group)
        device_layout.setHorizontalSpacing(12)
        device_layout.setVerticalSpacing(8)
        device_layout.setColumnStretch(1, 1)

        device_layout.addWidget(self._field_label("Monitor Device"), 0, 0)
        device_layout.addWidget(self.monitor_combo, 0, 1, 1, 2)

        self._setup_slider(self.monitor_slider)
        self._setup_slider(self.injection_slider)
        self._setup_slider(self.mic_slider)
        self._setup_slider(self.external_audio_slider)
        self._setup_review_slider()
        self.monitor_value_label.setObjectName("volumeValue")
        self.injection_value_label.setObjectName("volumeValue")
        self.mic_value_label.setObjectName("volumeValue")
        self.external_audio_value_label.setObjectName("volumeValue")
        self.review_time_label.setObjectName("reviewTime")
        self.mic_passthrough_check.setChecked(
            bool(self.config.get("mic_passthrough_enabled", True))
        )
        self.external_audio_check.setChecked(
            bool(self.config.get("external_audio_enabled", False))
        )
        self.advanced_devices_check.setChecked(
            bool(self.config.get("show_advanced_devices", False))
        )

        device_layout.addWidget(self._field_label("Monitor Volume"), 1, 0)
        device_layout.addWidget(self.monitor_slider, 1, 1)
        device_layout.addWidget(self.monitor_value_label, 1, 2)
        device_layout.addWidget(self._field_label("Mic Input"), 2, 0)
        device_layout.addWidget(self.mic_combo, 2, 1, 1, 2)
        device_layout.addWidget(self._field_label("Mic Volume"), 3, 0)
        device_layout.addWidget(self.mic_slider, 3, 1)
        device_layout.addWidget(self.mic_value_label, 3, 2)
        device_layout.addWidget(self.mic_passthrough_check, 4, 1, 1, 2)
        device_layout.addWidget(self._field_label("Device/App Audio Source"), 5, 0)
        device_layout.addWidget(self.external_audio_combo, 5, 1, 1, 2)
        device_layout.addWidget(self._field_label("Device/App Volume"), 6, 0)
        device_layout.addWidget(self.external_audio_slider, 6, 1)
        device_layout.addWidget(self.external_audio_value_label, 6, 2)
        device_layout.addWidget(self.external_audio_check, 7, 1, 1, 2)
        device_layout.addWidget(self._field_label("Injection Device"), 8, 0)
        device_layout.addWidget(self.injection_combo, 8, 1, 1, 2)
        device_layout.addWidget(self._field_label("Sound Injection Volume"), 9, 0)
        device_layout.addWidget(self.injection_slider, 9, 1)
        device_layout.addWidget(self.injection_value_label, 9, 2)
        device_layout.addWidget(self.advanced_devices_check, 10, 1, 1, 2)

        root.addWidget(device_group)

        library_panel = QFrame()
        library_panel.setObjectName("libraryPanel")
        library_layout = QVBoxLayout(library_panel)
        library_layout.setContentsMargins(14, 14, 14, 14)
        library_layout.setSpacing(9)

        controls = QHBoxLayout()
        controls.setSpacing(10)
        self.search_input.setPlaceholderText("Search sounds")
        controls.addWidget(self.search_input, 1)
        controls.addWidget(self.add_button)
        controls.addWidget(self.rename_button)
        controls.addWidget(self.change_path_button)
        controls.addWidget(self.delete_button)
        controls.addWidget(self.play_button)
        controls.addWidget(self.stop_button)
        library_layout.addLayout(controls)

        review_layout = QHBoxLayout()
        review_layout.setSpacing(10)
        review_layout.addWidget(self._field_label("Review"))
        review_layout.addWidget(self.review_slider, 1)
        review_layout.addWidget(self.review_time_label)
        library_layout.addLayout(review_layout)

        self.table.setHorizontalHeaderLabels(["Sound", "Path"])
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeToContents)
        self.table.horizontalHeader().setSectionResizeMode(1, QHeaderView.Stretch)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)
        self.table.setSelectionMode(QTableWidget.SingleSelection)
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.verticalHeader().setVisible(False)
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.setWordWrap(False)
        self.table.verticalHeader().setDefaultSectionSize(38)
        self.table.setMinimumHeight(170)
        library_panel.setMinimumHeight(260)
        library_layout.addWidget(self.table, 1)

        root.addWidget(library_panel, 1)

        scroll_area.setWidget(central)
        self.setCentralWidget(scroll_area)
        self._apply_button_icons()

    def _connect_signals(self) -> None:
        self.add_button.clicked.connect(self.add_sounds)
        self.rename_button.clicked.connect(self.rename_selected_sound)
        self.change_path_button.clicked.connect(self.change_selected_sound_path)
        self.delete_button.clicked.connect(self.delete_selected_sound)
        self.play_button.clicked.connect(self.play_selected)
        self.stop_button.clicked.connect(self.stop_playback)
        self.search_input.textChanged.connect(self._filter_table)
        self.table.doubleClicked.connect(self.play_selected)
        self.monitor_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.injection_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.mic_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.external_audio_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.monitor_slider.valueChanged.connect(self._volume_changed)
        self.injection_slider.valueChanged.connect(self._volume_changed)
        self.mic_slider.valueChanged.connect(self._volume_changed)
        self.external_audio_slider.valueChanged.connect(self._volume_changed)
        self.mic_passthrough_check.toggled.connect(self._mic_passthrough_changed)
        self.external_audio_check.toggled.connect(self._external_audio_changed)
        self.advanced_devices_check.toggled.connect(self._advanced_devices_changed)
        self.playback_finished.connect(self._playback_finished)
        self.playback_timer.timeout.connect(self._update_review_progress)
        self.review_slider.sliderPressed.connect(self._review_slider_pressed)
        self.review_slider.sliderMoved.connect(self._review_slider_moved)
        self.review_slider.sliderReleased.connect(self._review_slider_released)
        self.table.itemSelectionChanged.connect(self._sound_selection_changed)

    def _setup_slider(self, slider: QSlider) -> None:
        slider.setRange(0, 100)
        slider.setSingleStep(1)
        slider.setPageStep(5)

    def _setup_review_slider(self) -> None:
        self.review_slider.setRange(0, 0)
        self.review_slider.setSingleStep(250)
        self.review_slider.setPageStep(5000)
        self.review_slider.setEnabled(False)

    def _setup_combo(self, combo: QComboBox) -> None:
        view = QListView(combo)
        view.setUniformItemSizes(True)
        view.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOn)
        view.setTextElideMode(Qt.ElideRight)
        combo.setView(view)
        combo.setMinimumWidth(0)
        combo.setMaxVisibleItems(9)
        combo.setMinimumContentsLength(18)
        combo.setSizeAdjustPolicy(QComboBox.AdjustToMinimumContentsLengthWithIcon)
        combo.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)

    def _field_label(self, text: str) -> QLabel:
        label = QLabel(text)
        label.setObjectName("fieldLabel")
        return label

    def _apply_button_icons(self) -> None:
        style = self.style()
        self.add_button.setIcon(style.standardIcon(QStyle.SP_FileDialogNewFolder))
        self.rename_button.setIcon(style.standardIcon(QStyle.SP_FileDialogDetailedView))
        self.change_path_button.setIcon(style.standardIcon(QStyle.SP_DirOpenIcon))
        trash_icon = getattr(QStyle, "SP_TrashIcon", QStyle.SP_DialogDiscardButton)
        self.delete_button.setIcon(style.standardIcon(trash_icon))
        self.play_button.setIcon(style.standardIcon(QStyle.SP_MediaPlay))
        self.stop_button.setIcon(style.standardIcon(QStyle.SP_MediaStop))
        self.play_button.setObjectName("primaryButton")
        self.stop_button.setObjectName("dangerButton")
        self.delete_button.setObjectName("dangerButton")

        for button in (
            self.add_button,
            self.rename_button,
            self.change_path_button,
            self.delete_button,
            self.play_button,
            self.stop_button,
        ):
            button.setCursor(Qt.PointingHandCursor)

    def _set_initial_window_geometry(self) -> None:
        screen = QApplication.primaryScreen()
        if screen is None:
            self.resize(980, 620)
            return

        available = screen.availableGeometry()
        max_width = max(480, available.width() - 80)
        max_height = max(360, available.height() - 80)
        width = min(max_width, max(1080, int(available.width() * 0.82)))
        height = min(max_height, max(680, int(available.height() * 0.86)))

        self.resize(width, height)

        frame = self.frameGeometry()
        frame.moveCenter(available.center())
        self.move(frame.topLeft())

    def _load_devices(self) -> None:
        try:
            self.output_devices = AudioHandler.list_output_devices()
            self.input_devices = AudioHandler.list_input_devices()
        except Exception as exc:
            QMessageBox.critical(self, "Audio Devices", str(exc))
            self.output_devices = []
            self.input_devices = []

        show_advanced = self.advanced_devices_check.isChecked()
        combos = (
            self.monitor_combo,
            self.injection_combo,
            self.mic_combo,
            self.external_audio_combo,
        )
        for combo in combos:
            combo.blockSignals(True)

        try:
            self.monitor_combo.clear()
            self.injection_combo.clear()
            self.mic_combo.clear()
            self.external_audio_combo.clear()

            self.monitor_combo.addItem("Select headphones/speakers", None)
            self._populate_device_combo(
                self.monitor_combo,
                self.output_devices,
                role="monitor",
                show_advanced=show_advanced,
                selected_device=self.config.get("monitor_device"),
            )

            self.mic_combo.addItem("Select your real microphone", None)
            self._populate_device_combo(
                self.mic_combo,
                self.input_devices,
                role="mic",
                show_advanced=show_advanced,
                selected_device=self.config.get("mic_device"),
            )

            self.external_audio_combo.addItem("Select output device to capture", None)
            external_devices = AudioHandler.list_loopback_devices()
            if not external_devices:
                external_devices = self.input_devices
            self._populate_device_combo(
                self.external_audio_combo,
                external_devices,
                role="external",
                show_advanced=show_advanced,
                selected_device=self.config.get("external_audio_device"),
            )

            self.injection_combo.addItem("Select virtual cable input", None)
            self._populate_device_combo(
                self.injection_combo,
                self.output_devices,
                role="injection",
                show_advanced=show_advanced,
                selected_device=self.config.get("injection_device"),
            )

            self._select_combo_device(
                self.monitor_combo,
                self.config.get("monitor_device"),
            )
            self._select_combo_device(
                self.injection_combo,
                self.config.get("injection_device"),
            )
            self._select_combo_device(
                self.mic_combo,
                self.config.get("mic_device"),
            )
            self._select_combo_device(
                self.external_audio_combo,
                self.config.get("external_audio_device"),
            )
        finally:
            for combo in combos:
                combo.blockSignals(False)

        self._device_selection_changed()

    def _populate_device_combo(
        self,
        combo: QComboBox,
        devices: list[AudioDevice],
        *,
        role: str,
        show_advanced: bool,
        selected_device: Optional[int],
    ) -> None:
        shown_devices = (
            devices
            if show_advanced
            else self._recommended_devices(devices, role=role)
        )
        shown_indexes = {device.index for device in shown_devices}

        for device in shown_devices:
            self._add_device_item(combo, device, role=role, show_advanced=show_advanced)

        if selected_device is None or selected_device in shown_indexes:
            return

        if not show_advanced:
            return

        current = next(
            (device for device in devices if device.index == selected_device),
            None,
        )
        if current:
            self._add_device_item(combo, current, role=role, show_advanced=True)

    def _add_device_item(
        self,
        combo: QComboBox,
        device: AudioDevice,
        *,
        role: str,
        show_advanced: bool,
    ) -> None:
        row = combo.count()
        combo.addItem(
            self._device_label(device, role=role, show_advanced=show_advanced),
            device.index,
        )
        combo.setItemData(
            row,
            f"{device.index}: {device.name} [{device.hostapi}]",
            Qt.ToolTipRole,
        )

    def _recommended_devices(
        self,
        devices: list[AudioDevice],
        *,
        role: str,
    ) -> list[AudioDevice]:
        filtered = [
            device
            for device in devices
            if self._is_recommended_device(device, role=role)
        ]
        filtered.sort(key=lambda device: self._device_sort_key(device, role=role))

        by_name: dict[str, AudioDevice] = {}
        for device in filtered:
            by_name.setdefault(self._device_key(device), device)

        return list(by_name.values())

    def _is_recommended_device(self, device: AudioDevice, *, role: str) -> bool:
        name = self._clean_device_name(device.name).lower()
        if self._is_system_wrapper(name):
            return False
        if re.search(r"\(\s*\)", name):
            return False

        if role == "injection":
            return self._is_virtual_cable_injection(name)

        if role == "mic":
            if self._is_virtual_audio_device(name):
                return False
            if "stereo mix" in name or "pc speaker" in name:
                return False
            return any(token in name for token in ("microphone", "mic", "headset"))

        if role == "external":
            if device.hostapi == "Windows WASAPI Loopback":
                return True
            return "stereo mix" in name

        if role == "monitor":
            if self._is_virtual_audio_device(name):
                return False
            if name.startswith("speakers 1 ") or name.startswith("speakers 2 "):
                return False
            return any(
                token in name
                for token in ("headphones", "speakers", "headset")
            )

        return True

    def _device_label(
        self,
        device: AudioDevice,
        *,
        role: str,
        show_advanced: bool,
    ) -> str:
        name = self._clean_device_name(device.name)
        if show_advanced:
            return f"{device.index}: {name} [{device.hostapi}]"

        if role == "injection":
            return f"{self._friendly_injection_name(name)} -> meeting apps"

        if role == "external":
            if device.hostapi == "Windows WASAPI Loopback":
                return f"{self._friendly_loopback_name(name)} -> device/app audio"
            if "stereo mix" in name.lower():
                return f"{name} -> legacy capture"

        return name

    def _device_key(self, device: AudioDevice) -> str:
        name = self._clean_device_name(device.name).lower()
        if self._is_virtual_cable_injection(name):
            return "vb audio virtual cable injection"
        if "cable input" in name:
            return "cable input"
        if "cable output" in name:
            return "cable output"
        if "loopback" in name:
            return f"loopback {name}"
        if "stereo mix" in name:
            return "stereo mix"
        if "microphone array" in name:
            return "microphone array"
        if "headset" in name and "airpods pro" in name:
            return "airpods headset"
        if "headphones" in name and "airpods pro" in name:
            return "airpods headphones"
        if "speakers" in name and "realtek" in name:
            return "realtek speakers"
        return re.sub(r"\s+", " ", name).strip()

    def _device_sort_key(self, device: AudioDevice, *, role: str) -> tuple[int, int, str]:
        name = self._clean_device_name(device.name).lower()
        hostapi_rank = {
            "Windows WASAPI": 0,
            "Windows DirectSound": 1,
            "MME": 2,
            "Windows WDM-KS": 3,
        }.get(device.hostapi, 4)

        role_rank = 2
        if role == "injection" and self._is_virtual_cable_injection(name):
            role_rank = 0
        elif role == "mic" and "microphone" in name:
            role_rank = 0
        elif role == "mic" and "headset" in name:
            role_rank = 1
        elif role == "external" and "realtek" in name:
            role_rank = 0
        elif role == "external" and "loopback" in name:
            role_rank = 1
        elif role == "external" and "stereo mix" in name:
            role_rank = 2
        elif role == "monitor" and "headphones" in name:
            role_rank = 0
        elif role == "monitor" and "speakers" in name:
            role_rank = 1

        return (role_rank, hostapi_rank, name)

    def _clean_device_name(self, name: str) -> str:
        text = re.sub(r"\s+", " ", name).strip()
        if "@System32" in text and "AirPods Pro" in text:
            return "Headset (AirPods Pro Hands-Free)"
        return text

    def _is_system_wrapper(self, lower_name: str) -> bool:
        return (
            "microsoft sound mapper" in lower_name
            or lower_name.startswith("primary sound")
        )

    def _is_virtual_audio_device(self, lower_name: str) -> bool:
        return (
            "vb-audio" in lower_name
            or "cable input" in lower_name
            or "cable output" in lower_name
        )

    def _is_virtual_cable_injection(self, lower_name: str) -> bool:
        if "16ch" in lower_name or "16 ch" in lower_name:
            return False
        if "vb-audio point" in lower_name:
            return False
        return (
            "cable input" in lower_name
            or (
                "vb-audio virtual cable" in lower_name
                and "cable output" not in lower_name
            )
        )

    def _friendly_injection_name(self, name: str) -> str:
        lower_name = name.lower()
        if "vb-audio virtual cable" in lower_name:
            return "VB-Audio Virtual Cable Input"
        return name

    def _friendly_loopback_name(self, name: str) -> str:
        return name.replace(" [Loopback]", "").replace("[Loopback]", "").strip()

    def _select_combo_device(
        self,
        combo: QComboBox,
        device_index: Optional[int],
    ) -> None:
        if device_index is None:
            combo.setCurrentIndex(0)
            return

        for index in range(combo.count()):
            if combo.itemData(index) == device_index:
                combo.setCurrentIndex(index)
                return

    def _load_sound_table(self) -> None:
        self.table.setRowCount(0)

        for sound in self._sound_entries():
            self._append_sound_row(Path(sound["path"]), sound.get("name"))

        self._filter_table(self.search_input.text())
        self._select_first_visible_sound()

    def _sound_entries(self) -> list[dict[str, str]]:
        entries: list[dict[str, str]] = []
        for sound in self.config.get("sounds", []):
            if isinstance(sound, dict):
                path_text = str(sound.get("path", "")).strip()
                if not path_text:
                    continue
                name = str(sound.get("name") or Path(path_text).stem).strip()
            else:
                path_text = str(sound).strip()
                if not path_text:
                    continue
                name = Path(path_text).stem

            entries.append({"name": name, "path": path_text})

        return entries

    def _save_sound_entries(self, entries: list[dict[str, str]]) -> None:
        self.config["sounds"] = entries
        self._save_config()

    def _append_sound_row(self, path: Path, name: Optional[str] = None) -> None:
        row = self.table.rowCount()
        self.table.insertRow(row)
        self.table.setItem(row, 0, QTableWidgetItem(name or path.stem))
        self.table.setItem(row, 1, QTableWidgetItem(str(path)))

    def _apply_saved_volumes(self) -> None:
        monitor_volume = int(self.config.get("monitor_volume", 85))
        injection_volume = int(self.config.get("injection_volume", 85))
        mic_volume = int(self.config.get("mic_volume", 85))
        external_audio_volume = int(self.config.get("external_audio_volume", 85))

        self.monitor_slider.blockSignals(True)
        self.injection_slider.blockSignals(True)
        self.mic_slider.blockSignals(True)
        self.external_audio_slider.blockSignals(True)
        try:
            self.monitor_slider.setValue(monitor_volume)
            self.injection_slider.setValue(injection_volume)
            self.mic_slider.setValue(mic_volume)
            self.external_audio_slider.setValue(external_audio_volume)
        finally:
            self.monitor_slider.blockSignals(False)
            self.injection_slider.blockSignals(False)
            self.mic_slider.blockSignals(False)
            self.external_audio_slider.blockSignals(False)

        self._volume_changed()
        self._apply_mic_passthrough(show_errors=False)

    def add_sounds(self) -> None:
        files, _selected_filter = QFileDialog.getOpenFileNames(
            self,
            "Add sound files",
            str(Path.home()),
            AUDIO_FILTER,
        )
        if not files:
            return

        entries = self._sound_entries()
        known = {str(Path(sound["path"])) for sound in entries}
        added = 0

        for file_name in files:
            path = Path(file_name)
            path_text = str(path)
            if path_text in known:
                continue

            entries.append({"name": path.stem, "path": path_text})
            known.add(path_text)
            self._append_sound_row(path)
            added += 1

        self._save_sound_entries(entries)
        self._filter_table(self.search_input.text())
        if added:
            self._select_row(max(0, self.table.rowCount() - added))
        else:
            self._select_first_visible_sound()
        self.status_label.setText(f"Added {added} sound file(s)")

    def rename_selected_sound(self) -> None:
        row = self._selected_source_row()
        if row is None:
            QMessageBox.information(self, "Rename Sound", "Select a sound first.")
            return

        name_item = self.table.item(row, 0)
        if name_item is None:
            return

        new_name, accepted = QInputDialog.getText(
            self,
            "Rename Sound",
            "Sound name:",
            text=name_item.text(),
        )
        new_name = new_name.strip()
        if not accepted or not new_name:
            return

        entries = self._sound_entries()
        if row >= len(entries):
            return

        entries[row]["name"] = new_name
        name_item.setText(new_name)
        self._save_sound_entries(entries)
        self._filter_table(self.search_input.text())
        self.status_label.setText(f"Renamed: {new_name}")

    def change_selected_sound_path(self) -> None:
        row = self._selected_source_row()
        if row is None:
            QMessageBox.information(self, "Change Path", "Select a sound first.")
            return

        entries = self._sound_entries()
        if row >= len(entries):
            return

        old_path = Path(entries[row]["path"])
        file_name, _selected_filter = QFileDialog.getOpenFileName(
            self,
            "Choose replacement sound file",
            str(old_path.parent if old_path.parent.exists() else Path.home()),
            AUDIO_FILTER,
        )
        if not file_name:
            return

        new_path = Path(file_name)
        new_path_text = str(new_path)
        duplicate = any(
            index != row and str(Path(sound["path"])) == new_path_text
            for index, sound in enumerate(entries)
        )
        if duplicate:
            QMessageBox.warning(self, "Change Path", "That sound file is already in the library.")
            return

        entries[row]["path"] = new_path_text
        path_item = self.table.item(row, 1)
        if path_item:
            path_item.setText(new_path_text)

        self._save_sound_entries(entries)
        self._filter_table(self.search_input.text())
        self._sound_selection_changed()
        self.status_label.setText(f"Path updated: {new_path.name}")

    def delete_selected_sound(self) -> None:
        row = self._selected_source_row()
        if row is None:
            QMessageBox.information(self, "Delete Sound", "Select a sound first.")
            return

        name_item = self.table.item(row, 0)
        sound_name = name_item.text() if name_item else "selected sound"
        confirm = QMessageBox.question(
            self,
            "Delete Sound",
            f"Remove '{sound_name}' from the library?\n\nThe audio file itself will not be deleted.",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if confirm != QMessageBox.Yes:
            return

        entries = self._sound_entries()
        if row < len(entries):
            removed = entries.pop(row)
            if self._current_sound_path == Path(removed["path"]):
                self.stop_playback()
                self._reset_review_slider()

        self.table.removeRow(row)
        self._save_sound_entries(entries)
        self._filter_table(self.search_input.text())
        self.status_label.setText(f"Deleted: {sound_name}")

    def play_selected(self) -> None:
        path = self._selected_sound_path(show_message=True)
        if path is None:
            return

        start_ms = 0
        if self._current_sound_path == path:
            start_ms = self.review_slider.value()
            if self._current_duration_ms and start_ms >= self._current_duration_ms:
                start_ms = 0

        self._play_path(path, start_ms=start_ms)

    def _play_path(self, path: Path, *, start_ms: int = 0) -> None:
        if not path.exists():
            QMessageBox.warning(self, "Missing File", f"File not found:\n{path}")
            return

        monitor_device = self.monitor_combo.currentData()
        injection_device = self.injection_combo.currentData()
        if monitor_device is None or injection_device is None:
            QMessageBox.warning(
                self,
                "Audio Routing",
                "Select both monitor and injection devices before playing.",
            )
            return

        mic_device = self.mic_combo.currentData()
        if self.mic_passthrough_check.isChecked() and mic_device is None:
            QMessageBox.warning(
                self,
                "Mic Mixing",
                "Select your real microphone as the Mic Input before playing.",
            )
            return

        external_audio_device = self.external_audio_combo.currentData()
        if self.external_audio_check.isChecked() and external_audio_device is None:
            QMessageBox.warning(
                self,
                "Device/App Audio",
                "Select a Device/App Audio Source or turn that mix off.",
            )
            return

        try:
            duration_ms = self._duration_ms_for_path(path)
            if duration_ms > 0:
                start_ms = max(0, min(start_ms, duration_ms - 1))
            else:
                start_ms = 0

            self.audio.set_devices(
                monitor_device=monitor_device,
                injection_device=injection_device,
                mic_device=mic_device,
                external_audio_device=external_audio_device,
            )
            self.audio.set_volumes(
                monitor=self.monitor_slider.value() / 100.0,
                injection=self.injection_slider.value() / 100.0,
                mic=self.mic_slider.value() / 100.0,
                external_audio=self.external_audio_slider.value() / 100.0,
            )
            self._apply_mic_passthrough(show_errors=True)
            self.audio.play_file(
                path,
                start_seconds=start_ms / 1000.0,
                on_finished=self._emit_finished,
            )
            self._start_review_progress(path, duration_ms, start_ms)
            video_state = "video mix on" if self.external_audio_check.isChecked() else "video mix off"
            self.status_label.setText(f"Playing sound: {path.name} ({video_state})")
        except Exception as exc:
            if self.external_audio_check.isChecked() and self._is_invalid_external_source_error(exc):
                self._disable_external_audio_mix()
                self.status_label.setText("Device audio disabled; retrying sound")
                self._play_path(path, start_ms=start_ms)
                return

            QMessageBox.critical(self, "Playback Error", str(exc))

    def _is_invalid_external_source_error(self, error: Exception) -> bool:
        message = str(error).lower()
        return (
            "inputstream" in message
            or "external_audio_device" in message
            or "invalid device" in message
            or "paerrorcode -9996" in message
        )

    def _disable_external_audio_mix(self) -> None:
        self.external_audio_check.blockSignals(True)
        try:
            self.external_audio_check.setChecked(False)
        finally:
            self.external_audio_check.blockSignals(False)

        self.config["external_audio_enabled"] = False
        self.audio.set_external_audio_enabled(False)
        self._save_config()

    def stop_playback(self) -> None:
        self._set_review_position(self._current_review_position_ms())
        self.playback_timer.stop()
        self._ignore_next_stopped = self.audio.is_playing
        self.audio.stop()
        self.status_label.setText("Stopped")

    def _emit_finished(self, status: str, error: Optional[BaseException]) -> None:
        self.playback_finished.emit(status, error)

    def _playback_finished(self, status: str, error: object) -> None:
        if status == "stopped" and self._ignore_next_stopped:
            self._ignore_next_stopped = False
            return

        self.playback_timer.stop()
        self._ignore_next_stopped = False
        if error:
            self.status_label.setText(f"Playback {status}: {error}")
            return

        if status == "finished" and self._current_duration_ms:
            self._set_review_position(self._current_duration_ms)

        self.status_label.setText(f"Playback {status}")

    def _selected_sound_path(self, *, show_message: bool = False) -> Optional[Path]:
        row = self._selected_source_row()
        if row is None:
            row = self._select_first_visible_sound()

        if row is None:
            if show_message:
                self.status_label.setText("Add or select a sound first")
            return None

        path_item = self.table.item(row, 1)
        if path_item is None:
            return None

        return Path(path_item.text())

    def _sound_selection_changed(self) -> None:
        if self.audio.is_playing:
            return

        path = self._selected_sound_path()
        if path is None:
            self._reset_review_slider()
            return

        try:
            duration_ms = self._duration_ms_for_path(path)
        except Exception:
            duration_ms = 0

        self._current_sound_path = path
        self._current_duration_ms = duration_ms
        self.review_slider.setEnabled(duration_ms > 0)
        self.review_slider.setRange(0, max(0, duration_ms))
        self._set_review_position(0)

    def _duration_ms_for_path(self, path: Path) -> int:
        return max(0, int(AudioHandler.get_file_duration_seconds(path) * 1000))

    def _start_review_progress(
        self,
        path: Path,
        duration_ms: int,
        start_ms: int,
    ) -> None:
        self._current_sound_path = path
        self._current_duration_ms = duration_ms
        self._playback_start_offset_ms = start_ms
        self._playback_started_at = time.monotonic()
        self.review_slider.setEnabled(duration_ms > 0)
        self.review_slider.setRange(0, max(0, duration_ms))
        self._set_review_position(start_ms)
        self.playback_timer.start(100)

    def _update_review_progress(self) -> None:
        if self._is_review_seeking:
            return

        position_ms = self._current_review_position_ms()
        self._set_review_position(position_ms)
        if self._current_duration_ms and position_ms >= self._current_duration_ms:
            self.playback_timer.stop()

    def _current_review_position_ms(self) -> int:
        if not self._current_duration_ms:
            return 0

        if not self.playback_timer.isActive():
            return min(self.review_slider.value(), self._current_duration_ms)

        elapsed_ms = int((time.monotonic() - self._playback_started_at) * 1000)
        return min(self._playback_start_offset_ms + elapsed_ms, self._current_duration_ms)

    def _review_slider_pressed(self) -> None:
        self._is_review_seeking = True

    def _review_slider_moved(self, value: int) -> None:
        self._set_review_position(value)

    def _review_slider_released(self) -> None:
        self._is_review_seeking = False
        if self._current_sound_path is None:
            return

        target_ms = self.review_slider.value()
        self._set_review_position(target_ms)
        if self.audio.is_playing:
            self._ignore_next_stopped = True
            self.audio.stop()
            self._play_path(self._current_sound_path, start_ms=target_ms)

    def _set_review_position(self, position_ms: int) -> None:
        position_ms = max(0, min(position_ms, self._current_duration_ms))
        self.review_slider.blockSignals(True)
        try:
            self.review_slider.setValue(position_ms)
        finally:
            self.review_slider.blockSignals(False)

        self.review_time_label.setText(
            f"{self._format_duration(position_ms)} / "
            f"{self._format_duration(self._current_duration_ms)}"
        )

    def _reset_review_slider(self) -> None:
        self._current_sound_path = None
        self._current_duration_ms = 0
        self._playback_start_offset_ms = 0
        self._playback_started_at = 0
        self.playback_timer.stop()
        self.review_slider.setEnabled(False)
        self.review_slider.setRange(0, 0)
        self._set_review_position(0)

    def _format_duration(self, milliseconds: int) -> str:
        total_seconds = max(0, int(milliseconds / 1000))
        minutes, seconds = divmod(total_seconds, 60)
        hours, minutes = divmod(minutes, 60)
        if hours:
            return f"{hours}:{minutes:02d}:{seconds:02d}"
        return f"{minutes:02d}:{seconds:02d}"

    def _selected_source_row(self) -> Optional[int]:
        ranges = self.table.selectedRanges()
        if not ranges:
            return None
        return ranges[0].topRow()

    def _select_row(self, row: int) -> None:
        if row < 0 or row >= self.table.rowCount():
            return

        self.table.selectRow(row)
        self.table.scrollToItem(self.table.item(row, 0))

    def _select_first_visible_sound(self) -> Optional[int]:
        for row in range(self.table.rowCount()):
            if not self.table.isRowHidden(row):
                self._select_row(row)
                return row

        self.table.clearSelection()
        self._reset_review_slider()
        return None

    def _filter_table(self, text: str) -> None:
        needle = text.strip().lower()

        for row in range(self.table.rowCount()):
            name_item = self.table.item(row, 0)
            path_item = self.table.item(row, 1)
            haystack = " ".join(
                item.text().lower()
                for item in (name_item, path_item)
                if item is not None
            )
            self.table.setRowHidden(row, bool(needle and needle not in haystack))

        selected_row = self._selected_source_row()
        if selected_row is None or self.table.isRowHidden(selected_row):
            self._select_first_visible_sound()

    def _device_selection_changed(self) -> None:
        self.config["monitor_device"] = self.monitor_combo.currentData()
        self.config["injection_device"] = self.injection_combo.currentData()
        self.config["mic_device"] = self.mic_combo.currentData()
        self.config["external_audio_device"] = self.external_audio_combo.currentData()
        self.audio.set_devices(
            monitor_device=self.config["monitor_device"],
            injection_device=self.config["injection_device"],
            mic_device=self.config["mic_device"],
            external_audio_device=self.config["external_audio_device"],
        )
        self._save_config()
        self._apply_mic_passthrough(show_errors=False)

    def _volume_changed(self) -> None:
        self.config["monitor_volume"] = self.monitor_slider.value()
        self.config["injection_volume"] = self.injection_slider.value()
        self.config["mic_volume"] = self.mic_slider.value()
        self.config["external_audio_volume"] = self.external_audio_slider.value()
        self.monitor_value_label.setText(f"{self.monitor_slider.value()}%")
        self.injection_value_label.setText(f"{self.injection_slider.value()}%")
        self.mic_value_label.setText(f"{self.mic_slider.value()}%")
        self.external_audio_value_label.setText(
            f"{self.external_audio_slider.value()}%"
        )
        self.audio.set_volumes(
            monitor=self.monitor_slider.value() / 100.0,
            injection=self.injection_slider.value() / 100.0,
            mic=self.mic_slider.value() / 100.0,
            external_audio=self.external_audio_slider.value() / 100.0,
        )
        self._save_config()

    def _mic_passthrough_changed(self, enabled: bool) -> None:
        self.config["mic_passthrough_enabled"] = enabled
        self.audio.set_mic_passthrough_enabled(enabled)
        self._save_config()
        self._apply_mic_passthrough(show_errors=enabled)

    def _external_audio_changed(self, enabled: bool) -> None:
        if enabled and not self._selected_external_source_is_usable():
            self._disable_external_audio_mix()
            enabled = False

        self.config["external_audio_enabled"] = enabled
        self.audio.set_external_audio_enabled(enabled)
        self._save_config()
        self._apply_mic_passthrough(show_errors=enabled)

    def _selected_external_source_is_usable(self) -> bool:
        external_audio_device = self.external_audio_combo.currentData()
        if external_audio_device is None:
            return True

        try:
            AudioHandler.test_input_device_open(external_audio_device)
            return True
        except Exception as exc:
            self.status_label.setText("Device audio unavailable")
            QMessageBox.warning(
                self,
                "Device/App Audio",
                "This Device/App Audio Source is visible in Windows, but LineCast "
                "cannot open it as a live capture source.\n\n"
                "Choose a WASAPI loopback source such as Speakers (Realtek) "
                "instead of Stereo Mix. The device/app audio mix was turned off "
                "so normal mic and soundboard playback can still work.\n\n"
                f"Original error:\n{exc}",
            )
            return False

    def _advanced_devices_changed(self, enabled: bool) -> None:
        self.config["show_advanced_devices"] = enabled
        self._save_config()
        self._load_devices()

    def _enable_startup_mic_passthrough(self) -> None:
        self._defer_mic_start = False
        self._apply_mic_passthrough(show_errors=False)

    def _apply_mic_passthrough(self, *, show_errors: bool) -> None:
        if self._defer_mic_start and not show_errors:
            return

        mic_enabled = self.mic_passthrough_check.isChecked()
        external_enabled = self.external_audio_check.isChecked()
        if not mic_enabled and not external_enabled:
            self.audio.stop_mic_passthrough()
            if self.status_label.text() in {"Mic mix active", "Video mix active", "Mic + video mix active"}:
                self.status_label.setText("Ready")
            return

        if self.injection_combo.currentData() is None:
            if show_errors:
                QMessageBox.information(
                    self,
                    "Audio Routing",
                    "Select an Injection Device first.",
                )
            return

        if mic_enabled and self.mic_combo.currentData() is None:
            if show_errors:
                QMessageBox.information(
                    self,
                    "Mic Mixing",
                    "Select your real microphone as the Mic Input first.",
                )
            return

        if external_enabled and self.external_audio_combo.currentData() is None:
            if show_errors:
                QMessageBox.information(
                    self,
                    "Device/App Audio",
                    "Select a Device/App Audio Source or turn that mix off.",
                )
            return

        try:
            self.audio.start_mic_passthrough()
            if self.status_label.text() in {"Ready", "Stopped", "Mic mix active", "Video mix active", "Mic + video mix active"}:
                if mic_enabled and external_enabled:
                    self.status_label.setText("Mic + video mix active")
                elif external_enabled:
                    self.status_label.setText("Video mix active")
                else:
                    self.status_label.setText("Mic mix active")
        except Exception as exc:
            self.audio.stop_mic_passthrough()
            if show_errors:
                QMessageBox.critical(self, "Audio Routing Error", str(exc))

    def closeEvent(self, event: Any) -> None:
        self.playback_timer.stop()
        self.audio.stop()
        self.audio.stop_mic_passthrough()
        self._save_config()
        super().closeEvent(event)

    def _load_config(self) -> dict[str, Any]:
        path = CONFIG_PATH
        if not path.exists() and LEGACY_CONFIG_PATH != CONFIG_PATH:
            path = LEGACY_CONFIG_PATH

        if not path.exists():
            return dict(DEFAULT_CONFIG)

        try:
            with path.open("r", encoding="utf-8") as file:
                data = json.load(file)
        except (OSError, json.JSONDecodeError):
            return dict(DEFAULT_CONFIG)

        config = dict(DEFAULT_CONFIG)
        config.update(data)
        return config

    def _save_config(self) -> None:
        CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with CONFIG_PATH.open("w", encoding="utf-8") as file:
            json.dump(self.config, file, indent=2)


def main() -> int:
    _set_windows_app_id()

    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    app.setWindowIcon(QIcon(str(APP_ICON_PATH)))
    app.setStyle("Fusion")
    app.setStyleSheet(APP_STYLESHEET)

    window = SoundPadWindow()
    window.show()

    return app.exec_()


def _set_windows_app_id() -> None:
    if sys.platform != "win32":
        return

    try:
        import ctypes

        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(APP_ID)
    except Exception:
        pass


if __name__ == "__main__":
    raise SystemExit(main())


