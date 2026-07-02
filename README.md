# Stride Procedural Sky & Day/Night

A fully procedural sky for [Stride](https://www.stride3d.net/) — no textures, no cubemaps.
Attach one component and get a camera-following sky dome with a zenith→horizon gradient,
a sun disc + halo, a textured moon with phases, drifting multi-layer clouds, twinkling
stars and a sunset / anti-twilight (Belt of Venus) glow — plus a built-in day/night clock
that rotates the sun and moon and drives your directional light.

## What's in the box

| File | Role |
|------|------|
| `ProceduralSky` | Drop-in `SyncScript`. Spawns the dome, runs the day/night clock, drives the sun light, pushes every shader parameter. |
| `Effects/SkyProcedural.sdsl` | The sky shader: gradient, sun, moon (maria + craters + phase), 3-layer parallax clouds, stars, twilight glow. |

## Quick start

1. Reference `StrideSkySystem` from your game project.
2. Add an empty entity and attach a **ProceduralSky** component (category *Sky*).
3. Set `Camera` to your scene camera and (optionally) `SunLight` to a directional light.
4. Tweak `TimeOfDay`, `DayLengthSeconds`, `CloudCoverage`, `StarBrightness` — press play.

Set `DayLengthSeconds = 0` to freeze time at a fixed `TimeOfDay` (e.g. a permanent golden
hour). Leave `SunLight` empty if you drive your own lighting.

## How it works

- **Camera-following dome.** The component builds an inverted sphere, keeps it centred on
  the camera and, each frame, feeds the shader the sun/moon directions, a `NightFactor`, the
  sun colour and the sky palette. The dome is emissive-only, so its colour is pure procedural
  output unaffected by scene lights.
- **Day/night clock.** `TimeOfDay` advances at `24h / DayLengthSeconds`. The sun sweeps an
  east→up→west arc; the moon sits opposite. `NightFactor` (from the sun's altitude) blends the
  day and night palettes and reveals the stars; the sun colour shifts white→orange→red as it
  nears the horizon.
- **Matched lighting.** If a `SunLight` is assigned, its direction, colour and intensity are
  driven from the same sun so the ground lighting always agrees with the sky (bright noon,
  warm low sun, dark night).
- **All procedural.** Clouds are 2D fbm with cheap 2-tap self-shadowing; the moon has hashed
  maria/craters and phase shading; stars are a hashed grid with per-star twinkle. Zero assets.

## Demo

Open `StrideSkySystem.sln`, set **Demo.Windows** as startup and run. A full day passes every
90 s over a lit ground plane — watch the sunrise, midday blue, sunset glow, and the starry
night with a moon. Fly with WASD + right-mouse.

## License

MIT. See [LICENSE.md](LICENSE.md).
