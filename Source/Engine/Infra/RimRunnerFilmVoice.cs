using System.Text.Json;
using Waylith.Audio;

namespace RimRunner;

internal sealed class RimRunnerFilmVoice : IAudioVoice
{
    private readonly struct Row(float time, string name, float[] values)
    {
        internal readonly float Time = time;
        internal readonly string Name = name;
        internal readonly float[] Values = values;
    }

    internal readonly IAudioSound Sound;
    private readonly float start;
    private readonly bool startPaused;
    private readonly List<Row>? rows;
    private readonly Dictionary<string, int>? latest;
    private double position;
    private float last;
    private float pitch = 1f;
    private double step;
    private double delay;
    private bool paused;
    private bool loop;
    private bool looped;
    private double furthest;
    private bool stopped;
    private bool ended;

    internal bool IgnoresListenerVolume;
    internal bool Audible { get; private set; }

    internal RimRunnerFilmVoice(IAudioSound sound, bool paused, float now)
    {
        Sound = sound;
        step = RimRunnerFilmPitch.Exact(sound.Frequency);
        this.paused = startPaused = paused;
        start = last = now;
        if (RimRunnerFilmAudio.Recording)
        {
            rows = [];
            latest = [];
            RimRunnerFilmAudio.NoteVoice(this);
        }
    }

    private void Advance()
    {
        if (stopped || ended)
        {
            return;
        }

        if (!RimRunnerFilmClock.TryPinnedRealtime(out float now))
        {
            ended = true;
            return;
        }

        double dt = Math.Max(0f, now - last);
        last = now;
        if (paused)
        {
            return;
        }

        if (delay > 0)
        {
            double used = Math.Min(delay, dt);
            delay -= used;
            dt -= used;
        }

        position += dt * RimRunnerFilmPitch.OutputRate * step;
        furthest = Math.Max(furthest, position);
        if (!loop && position >= Sound.LengthFrames)
        {
            ended = true;
        }
    }

    internal long NeededFrames
    {
        get
        {
            Advance();
            return looped ? Sound.LengthFrames : (long)Math.Ceiling(furthest) + Math.Max(1, Sound.Frequency);
        }
    }

    private void Record(string name, params float[] values)
    {
        Advance();
        if (rows == null || stopped || ended)
        {
            return;
        }

        string key = RimRunnerFilmAudioRows.Route(name) ? "route" : name;
        if (latest!.TryGetValue(key, out int index))
        {
            Row previous = rows[index];
            if (previous.Name == name && previous.Values.AsSpan().SequenceEqual(values))
            {
                return;
            }

            if (key == name && previous.Time == last)
            {
                rows[index] = new Row(last, name, values);
                return;
            }
        }

        latest[key] = rows.Count;
        rows.Add(new Row(last, name, values));
    }

    private static float Flag(bool value) => value ? 1f : 0f;

    public bool IsPlaying
    {
        get
        {
            Advance();
            return !stopped && !ended;
        }
    }

    public long PositionFrames
    {
        get
        {
            Advance();
            long length = Math.Max(1, Sound.LengthFrames);
            return loop ? (long)position % length : Math.Min((long)position, length);
        }
        set
        {
            Record(RimRunnerFilmAudioRows.Seek, value);
            position = Math.Max(0, value);
            furthest = Math.Max(furthest, position);
        }
    }

    public void SetVolume(float volume)
    {
        Record(RimRunnerFilmAudioRows.Volume, volume);
        Audible |= volume > 0f;
    }

    public void SetMuted(bool muted) => Record(RimRunnerFilmAudioRows.Mute, Flag(muted));

    public void SetPitch(float value)
    {
        Record(RimRunnerFilmAudioRows.Pitch, value);
        if (value != pitch && RimRunnerFilmPitch.Pitched(Sound.Frequency, value) is double pitched)
        {
            step = pitched;
        }

        pitch = value;
    }

    public void SetPriority(int priority) => Advance();

