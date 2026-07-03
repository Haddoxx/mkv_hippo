# Icon source

`The_face_of_a_bearded_man_expressing_anger._Etching_in_the_c_Wellcome_V0009342.png`
— *The face of a bearded man expressing anger. Etching in the crayon manner.*
Wellcome Collection reference V0009342, licensed CC BY 4.0.
<https://wellcomecollection.org/works>

`src/MkvHippo.App/hippo.ico` is generated from a tight square crop of the face
(1200×1200 at offset +450+400) with a contrast boost (`-level 12%,88%`), then
resized to 16/20/24/32/40/48/64/256 px frames; the frames at 64 px and below get
`-sigmoidal-contrast 4x50% -unsharp 0x0.75` so the drawing survives downscaling.
Rebuild with ImageMagick:

```bash
convert source.png -crop 1200x1200+450+400 +repage -level 12%,88% face.png
convert face.png -resize 256x256 f256.png
for s in 64 48 40 32 24 20 16; do
  convert face.png -resize ${s}x${s} -sigmoidal-contrast 4x50% -unsharp 0x0.75 f$s.png
done
convert f16.png f20.png f24.png f32.png f40.png f48.png f64.png f256.png hippo.ico
```

Status: initial test artwork — the 16 px frame is illegible (fine etching lines
don't survive that size); a simplified hand-drawn glyph would be the proper
replacement for the small frames.
