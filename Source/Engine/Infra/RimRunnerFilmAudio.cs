using System.IO;
using System.Text.Json;
using Waylith.Audio;
using Waylith.Audio.MiniAudio;

namespace RimRunner;

internal static class RimRunnerFilmAudio
{
    private static string? directory;
    private static float frame0;
    private static bool armed;
    private static readonly List<RimRunnerFilmVoice> voices = [];
    private static readonly List<float[]> listener = [];
    private static readonly Dictionary<IAudioSound, AudioClip> clips = new(ReferenceEqualityComparer.Instance);

    internal static bool Recording => directory != null;

    internal static void Begin(string? audioDir)
    {
        Reset();
        directory = audioDir;
    }

    internal static void Reset()
    {
        directory = null;
        armed = false;
        voices.Clear();
        listener.Clear();
        clips.Clear();
    }

    internal static void MarkFirstFrame()
    {
        armed = RimRunnerFilmClock.TryPinnedRealtime(out frame0);
    }

    internal static void NoteVoice(RimRunnerFilmVoice voice)
    {
        if (Recording)
        {
            voices.Add(voice);
        }
    }

    internal static void NoteSound(AudioClip clip, IAudioSound sound)
    {
        if (Recording)
        {
            clips[sound] = clip;
        }
    }

    internal static void NoteListener(System.Numerics.Vector3 position, System.Numerics.Vector3 forward, System.Numerics.Vector3 up)
    {
        if (!Recording || !RimRunnerFilmClock.TryPinnedRealtime(out float now))
        {
            return;
        }

        float[] row = [now, position.X, position.Y, position.Z, forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z];
        if (listener.Count > 0 && listener[^1].AsSpan(1).SequenceEqual(row.AsSpan(1)))
        {
            return;
        }

        if (listener.Count > 0 && listener[^1][0] == now)
        {
            listener[^1] = row;
            return;
        }

        listener.Add(row);
    }

    internal static void Write()
    {
        string? dir = directory;
        if (dir == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            if (!armed)
            {
                File.WriteAllText(Path.Combine(dir, RimRunnerDataLayout.FilmAudioEventsFileName), RimRunnerJson.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("unpinned", RimRunnerFilmClock.TimeUnpinned ?? "开录时钟没有钉住");
                    writer.WriteEndObject();
                }, indented: false));
                return;
            }

            RimRunnerFilmClock.TryPinnedRealtime(out float end);
            List<ClipEntry> entries = CollectClips();
            foreach (RimRunnerFilmVoice voice in voices)
            {
                entries.Find(entry => entry.Covers(voice.Sound, clips))?.Need(voice.NeededFrames);
            }

            using (FileStream pcm = File.Create(Path.Combine(dir, RimRunnerDataLayout.FilmAudioPcmFileName)))
            {
                foreach (ClipEntry entry in entries)
                {
                    entry.Export(pcm);
                }
            }