    public void SetPaused(bool value)
    {
        Record(RimRunnerFilmAudioRows.Pause, Flag(value));
        paused = value;
    }

    public void SetLoop(bool value)
    {
        Record(RimRunnerFilmAudioRows.Loop, Flag(value));
        loop = value;
        looped |= value;
    }

    public void SetGroup(IAudioGroup? group) => Advance();
    public void SetPan(float pan) => Record(RimRunnerFilmAudioRows.Pan, pan);
    public void SetMixMatrix(ReadOnlySpan<float> matrix, int outChannels, int inChannels) =>
        Record(RimRunnerFilmAudioRows.Mix, [outChannels, inChannels, .. matrix]);

    public void SetSpatial(bool enabled, float minDistance, float maxDistance, AudioRolloffModel rolloff, float level) =>
        Record(RimRunnerFilmAudioRows.Spatial, Flag(enabled), minDistance, maxDistance, Flag(rolloff == AudioRolloffModel.Linear), level);

    public void SetPosition(float x, float y, float z) => Record(RimRunnerFilmAudioRows.Position, x, y, z);
    public void SetDoppler(float factor) => Advance();
    public void SetSpread(float degrees) => Advance();

    public void ScheduleStart(double delaySeconds)
    {
        Record(RimRunnerFilmAudioRows.Schedule, (float)delaySeconds);
        delay = Math.Max(0, delaySeconds);
    }

    public bool SetLowPass(bool enabled, float cutoffHz, float q)
    {
        Record(RimRunnerFilmAudioRows.LowPass, Flag(enabled), cutoffHz, q);
        return true;
    }

    public bool SetHighPass(bool enabled, float cutoffHz, float q)
    {
        Record(RimRunnerFilmAudioRows.HighPass, Flag(enabled), cutoffHz, q);
        return true;
    }

    public bool SetEcho(bool enabled, float delayMs, float feedback, float wetMix, float dryMix)
    {
        Record(RimRunnerFilmAudioRows.Echo, Flag(enabled), delayMs, feedback, wetMix, dryMix);
        return true;
    }

    public bool SetReverb(bool enabled, in ReverbParams p)
    {
        Record(RimRunnerFilmAudioRows.Reverb, Flag(enabled), p.dryLevelMb, p.roomMb, p.reflectionsLevelMb, p.roomHfMb, p.roomLfMb,
            p.decayTimeSeconds, p.decayHfRatio, p.reflectionsDelaySeconds, p.reverbLevelMb, p.reverbDelaySeconds,
            p.hfReferenceHz, p.lfReferenceHz, p.diffusionPercent, p.densityPercent);
        return true;
    }

    public bool SetChorus(bool enabled, float rateHz, float depth, float mix)
    {
        Record(RimRunnerFilmAudioRows.Chorus, Flag(enabled), rateHz, depth, mix);
        return true;
    }

    public bool SetDistortion(bool enabled, float level)
    {
        Record(RimRunnerFilmAudioRows.Distortion, Flag(enabled), level);
        return true;
    }

    public void EnableOutputTap() { }
    public void ReadOutput(Span<float> samples, int channel) => samples.Clear();

    public void Stop()
    {
        Record(RimRunnerFilmAudioRows.Stop);
        stopped = true;
    }

    internal void WriteJson(Utf8JsonWriter writer, int clip, float frame0)
    {
        writer.WriteStartObject();
        writer.WriteNumber("clip", clip);
        writer.WriteNumber("hz", Sound.Frequency);
        writer.WriteNumber("frames", Sound.LengthFrames);
        writer.WriteNumber("start", start - frame0);
        writer.WriteBoolean("paused", startPaused);
        writer.WriteBoolean("ignoreListenerVolume", IgnoresListenerVolume);
        writer.WriteStartArray("rows");
        foreach (Row row in rows!)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(row.Time - frame0);
            writer.WriteStringValue(row.Name);
            foreach (float value in row.Values)
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
