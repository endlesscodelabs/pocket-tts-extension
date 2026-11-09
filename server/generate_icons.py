"""
generate_icons.py
─────────────────
Reads logo_master.png from the project root and exports resized icons
to ../icons/ at sizes 16, 32, 48, and 128 pixels.

Usage:
  python generate_icons.py
"""

from pathlib import Path
from PIL import Image

SIZES = [16, 32, 48, 128]

# Paths relative to this script's location (server/)
SCRIPT_DIR  = Path(__file__).parent
ROOT_DIR    = SCRIPT_DIR.parent
SOURCE_IMG = ROOT_DIR / "logo_new.png"
if not SOURCE_IMG.exists():
    SOURCE_IMG = ROOT_DIR / "logo_master.png"
ICONS_DIR   = ROOT_DIR / "icons"


def main():
    if not SOURCE_IMG.exists():
        raise FileNotFoundError(
            f"Source image not found: {SOURCE_IMG}\n"
            "Place your logo_master.png in the project root."
        )

    ICONS_DIR.mkdir(parents=True, exist_ok=True)
    print(f"Reading source: {SOURCE_IMG}")

    with Image.open(SOURCE_IMG) as img:
        # Convert to RGBA to preserve transparency
        img = img.convert("RGBA")

        for size in SIZES:
            resized = img.resize((size, size), Image.Resampling.LANCZOS)
            out_path = ICONS_DIR / f"icon-{size}.png"
            resized.save(out_path, format="PNG")
            print(f"  -> Saved {out_path.name} ({size}x{size})")

    print(f"\nDone! {len(SIZES)} icons written to: {ICONS_DIR}")


if __name__ == "__main__":
    main()
