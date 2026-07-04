using System;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using StrideSkySystem;

namespace Demo.Windows
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var game = new Game())
            {
                // Scripted showcase-video camera + timelapse (RECORD_VARIANT=1|2|3). No effect otherwise.
                if (int.TryParse(Environment.GetEnvironmentVariable("RECORD_VARIANT"), out var variant))
                {
                    game.WindowCreated += (_, _) => game.Window.Title = "StrideDemoRecord";
                    game.Script.AddTask(() => DriveAsync(game, variant));
                }

                game.Run();
            }
        }

        private static async Task DriveAsync(Game game, int variant)
        {
            Entity? camera = null;
            ProceduralSky? sky = null;
            while (camera is null || sky is null)
            {
                await game.Script.NextFrame();
                var scene = game.SceneSystem.SceneInstance?.RootScene;
                if (scene is null)
                {
                    continue;
                }

                foreach (var entity in scene.Entities)
                {
                    if (entity.Get<CameraComponent>() is not null) camera = entity;
                    sky ??= entity.Get<ProceduralSky>();
                }
            }

            if (camera.Get<BasicCameraController>() is { } controller)
            {
                camera.Remove(controller);
            }

            // (start hour, hours per second) per variant; time is driven manually so the cycle
            // starts exactly when the screen capture does (after the shader warmup).
            var (startHour, hoursPerSecond) = variant switch
            {
                1 => (5.5f, 1.7f),   // full day: dawn → dusk (panning)
                2 => (16.4f, 0.35f), // sun sinks through the frame → dusk stars
                _ => (5.0f, 0.35f),  // pre-dawn → sun climbs through the frame
            };
            const float warmupSeconds = 14f;

            sky.DayLengthSeconds = 0f; // frozen — we own TimeOfDay
            camera.Transform.Position = new Vector3(0f, 2f, 0f);

            while (game.IsRunning)
            {
                var total = (float)game.UpdateTime.Total.TotalSeconds;
                var t = MathF.Max(0f, total - warmupSeconds);
                sky.TimeOfDay = (startHour + t * hoursPerSecond) % 24f;

                Vector3 dir;
                if (variant == 1)
                {
                    // Slow horizon pan, tilted a bit up so sun path, clouds and stars all pass through.
                    var yaw = 0.6f + 0.05f * t;
                    dir = Vector3.Normalize(new Vector3(MathF.Sin(yaw), 0.28f, -MathF.Cos(yaw)));
                }
                else
                {
                    // Fixed on the azimuth where the sun crosses the horizon (18h/6h on the
                    // ProceduralSky arc): the sun rises/sets THROUGH the frame instead of being
                    // tracked dead-center.
                    var horizonX = variant == 2 ? -1f : 1f;
                    dir = Vector3.Normalize(new Vector3(horizonX, 0.14f, 0.15f));
                }

                camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(
                    MathF.Atan2(-dir.X, -dir.Z), MathF.Asin(dir.Y), 0f);

                await game.Script.NextFrame();
            }
        }
    }
}
