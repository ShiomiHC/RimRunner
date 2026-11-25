using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

internal static class FilmMixer
{
    internal const int SampleRate = RimRunnerFilmPitch.OutputRate;
    internal const float MasterGain = 0.8f;
    private const float ClipKnee = 0.9f;
    internal const int AttenuationPeriod = 480;
    internal const int AttenuationSmoothFrames = 768;

    internal sealed record Outcome(string? Skipped, int Voices, int Clips, IReadOnlyList<string> Unreadable,
        IReadOnlyList<string> UnknownRows);

    internal static readonly IReadOnlySet<string> Replayed = new HashSet<string>(StringComparer.Ordinal)
    {
        RimRunnerFilmAudioRows.Seek, RimRunnerFilmAudioRows.Volume, RimRunnerFilmAudioRows.Mute, RimRunnerFilmAudioRows.Pitch,
        RimRunnerFilmAudioRows.Pause, RimRunnerFilmAudioRows.Loop, RimRunnerFilmAudioRows.Pan, RimRunnerFilmAudioRows.Mix,
        RimRunnerFilmAudioRows.Spatial, RimRunnerFilmAudioRows.Position, RimRunnerFilmAudioRows.Schedule,
        RimRunnerFilmAudioRows.LowPass, RimRunnerFilmAudioRows.HighPass, RimRunnerFilmAudioRows.Echo,
        RimRunnerFilmAudioRows.Reverb, RimRunnerFilmAudioRows.Stop,
    };

    internal static Outcome MixToWav(string audioDir, int frames, int fps, string wavPath)
    {
        string eventsPath = Path.Combine(audioDir, RimRunnerDataLayout.FilmAudioEventsFileName);
        if (!File.Exists(eventsPath))
        {
            return new Outcome("引擎未写出事件表（" + eventsPath + "）", 0, 0, [], []);
        }

        using JsonDocument events = JsonDocument.Parse(File.ReadAllBytes(eventsPath));
        string pcmPath = Path.Combine(audioDir, RimRunnerDataLayout.FilmAudioPcmFileName);
        byte[] pcm = File.Exists(pcmPath) ? File.ReadAllBytes(pcmPath) : [];
        Outcome outcome = Mix(events.RootElement, pcm, frames, fps, out float[] samples);
        if (outcome.Skipped == null)
        {
            WriteWav(wavPath, samples);
        }

        return outcome;
    }

    internal static Outcome Mix(JsonElement root, ReadOnlySpan<byte> pcm, int frames, int fps, out float[] samples)
    {
        samples = [];
        if (root.TryGetProperty("unpinned", out JsonElement unpinned))
        {
            return new Outcome("拍摄实例的时间未钉住：" + unpinned.GetString(), 0, 0, [], []);
        }

        if (fps <= 0)
        {
            return new Outcome("帧率不是正数", 0, 0, [], []);
        }

        var clips = new List<Clip?>();
        var unreadable = new List<string>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonElement clip in root.GetProperty("clips").EnumerateArray())
        {
            string label = clip.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()!
                : "（无源路径）";
            if (clip.TryGetProperty("error", out JsonElement error))
            {
                unreadable.Add(label + "：" + error.GetString());
                clips.Add(null);
                continue;
            }

            clips.Add(Clip.Read(clip, pcm));
        }

        Listener listener = Listener.Read(root.GetProperty("listener"));
        long total = (long)Math.Round(frames * (double)SampleRate / fps);
        samples = new float[total * 2];
        int voices = 0;
        foreach (JsonElement voice in root.GetProperty("voices").EnumerateArray())
        {
            int index = voice.GetProperty("clip").GetInt32();
            if (index < 0 || index >= clips.Count || clips[index] is not { } clip)
            {
                continue;
            }

            new Voice(voice, clip, listener, unknown).Render(samples);
            voices++;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = SoftClip(samples[i]);
        }

