# Wodra icon assets

`images/Golden Helmet Tortoiseshell Cat Icon.png` is the approved application artwork. Keep it unchanged. `images/Cobalt Helmet Tortoiseshell Cycling Icon.png` is also retained as original artwork for potential future use. The generator clears only disconnected alpha components outside the main rounded-square artwork in derived outputs, then resizes with Lanczos filtering.

Regenerate the committed assets from the repository root:

```powershell
py -m venv .venv-branding
.\.venv-branding\Scripts\python.exe -m pip install -r tools\branding\requirements.txt
.\.venv-branding\Scripts\python.exe tools\branding\generate_icons.py
```

The script writes transparent PNG variants at 16, 24, 32, 48, 64, 128, 256, 512, and 1024 pixels and `wodra.ico` containing 16, 24, 32, 48, 64, 128, and 256 pixel images. Godot uses the 512 pixel PNG for the project/window and Windows export; Velopack uses the multi-resolution ICO for its installer and Start menu/Desktop shortcuts. The publisher explicitly declares both shortcut locations so the update package contains the expected shortcut metadata. The publisher includes the 512 pixel PNG and ICO in the portable and Velopack app packages. Package identity and the existing updater channel stay unchanged.