            File.WriteAllText(Path.Combine(dir, RimRunnerDataLayout.FilmAudioEventsFileName), RimRunnerJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("end", end - frame0);
                writer.WriteStartArray("clips");
                foreach (ClipEntry entry in entries)
                {
                    entry.WriteJson(writer);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("voices");
                foreach (RimRunnerFilmVoice voice in voices)
                {
                    voice.WriteJson(writer, entries.FindIndex(entry => entry.Covers(voice.Sound, clips)), frame0);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("listener");
                foreach (float[] row in listener)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(row[0] - frame0);
                    for (int i = 1; i < row.Length; i++)
                    {
                        writer.WriteNumberValue(row[i]);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }, indented: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(RimRunnerLog.Prefix + " 无法写出拍摄音轨事件表（" + dir + "）：" + exception.Message);
        }
    }

    private static List<ClipEntry> CollectClips()
    {
        var entries = new List<ClipEntry>();
        foreach (RimRunnerFilmVoice voice in voices)
        {
            if (!voice.Audible || entries.Any(entry => entry.Covers(voice.Sound, clips)))
            {
                continue;
            }

            entries.Add(new ClipEntry(clips.TryGetValue(voice.Sound, out AudioClip? clip) ? clip : null, voice.Sound));
        }

        return entries;
    }

    private static readonly AccessTools.FieldRef<AudioClip, string?> SourcePath = AccessTools.FieldRefAccess<AudioClip, string?>("sourcePath");
    private static readonly AccessTools.FieldRef<AudioClip, bool> Streamed = AccessTools.FieldRefAccess<AudioClip, bool>("streamed");

    private sealed class ClipEntry(AudioClip? clip, IAudioSound sound)
    {
        private int channels;
        private int frequency;
        private long frames;
        private long offset;
        private long needed;
        private string? error;

        private bool IsStream => clip != null ? Streamed(clip) : sound.IsStream;

        internal bool Covers(IAudioSound other, Dictionary<IAudioSound, AudioClip> owners) =>
            ReferenceEquals(other, sound) || (clip != null && owners.TryGetValue(other, out AudioClip? owner) && owner == clip);

        internal void Need(long frames) => needed = Math.Max(needed, frames);

        internal void Export(Stream output)
        {
            IAudioSound source = sound;
            bool reloaded = false;
            try
            {
                if (IsStream)
                {
                    string? path = clip != null ? SourcePath(clip) : null;
                    if (path == null)
                    {
                        error = "流式 clip 没有源路径";
                        return;
                    }

                    source = AudioBackend.Current!.LoadSound(path, streaming: false);
                    reloaded = true;
                }

                channels = source.Channels;
                frequency = source.Frequency;
                frames = Math.Min(source.LengthFrames, needed);
                var samples = new float[frames * channels];
                if (!source.TryReadPcm(samples, 0))
                {
                    error = "TryReadPcm 返回 false";
                    return;
                }

                offset = output.Position;
                var bytes = new byte[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    short value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                    bytes[i * 2] = (byte)value;
                    bytes[i * 2 + 1] = (byte)(value >> 8);
                }

                output.Write(bytes);
            }
            catch (Exception exception) when (exception is not IOException)
            {
                error = exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                if (reloaded)
                {
                    source.Release();
                }
            }
        }

        internal void WriteJson(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("path", clip != null ? SourcePath(clip) : null);
            writer.WriteBoolean("streamed", IsStream);
            if (error != null)
            {
                writer.WriteString("error", error);
            }
            else
            {
                writer.WriteNumber("channels", channels);
                writer.WriteNumber("hz", frequency);
                writer.WriteNumber("frames", frames);
                writer.WriteNumber("offset", offset);
            }

            writer.WriteEndObject();
        }
    }
}

[HarmonyPatch(typeof(MiniAudioBackend), nameof(MiniAudioBackend.Play))]
internal static class Patch_MiniAudioBackend_Play_RimRunnerFilmAudio
{
    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static bool Prefix(IAudioSound sound, bool paused, ref IAudioVoice? __result)
    {
        if (!RimRunnerFilmClock.TryPinnedRealtime(out float now))
        {
            return true;
        }

        __result = new RimRunnerFilmVoice(sound, paused, now);
        return false;
    }
}

[HarmonyPatch(typeof(MiniAudioBackend), nameof(MiniAudioBackend.SetListener))]
internal static class Patch_MiniAudioBackend_SetListener_RimRunnerFilmAudio
{
    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static void Prefix(System.Numerics.Vector3 position, System.Numerics.Vector3 forward, System.Numerics.Vector3 up) =>
        RimRunnerFilmAudio.NoteListener(position, forward, up);
}

[HarmonyPatch(typeof(AudioClip), "AcquireSound")]
internal static class Patch_AudioClip_AcquireSound_RimRunnerFilmAudio
{
    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static void Postfix(AudioClip __instance, IAudioSound __result)
    {
        if (__result != null)
        {
            RimRunnerFilmAudio.NoteSound(__instance, __result);
        }
    }
}

[HarmonyPatch(typeof(AudioSource), "Play", typeof(double))]
internal static class Patch_AudioSource_Play_RimRunnerFilmAudio
{
    private static readonly AccessTools.FieldRef<AudioSource, IAudioVoice?> Voice = AccessTools.FieldRefAccess<AudioSource, IAudioVoice?>("_voice");

    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static void Postfix(AudioSource __instance)
    {
        if (Voice(__instance) is RimRunnerFilmVoice voice)
        {
            voice.IgnoresListenerVolume = __instance.ignoreListenerVolume;
        }
    }
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), typeof(AudioClip), typeof(float))]
internal static class Patch_AudioSource_PlayOneShot_RimRunnerFilmAudio
{
    private static readonly AccessTools.FieldRef<AudioSource, List<(IAudioVoice voice, IAudioSound sound)>> OneShots =
        AccessTools.FieldRefAccess<AudioSource, List<(IAudioVoice voice, IAudioSound sound)>>("_oneShots");

    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static void Postfix(AudioSource __instance)
    {
        List<(IAudioVoice voice, IAudioSound sound)> shots = OneShots(__instance);
        if (shots.Count > 0 && shots[^1].voice is RimRunnerFilmVoice voice)
        {
            voice.IgnoresListenerVolume = __instance.ignoreListenerVolume;
        }
    }
}
