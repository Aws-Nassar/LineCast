from __future__ import annotations

import struct
import sys
from pathlib import Path

from PyQt5.QtCore import QByteArray, QBuffer, QIODevice, QPointF, QRectF, Qt
from PyQt5.QtGui import (
    QColor,
    QGuiApplication,
    QImage,
    QLinearGradient,
    QPainter,
    QPainterPath,
    QPen,
    QPolygonF,
    QRadialGradient,
)


ROOT = Path(__file__).resolve().parents[1]
ASSETS_DIR = ROOT / "assets"
ICON_PATH = ASSETS_DIR / "linecast.ico"
PREVIEW_PATH = ASSETS_DIR / "linecast.png"
ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)


def draw_icon(size: int) -> QImage:
    image = QImage(size, size, QImage.Format_ARGB32)
    image.fill(Qt.transparent)

    painter = QPainter(image)
    painter.setRenderHint(QPainter.Antialiasing)

    margin = size * 0.075
    rect = QRectF(margin, margin, size - margin * 2, size - margin * 2)
    radius = size * 0.19

    shadow = QPainterPath()
    shadow.addRoundedRect(rect.adjusted(size * 0.02, size * 0.025, size * 0.02, size * 0.025), radius, radius)
    painter.fillPath(shadow, QColor(0, 0, 0, 80))

    bg = QLinearGradient(rect.topLeft(), rect.bottomRight())
    bg.setColorAt(0.0, QColor("#17202b"))
    bg.setColorAt(0.56, QColor("#0f141c"))
    bg.setColorAt(1.0, QColor("#071019"))
    bg_path = QPainterPath()
    bg_path.addRoundedRect(rect, radius, radius)
    painter.setBrush(bg)
    painter.setPen(QPen(QColor("#334155"), max(1, int(size * 0.018))))
    painter.drawRoundedRect(rect, radius, radius)

    glow = QRadialGradient(QPointF(size * 0.35, size * 0.28), size * 0.58)
    glow.setColorAt(0.0, QColor(14, 165, 233, 95))
    glow.setColorAt(1.0, QColor(14, 165, 233, 0))
    painter.save()
    painter.setClipPath(bg_path)
    painter.fillRect(image.rect(), glow)
    painter.restore()

    play = QPolygonF(
        [
            QPointF(size * 0.245, size * 0.305),
            QPointF(size * 0.245, size * 0.695),
            QPointF(size * 0.505, size * 0.5),
        ]
    )
    painter.setPen(Qt.NoPen)
    painter.setBrush(QColor("#22c55e"))
    painter.drawPolygon(play)

    painter.setPen(QPen(QColor(220, 252, 231, 150), max(1, int(size * 0.018))))
    painter.drawLine(QPointF(size * 0.285, size * 0.38), QPointF(size * 0.285, size * 0.62))

    bars = [
        (0.56, 0.17),
        (0.63, 0.30),
        (0.70, 0.46),
        (0.77, 0.31),
        (0.84, 0.19),
    ]
    bar_width = max(2, int(size * 0.035))
    painter.setPen(QPen(QColor("#e0f2fe"), bar_width, Qt.SolidLine, Qt.RoundCap))
    for x_ratio, height_ratio in bars:
        x = size * x_ratio
        half = size * height_ratio / 2
        painter.drawLine(QPointF(x, size * 0.5 - half), QPointF(x, size * 0.5 + half))

    painter.setPen(QPen(QColor("#38bdf8"), max(1, int(size * 0.024)), Qt.SolidLine, Qt.RoundCap))
    painter.drawArc(QRectF(size * 0.51, size * 0.24, size * 0.42, size * 0.52), -45 * 16, 90 * 16)
    painter.drawArc(QRectF(size * 0.45, size * 0.17, size * 0.54, size * 0.66), -42 * 16, 84 * 16)

    painter.end()
    return image


def image_to_png_bytes(image: QImage) -> bytes:
    data = QByteArray()
    buffer = QBuffer(data)
    buffer.open(QIODevice.WriteOnly)
    image.save(buffer, "PNG")
    return bytes(data)


def write_ico(images: list[tuple[int, bytes]], path: Path) -> None:
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = bytearray()
    payload = bytearray()

    for size, png_data in images:
        width = 0 if size >= 256 else size
        height = 0 if size >= 256 else size
        entries.extend(
            struct.pack(
                "<BBBBHHII",
                width,
                height,
                0,
                0,
                1,
                32,
                len(png_data),
                offset,
            )
        )
        payload.extend(png_data)
        offset += len(png_data)

    path.write_bytes(header + bytes(entries) + bytes(payload))


def main() -> int:
    QGuiApplication(sys.argv)
    ASSETS_DIR.mkdir(exist_ok=True)

    images = [(size, image_to_png_bytes(draw_icon(size))) for size in ICON_SIZES]
    write_ico(images, ICON_PATH)
    draw_icon(512).save(str(PREVIEW_PATH), "PNG")

    print(f"Wrote {ICON_PATH.relative_to(ROOT)}")
    print(f"Wrote {PREVIEW_PATH.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
