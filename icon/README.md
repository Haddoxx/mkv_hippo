# Icon source

- `Hyppo_and_Thomas.jpg` — original low-resolution (280×246) cartoon: a hippo and a
  red bird companion.
- `hippo_head_source.png` — the derived 1024×1024 icon master: bird removed, body
  erased below the chin, square crop auto-centered on the head with ~4% margins,
  JPEG artifacts median-filtered, Lanczos-upscaled.

## Rebuilding the master (Pillow)

1. Paint the bird region `[12, 5, 94, 115]` with the flat background `(254, 229, 188)`.
2. Flood-fill the chest pocket below the chin (seed `(150, 150)`, threshold 60), then
   erase all rows from y=127 down, plus the leg-line tick at `[127, 121, 139, 127]`.
3. Compute the non-background bounding box, center it on a square canvas
   (side = max dimension + 8%), median-filter 3×3, resize to 1024×1024 with Lanczos.

## Building hippo.ico (ImageMagick)

```bash
convert hippo_head_source.png -resize 256x256 g256.png
for s in 64 48 40 32 24 20 16; do
  convert hippo_head_source.png -resize ${s}x${s} -unsharp 0x0.6+0.6+0 g$s.png
done
convert g16.png g20.png g24.png g32.png g40.png g48.png g64.png g256.png \
  ../src/MkvHippo.App/hippo.ico
```

Unlike the previous etching test, the bold flat artwork stays legible down to 16 px.
