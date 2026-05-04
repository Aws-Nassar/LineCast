from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Optional

from PyQt5.QtCore import Qt, pyqtSignal
from PyQt5.QtWidgets import (
    QApplication,
    QComboBox,
    QFileDialog,
    QFrame,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QSlider,
    QStyle,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from audio_handler import AudioDevice, AudioHandler


CONFIG_PATH = Path(__file__).with_name("config.json")
AUDIO_FILTER = "Audio files (*.mp3 *.wav);;All files (*.*)"


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
    "monitor_volume": 85,
    "injection_volume": 85,
    "sounds": [],
}


class SoundPadWindow(QMainWindow):
    playback_finished = pyqtSignal(str, object)

    def __init__(self) -> None:
        super().__init__()

        self.config = self._load_config()
        self.devices: list[AudioDevice] = []
        self.audio = AudioHandler(
            monitor_device=self.config.get("monitor_device"),
            injection_device=self.config.get("injection_device"),
        )

        self.setWindowTitle("SoundPad Clone")
        self.resize(980, 620)

        self.monitor_combo = QComboBox()
        self.injection_combo = QComboBox()
        self.monitor_slider = QSlider(Qt.Horizontal)
        self.injection_slider = QSlider(Qt.Horizontal)
        self.search_input = QLineEdit()
        self.table = QTableWidget(0, 2)
        self.add_button = QPushButton("Add Sounds")
        self.play_button = QPushButton("Play")
        self.stop_button = QPushButton("Stop")
        self.status_label = QLabel("Ready")
        self.monitor_value_label = QLabel("85%")
        self.injection_value_label = QLabel("85%")

        self._build_ui()
        self._connect_signals()
        self._load_devices()
        self._load_sound_table()
        self._apply_saved_volumes()

    def _build_ui(self) -> None:
        central = QWidget()
        central.setObjectName("appRoot")
        root = QVBoxLayout(central)
        root.setContentsMargins(20, 18, 20, 18)
        root.setSpacing(14)

        top_bar = QFrame()
        top_bar.setObjectName("topBar")
        top_layout = QHBoxLayout(top_bar)
        top_layout.setContentsMargins(16, 13, 16, 13)

        title_block = QVBoxLayout()
        title_block.setSpacing(1)
        title = QLabel("SoundPad Clone")
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
        device_layout.setVerticalSpacing(11)
        device_layout.setColumnStretch(1, 1)
        device_layout.setColumnStretch(4, 1)

        device_layout.addWidget(self._field_label("Monitor Device"), 0, 0)
        device_layout.addWidget(self.monitor_combo, 0, 1)
        device_layout.addWidget(self._field_label("Injection Device"), 0, 3)
        device_layout.addWidget(self.injection_combo, 0, 4)

        self._setup_slider(self.monitor_slider)
        self._setup_slider(self.injection_slider)
        self.monitor_value_label.setObjectName("volumeValue")
        self.injection_value_label.setObjectName("volumeValue")

        device_layout.addWidget(self._field_label("Monitor Volume"), 1, 0)
        device_layout.addWidget(self.monitor_slider, 1, 1)
        device_layout.addWidget(self.monitor_value_label, 1, 2)
        device_layout.addWidget(self._field_label("Injection Volume"), 1, 3)
        device_layout.addWidget(self.injection_slider, 1, 4)
        device_layout.addWidget(self.injection_value_label, 1, 5)

        root.addWidget(device_group)

        library_panel = QFrame()
        library_panel.setObjectName("libraryPanel")
        library_layout = QVBoxLayout(library_panel)
        library_layout.setContentsMargins(14, 14, 14, 14)
        library_layout.setSpacing(12)

        controls = QHBoxLayout()
        controls.setSpacing(10)
        self.search_input.setPlaceholderText("Search sounds")
        controls.addWidget(self.search_input, 1)
        controls.addWidget(self.add_button)
        controls.addWidget(self.play_button)
        controls.addWidget(self.stop_button)
        library_layout.addLayout(controls)

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
        self.table.verticalHeader().setDefaultSectionSize(42)
        library_layout.addWidget(self.table, 1)

        root.addWidget(library_panel, 1)

        self.setCentralWidget(central)
        self._apply_button_icons()

    def _connect_signals(self) -> None:
        self.add_button.clicked.connect(self.add_sounds)
        self.play_button.clicked.connect(self.play_selected)
        self.stop_button.clicked.connect(self.stop_playback)
        self.search_input.textChanged.connect(self._filter_table)
        self.table.doubleClicked.connect(self.play_selected)
        self.monitor_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.injection_combo.currentIndexChanged.connect(self._device_selection_changed)
        self.monitor_slider.valueChanged.connect(self._volume_changed)
        self.injection_slider.valueChanged.connect(self._volume_changed)
        self.playback_finished.connect(self._playback_finished)

    def _setup_slider(self, slider: QSlider) -> None:
        slider.setRange(0, 100)
        slider.setSingleStep(1)
        slider.setPageStep(5)

    def _field_label(self, text: str) -> QLabel:
        label = QLabel(text)
        label.setObjectName("fieldLabel")
        return label

    def _apply_button_icons(self) -> None:
        style = self.style()
        self.add_button.setIcon(style.standardIcon(QStyle.SP_FileDialogNewFolder))
        self.play_button.setIcon(style.standardIcon(QStyle.SP_MediaPlay))
        self.stop_button.setIcon(style.standardIcon(QStyle.SP_MediaStop))
        self.play_button.setObjectName("primaryButton")
        self.stop_button.setObjectName("dangerButton")

        for button in (self.add_button, self.play_button, self.stop_button):
            button.setCursor(Qt.PointingHandCursor)

    def _load_devices(self) -> None:
        try:
            self.devices = AudioHandler.list_output_devices()
        except Exception as exc:
            QMessageBox.critical(self, "Audio Devices", str(exc))
            self.devices = []

        self.monitor_combo.clear()
        self.injection_combo.clear()

        for combo in (self.monitor_combo, self.injection_combo):
            combo.addItem("Select device", None)
            for device in self.devices:
                label = f"{device.index}: {device.name} [{device.hostapi}]"
                combo.addItem(label, device.index)

        self._select_combo_device(
            self.monitor_combo,
            self.config.get("monitor_device"),
        )
        self._select_combo_device(
            self.injection_combo,
            self.config.get("injection_device"),
        )
        self._device_selection_changed()

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

        for sound_path in self.config.get("sounds", []):
            self._append_sound_row(Path(sound_path))

        self._filter_table(self.search_input.text())

    def _append_sound_row(self, path: Path) -> None:
        row = self.table.rowCount()
        self.table.insertRow(row)
        self.table.setItem(row, 0, QTableWidgetItem(path.stem))
        self.table.setItem(row, 1, QTableWidgetItem(str(path)))

    def _apply_saved_volumes(self) -> None:
        monitor_volume = int(self.config.get("monitor_volume", 85))
        injection_volume = int(self.config.get("injection_volume", 85))

        self.monitor_slider.blockSignals(True)
        self.injection_slider.blockSignals(True)
        try:
            self.monitor_slider.setValue(monitor_volume)
            self.injection_slider.setValue(injection_volume)
        finally:
            self.monitor_slider.blockSignals(False)
            self.injection_slider.blockSignals(False)

        self._volume_changed()

    def add_sounds(self) -> None:
        files, _selected_filter = QFileDialog.getOpenFileNames(
            self,
            "Add sound files",
            str(Path.home()),
            AUDIO_FILTER,
        )
        if not files:
            return

        known = {str(Path(path)) for path in self.config.get("sounds", [])}
        added = 0

        for file_name in files:
            path = Path(file_name)
            path_text = str(path)
            if path_text in known:
                continue

            self.config.setdefault("sounds", []).append(path_text)
            known.add(path_text)
            self._append_sound_row(path)
            added += 1

        self._save_config()
        self._filter_table(self.search_input.text())
        self.status_label.setText(f"Added {added} sound file(s)")

    def play_selected(self) -> None:
        row = self._selected_source_row()
        if row is None:
            QMessageBox.information(self, "Play Sound", "Select a sound first.")
            return

        path_item = self.table.item(row, 1)
        if path_item is None:
            return

        path = Path(path_item.text())
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

        try:
            self.audio.set_devices(
                monitor_device=monitor_device,
                injection_device=injection_device,
            )
            self.audio.set_volumes(
                monitor=self.monitor_slider.value() / 100.0,
                injection=self.injection_slider.value() / 100.0,
            )
            self.audio.play_file(path, on_finished=self._emit_finished)
            self.status_label.setText(f"Playing: {path.name}")
        except Exception as exc:
            QMessageBox.critical(self, "Playback Error", str(exc))

    def stop_playback(self) -> None:
        self.audio.stop()
        self.status_label.setText("Stopped")

    def _emit_finished(self, status: str, error: Optional[BaseException]) -> None:
        self.playback_finished.emit(status, error)

    def _playback_finished(self, status: str, error: object) -> None:
        if error:
            self.status_label.setText(f"Playback {status}: {error}")
            return

        self.status_label.setText(f"Playback {status}")

    def _selected_source_row(self) -> Optional[int]:
        ranges = self.table.selectedRanges()
        if not ranges:
            return None
        return ranges[0].topRow()

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

    def _device_selection_changed(self) -> None:
        self.config["monitor_device"] = self.monitor_combo.currentData()
        self.config["injection_device"] = self.injection_combo.currentData()
        self._save_config()

    def _volume_changed(self) -> None:
        self.config["monitor_volume"] = self.monitor_slider.value()
        self.config["injection_volume"] = self.injection_slider.value()
        self.monitor_value_label.setText(f"{self.monitor_slider.value()}%")
        self.injection_value_label.setText(f"{self.injection_slider.value()}%")
        self.audio.set_volumes(
            monitor=self.monitor_slider.value() / 100.0,
            injection=self.injection_slider.value() / 100.0,
        )
        self._save_config()

    def closeEvent(self, event: Any) -> None:
        self.audio.stop()
        self._save_config()
        super().closeEvent(event)

    def _load_config(self) -> dict[str, Any]:
        if not CONFIG_PATH.exists():
            return dict(DEFAULT_CONFIG)

        try:
            with CONFIG_PATH.open("r", encoding="utf-8") as file:
                data = json.load(file)
        except (OSError, json.JSONDecodeError):
            return dict(DEFAULT_CONFIG)

        config = dict(DEFAULT_CONFIG)
        config.update(data)
        return config

    def _save_config(self) -> None:
        with CONFIG_PATH.open("w", encoding="utf-8") as file:
            json.dump(self.config, file, indent=2)


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("SoundPad Clone")
    app.setStyle("Fusion")
    app.setStyleSheet(APP_STYLESHEET)

    window = SoundPadWindow()
    window.show()

    return app.exec_()


if __name__ == "__main__":
    raise SystemExit(main())