        return new Outcome(null, voices, clips.Count(clip => clip != null), unreadable, [.. unknown]);
    }

    internal static float SoftClip(float value)
    {
        float magnitude = Math.Abs(value);
        if (magnitude <= ClipKnee)
        {
            return value;
        }

        float squashed = ClipKnee + (1f - ClipKnee) * MathF.Tanh((magnitude - ClipKnee) / (1f - ClipKnee));
        return MathF.CopySign(squashed, value);
    }

    internal static void WriteWav(string path, float[] samples)
    {
        const int channels = 2;
        int dataBytes = samples.Length * 2;
        var bytes = new byte[44 + dataBytes];
        Span<byte> header = bytes.AsSpan(0, 44);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), value);
        }

        File.WriteAllBytes(path, bytes);
    }

    private sealed class Clip
    {
        internal required int Channels;
        internal required int Hz;
        internal required long Frames;
        internal required float[] Data;

        internal static Clip? Read(JsonElement clip, ReadOnlySpan<byte> pcm)
        {
            int channels = clip.GetProperty("channels").GetInt32();
            long frames = clip.GetProperty("frames").GetInt64();
            long offset = clip.GetProperty("offset").GetInt64();
            long count = frames * channels;
            if (channels <= 0 || frames <= 0 || offset < 0 || offset + count * 2 > pcm.Length)
            {
                return null;
            }

            var data = new float[count];
            ReadOnlySpan<byte> bytes = pcm.Slice((int)offset, (int)(count * 2));
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]) / 32768f;
            }

            return new Clip { Channels = channels, Hz = clip.GetProperty("hz").GetInt32(), Frames = frames, Data = data };
        }
    }

    private sealed class Listener
    {
        private readonly List<(double Time, float[] Pose)> rows = [];

        internal static Listener Read(JsonElement array)
        {
            var listener = new Listener();
            foreach (JsonElement row in array.EnumerateArray())
            {
                float[] values = row.EnumerateArray().Select(item => item.GetSingle()).ToArray();
                listener.rows.Add((values[0], values[1..]));
            }

            return listener;
        }

        internal float[]? At(double time)
        {
            if (rows.Count == 0)
            {
                return null;
            }

            int index = rows.FindLastIndex(row => row.Time <= time);
            return rows[Math.Max(0, index)].Pose;
        }
    }

    private sealed class Voice
    {
        private const float MinimumDistance = 1e-4f;

        private enum Route { Default, Pan, Matrix }

        private readonly Clip clip;
        private readonly Listener listener;
        private readonly int voiceHz;
        private readonly long voiceFrames;
        private readonly double start;
        private readonly bool ignoresListenerVolume;
        private readonly List<(double Time, string Name, float[] Values)> rows = [];

        private double position;
        private float pitch = 1f;
        private double step;
        private long delay;
        private bool paused;
        private bool loop;
        private bool stopped;
        private bool ended;
        private float volume = 1f;
        private bool muted;
        private Route route;
        private float pan;
        private float[]? matrix;
        private bool attenuating;
        private float minDistance = 1f;
        private float maxDistance = 500f;
        private bool linearRolloff;
        private (float X, float Y, float Z) source;

        private readonly float[] gains;
        private float post;
        private bool dirty = true;
        private float attenuationFrom;
        private float attenuationTo;
        private long periodStart = -1;
        private readonly float[] frame;
        private readonly float[] mixed = new float[2];
        private readonly FilterChain filters = new();
        private long tailEnd = -1;

        internal Voice(JsonElement voice, Clip clip, Listener listener, ISet<string> unknown)
        {
            this.clip = clip;
            this.listener = listener;
            voiceHz = voice.GetProperty("hz").GetInt32();
            voiceFrames = voice.GetProperty("frames").GetInt64();
            start = voice.GetProperty("start").GetDouble();
            paused = voice.GetProperty("paused").GetBoolean();
            ignoresListenerVolume = voice.GetProperty("ignoreListenerVolume").GetBoolean();
            step = RimRunnerFilmPitch.Exact(voiceHz);
            foreach (JsonElement row in voice.GetProperty("rows").EnumerateArray())
            {
                var items = row.EnumerateArray().ToList();
                string name = items[1].GetString()!;
                if (!Replayed.Contains(name) && !RimRunnerFilmAudioRows.Unsupported(name))
                {
                    unknown.Add(name);
                }

                rows.Add((items[0].GetDouble(), name, items.Skip(2).Select(item => item.GetSingle()).ToArray()));
            }

            gains = new float[2 * clip.Channels];
            frame = new float[clip.Channels];
        }

        private bool Active => !stopped && !ended;

        internal void Render(float[] output)
        {
            long total = output.Length / 2;
            int row = 0;
            long n = (long)Math.Ceiling(start * SampleRate);
            while (n < total && (Active || row < rows.Count || n < tailEnd))
            {
                while (row < rows.Count && rows[row].Time <= n / (double)SampleRate)
                {
                    Apply(rows[row].Name, rows[row].Values);
                    dirty = true;
                    row++;
                }

                if (n < 0)
                {
                    long next = row < rows.Count ? Math.Min(0, (long)Math.Ceiling(rows[row].Time * SampleRate)) : 0;
                    next = Math.Max(next, n + 1);
                    Skip(next - n);
                    n = next;
                    continue;
                }

                if (Active || n < tailEnd)
                {
                    Emit(n, output);
                }

                n++;
            }
        }

        private void Skip(long count)
        {
            if (!Active || paused)
            {
                return;
            }

            long waited = Math.Min(delay, count);
            delay -= waited;
            position += step * (count - waited);
            if (!loop && position >= voiceFrames)
            {
                ended = true;
            }
        }

        private void Emit(long n, float[] output)
        {
            bool periodBegins = n / AttenuationPeriod != periodStart;
            if (periodBegins || dirty)
            {
                Gains(n / (double)SampleRate);
            }

            if (periodBegins)
            {
                float target = Attenuation(n / (double)SampleRate);
                attenuationFrom = periodStart < 0 ? target : attenuationTo;
                attenuationTo = target;
                periodStart = n / AttenuationPeriod;
            }

            dirty = false;
            bool feeding = Active && !paused;
            if (feeding && delay > 0)
            {
                delay--;
                feeding = false;
            }

            mixed[0] = mixed[1] = 0f;
            if (feeding)
            {
                long into = n - periodStart * AttenuationPeriod;
                float attenuation = attenuationFrom + (attenuationTo - attenuationFrom) * Math.Min(1f, into / (float)AttenuationSmoothFrames);
                Sample(frame);
                for (int o = 0; o < 2; o++)
                {
                    float sum = 0f;
                    for (int c = 0; c < clip.Channels; c++)
                    {
                        sum += gains[o * clip.Channels + c] * frame[c];
                    }

                    mixed[o] = sum * attenuation;
                }

                position += step;
                if (!loop && position >= voiceFrames)
                {
                    ended = true;
                    tailEnd = filters.Enabled ? ((n + 1) / AttenuationPeriod + 1) * AttenuationPeriod : -1;
                }
            }

            if (filters.Enabled)
            {
                filters.Process(mixed);
            }
            else if (!feeding)
            {
                return;
            }

            output[n * 2] += mixed[0] * post;
            output[n * 2 + 1] += mixed[1] * post;
        }

        private void Apply(string name, float[] values)
        {
            if (!Active)
            {
                return;
            }

            switch (name)
            {
                case RimRunnerFilmAudioRows.Volume: volume = values[0]; break;
                case RimRunnerFilmAudioRows.Mute: muted = values[0] != 0f; break;
                case RimRunnerFilmAudioRows.Pitch:
                    if (values[0] != pitch && RimRunnerFilmPitch.Pitched(voiceHz, values[0]) is double pitched)
                    {
                        step = pitched;
                    }

                    pitch = values[0];
                    break;
                case RimRunnerFilmAudioRows.Pause: paused = values[0] != 0f; break;
                case RimRunnerFilmAudioRows.Loop: loop = values[0] != 0f; break;
                case RimRunnerFilmAudioRows.Pan:
                    route = Route.Pan;
                    pan = Math.Clamp(values[0], -1f, 1f);
                    break;
                case RimRunnerFilmAudioRows.Mix:
                    if ((int)values[0] == 2 && (int)values[1] == clip.Channels && values.Length == 2 + 2 * clip.Channels)
                    {
                        route = Route.Matrix;
                        matrix = values[2..];
                    }

                    break;
                case RimRunnerFilmAudioRows.Spatial:
                    attenuating = values[0] != 0f && values[4] > 0f;
                    if (route == Route.Matrix)
                    {
                        route = Route.Default;
                    }

                    minDistance = values[1];
                    maxDistance = values[2];
                    linearRolloff = values[3] != 0f;
                    break;
                case RimRunnerFilmAudioRows.Position: source = (values[0], values[1], values[2]); break;
                case RimRunnerFilmAudioRows.Schedule: delay = Math.Max(0, (long)(values[0] * SampleRate)); break;
                case RimRunnerFilmAudioRows.Seek: position = Math.Max(0, values[0]); break;
                case RimRunnerFilmAudioRows.Stop: stopped = true; break;
                case RimRunnerFilmAudioRows.LowPass: filters.SetLowPass(values[0] != 0f, values[1], values[2]); break;
                case RimRunnerFilmAudioRows.HighPass: filters.SetHighPass(values[0] != 0f, values[1], values[2]); break;
                case RimRunnerFilmAudioRows.Echo: filters.SetEcho(values[0] != 0f, values[1], values[2], values[3], values[4]); break;
                case RimRunnerFilmAudioRows.Reverb: filters.SetReverb(ReverbSettings.FromRow(values)); break;
            }
        }

        private void Sample(float[] output)
        {
            double at = (position - 1) * clip.Hz / Math.Max(1, voiceHz);
            long i0 = (long)Math.Floor(at);
            float t = (float)(at - i0);
            for (int c = 0; c < clip.Channels; c++)
            {
                float a = Read(i0, c);
                float b = Read(i0 + 1, c);
                output[c] = a + (b - a) * t;
            }
        }

        private float Read(long index, int channel)
        {
            if (index < 0)
            {
                return 0f;
            }

            if (index >= clip.Frames)
            {
                if (!loop)
                {
                    return clip.Data[(clip.Frames - 1) * clip.Channels + channel];
                }

                index %= clip.Frames;
            }

            return clip.Data[index * clip.Channels + channel];
        }

        private void Gains(double time)
        {
            int channels = clip.Channels;
            Array.Clear(gains);
            if (route == Route.Matrix && !attenuating)
            {
                matrix!.CopyTo(gains, 0);
            }
            else
            {
                gains[0] = gains[channels == 1 ? 1 : channels + 1] = 1f;
            }

            if (route == Route.Pan && pan != 0f)
            {
                int from = pan > 0f ? 0 : 1;
                int to = 1 - from;
                float moved = Math.Abs(pan);
                for (int c = 0; c < channels; c++)
                {
                    float value = gains[from * channels + c];
                    gains[to * channels + c] += value * moved;
                    gains[from * channels + c] = value * (1f - moved);
                }
            }

            if (attenuating)
            {
                float angle = (Azimuth(time) + 1f) * MathF.PI / 4f;
                float left = MathF.Cos(angle);
                float right = MathF.Sin(angle);
                for (int c = 0; c < channels; c++)
                {
                    gains[c] *= left;
                    gains[channels + c] *= right;
                }
            }

            post = muted ? 0f : volume * (ignoresListenerVolume ? 1f : MasterGain);
        }

        private (float X, float Y, float Z)? Relative(double time, bool approximate)
        {
            if (listener.At(time) is not { } pose)
            {
                return null;
            }

            (float X, float Y, float Z) d = (-(source.X - pose[0]), source.Y - pose[1], source.Z - pose[2]);
            (float X, float Y, float Z) forward = (-pose[3], pose[4], pose[5]);
            (float X, float Y, float Z) up = (-pose[6], pose[7], pose[8]);
            var z = Scale(forward, Normalizer(Dot(forward, forward), approximate));
            var cross = Cross(z, up);
            float length = Dot(cross, cross);
            var x = length > 0f ? Scale(cross, Normalizer(length, approximate)) : (1f, 0f, 0f);
            var y = Cross(x, z);
            return (Dot(x, d), Dot(y, d), -Dot(z, d));
        }

        private float Attenuation(double time)
        {
            if (!attenuating || Relative(time, approximate: true) is not { } relative)
            {
                return 1f;
            }

            float distance = MathF.Sqrt(Dot(relative, relative));
            float min = linearRolloff ? minDistance : Math.Max(minDistance, MinimumDistance);
            float clamped = Math.Clamp(distance, min, Math.Max(min, maxDistance));
            float attenuation = linearRolloff
                ? maxDistance > min ? 1f - (clamped - min) / (maxDistance - min) : 1f
                : min / clamped;
            return Math.Clamp(attenuation, 0f, 1f);
        }

        private float Azimuth(double time)
        {
            if (Relative(time, approximate: false) is not { } relative)
            {
                return 0f;
            }

            float distance = MathF.Sqrt(Dot(relative, relative));
            return distance > 1e-6f ? Math.Clamp(relative.X / distance, -1f, 1f) : 0f;
        }

        private static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static (float X, float Y, float Z) Cross((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
            (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static (float X, float Y, float Z) Scale((float X, float Y, float Z) a, float k) => (a.X * k, a.Y * k, a.Z * k);

        private static float Normalizer(float lengthSquared, bool approximate) =>
            approximate && Sse.IsSupported
                ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(lengthSquared)).ToScalar()
                : 1f / MathF.Sqrt(lengthSquared);
    }
    internal readonly record struct ReverbSettings(float Room, float Damp, float Wet, float Dry, float Width, float HighCut)
    {
        internal static ReverbSettings? FromRow(float[] values)
        {
            if (values[0] == 0f || values.Length < 15)
            {
                return null;
            }

            float dryMb = values[1], roomMb = values[2], roomHfMb = values[4], decay = values[6], hfRatio = values[7];
            float reverbMb = values[9], hfReference = values[11], diffusion = values[13];
            float room = Math.Clamp((MathF.Pow(0.001f, 0.0317f / Math.Max(0.05f, decay)) - 0.7f) / 0.28f, 0f, 0.98f);
            float damp = Math.Clamp(1f - hfRatio, 0f, 1f);
            float wet = MathF.Pow(10f, Math.Clamp(roomMb + reverbMb, -10000f, 2000f) / 2000f) / 3f;
            float dry = MathF.Pow(10f, Math.Clamp(dryMb, -10000f, 2000f) / 2000f);
            float width = Math.Clamp(diffusion / 100f, 0f, 1f);
            float hf = MathF.Pow(10f, Math.Clamp(roomHfMb, -10000f, 0f) / 2000f);
            float highCut = hf >= 1f ? 0f : Math.Clamp(Math.Clamp(hfReference, 20f, 20000f) * MathF.Sqrt(hf / (1f - hf)), 20f, 20000f);
            return new ReverbSettings(room, damp, wet, dry, width, highCut);
        }
    }

    private sealed class FilterChain
    {
        private Biquad? lowPass;
        private Biquad? highPass;
        private Echo? echo;
        private Freeverb? reverb;

        internal bool Enabled => lowPass != null || highPass != null || echo != null || reverb != null;

        internal void SetLowPass(bool enabled, float cutoffHz, float q) => lowPass = enabled ? (lowPass ?? new Biquad()).LowPass(cutoffHz, q) : null;

        internal void SetHighPass(bool enabled, float cutoffHz, float q) => highPass = enabled ? (highPass ?? new Biquad()).HighPass(cutoffHz, q) : null;

        internal void SetEcho(bool enabled, float delayMs, float feedback, float wet, float dry) =>
            echo = enabled ? (echo ?? new Echo()).Set(delayMs, feedback, wet, dry) : null;

        internal void SetReverb(ReverbSettings? settings) => reverb = settings is { } value ? (reverb ?? new Freeverb()).Set(value) : null;

        internal void Process(float[] frame)
        {
            lowPass?.Process(frame);
            highPass?.Process(frame);
            echo?.Process(frame);
            reverb?.Process(frame);
        }
    }

    private sealed class Biquad
    {
        private float b0, b1, b2, a1, a2;
        private readonly float[] r1 = new float[2];
        private readonly float[] r2 = new float[2];

        internal Biquad LowPass(float cutoffHz, float q)
        {
            (double c, double a0, double a) = Shape(cutoffHz, q);
            Set((1 - c) / 2, 1 - c, (1 - c) / 2, a0, -2 * c, 1 - a);
            return this;
        }

        internal Biquad HighPass(float cutoffHz, float q)
        {
            (double c, double a0, double a) = Shape(cutoffHz, q);
            Set((1 + c) / 2, -(1 + c), (1 + c) / 2, a0, -2 * c, 1 - a);
            return this;
        }

        private static (double C, double A0, double Alpha) Shape(float cutoffHz, float q)
        {
            double w = 2 * Math.PI * cutoffHz / SampleRate;
            double alpha = Math.Sin(w) / (2 * q);
            return (Math.Cos(w), 1 + alpha, alpha);
        }

        private void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
        {
            float a0 = (float)na0;
            b0 = (float)nb0 / a0;
            b1 = (float)nb1 / a0;
            b2 = (float)nb2 / a0;
            a1 = (float)na1 / a0;
            a2 = (float)na2 / a0;
        }

        internal void Process(float[] frame)
        {
            for (int c = 0; c < 2; c++)
            {
                float x = frame[c];
                float y = b0 * x + r1[c];
                r1[c] = b1 * x - a1 * y + r2[c];
                r2[c] = b2 * x - a2 * y;
                frame[c] = y;
            }
        }
    }

    private sealed class Echo
    {
        private const int Capacity = 5000 * SampleRate / 1000 + 1;
        private readonly float[] buffer = new float[Capacity * 2];
        private int write;
        private int delay;
        private float feedback, wet, dry;

        internal Echo Set(float delayMs, float feedback, float wet, float dry)
        {
            delay = Math.Clamp((int)(delayMs * (SampleRate / 1000f)), 1, Capacity - 1);
            this.feedback = feedback;
            this.wet = wet;
            this.dry = dry;
            return this;
        }

        internal void Process(float[] frame)
        {
            int read = write - delay;
            if (read < 0)
            {
                read += Capacity;
            }

            for (int c = 0; c < 2; c++)
            {
                float x = frame[c];
                float tap = buffer[read * 2 + c];
                buffer[write * 2 + c] = x + feedback * tap;
                frame[c] = dry * x + wet * tap;
            }

            if (++write == Capacity)
            {
                write = 0;
            }
        }
    }

    private sealed class Freeverb
    {
        private const float FixedGain = 0.015f;
        private const float ScaleWet = 3f;
        private const float ScaleDamp = 0.4f;
        private const float ScaleRoom = 0.28f;
        private const float OffsetRoom = 0.7f;
        private const float AllPassFeedback = 0.5f;
        private const int StereoSpread = 23;
        private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        private static readonly int[] AllPassTunings = [556, 441, 341, 225];

        private readonly Comb[][] combs = [Combs(0), Combs(StereoSpread)];
        private readonly Line[][] allPasses = [AllPasses(0), AllPasses(StereoSpread)];
        private readonly float[] smoothed = new float[2];
        private float roomFeedback, damp, wet1, wet2, dry, highCut;

        private static int Scale(int tuning) => (int)(tuning * (double)SampleRate / 44100);

        private static Comb[] Combs(int spread) => CombTunings.Select(tuning => new Comb(Scale(tuning + spread))).ToArray();

        private static Line[] AllPasses(int spread) => AllPassTunings.Select(tuning => new Line(Scale(tuning + spread))).ToArray();

        internal Freeverb Set(ReverbSettings settings)
        {
            roomFeedback = settings.Room * ScaleRoom + OffsetRoom;
            damp = settings.Damp * ScaleDamp;
            float wet = settings.Wet * ScaleWet;
            wet1 = wet * (settings.Width / 2f + 0.5f);
            wet2 = wet * ((1f - settings.Width) / 2f);
            dry = settings.Dry;
            highCut = settings.HighCut > 0f ? 1f - MathF.Exp(-2f * MathF.PI * settings.HighCut / SampleRate) : 0f;
            return this;
        }

        internal void Process(float[] frame)
        {
            float input = (frame[0] + frame[1]) * FixedGain;
            Span<float> wet = stackalloc float[2];
            for (int c = 0; c < 2; c++)
            {
                float sum = 0f;
                foreach (Comb comb in combs[c])
                {
                    sum += comb.Process(input, roomFeedback, damp);
                }

                foreach (Line allPass in allPasses[c])
                {
                    float delayed = allPass.Buffer[allPass.Index];
                    float output = -sum + delayed;
                    allPass.Buffer[allPass.Index] = sum + delayed * AllPassFeedback;
                    allPass.Advance();
                    sum = output;
                }

                wet[c] = sum;
            }

            for (int c = 0; c < 2; c++)
            {
                float value = wet[c] * wet1 + wet[1 - c] * wet2;
                if (highCut > 0f)
                {
                    smoothed[c] += highCut * (value - smoothed[c]);
                    value = smoothed[c];
                }

                frame[c] = value + frame[c] * dry;
            }
        }

        private class Line(int length)
        {
            internal readonly float[] Buffer = new float[length];
            internal int Index;

            internal void Advance()
            {
                if (++Index == Buffer.Length)
                {
                    Index = 0;
                }
            }
        }

        private sealed class Comb(int length) : Line(length)
        {
            private float store;

            internal float Process(float input, float feedback, float damp)
            {
                float output = Buffer[Index];
                store = output * (1f - damp) + store * damp;
                Buffer[Index] = input + store * feedback;
                Advance();
                return output;
            }
        }
    }
}
