using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerFilmMusic
{
    private static bool held;
    private static bool wasDisabled;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "audioSource")]
    private static extern ref AudioSource? Source(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "gameObjectCreated")]
    private static extern ref bool GameObjectCreated(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InitializeMusicManager")]
    private static extern void Initialize(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
    private static extern ref MusicManagerPlay.MusicManagerState State(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<IsPlaying>k__BackingField")]
    private static extern ref bool IsPlaying(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentSong")]
    private static extern ref SongDef? CurrentSong(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "sequenceWorker")]
    private static extern ref MusicSequenceWorker? SequenceWorker(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "triggeredTransition")]
    private static extern ref MusicTransition? TriggeredTransition(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "manuallyTriggered")]
    private static extern ref bool ManuallyTriggered(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loopWhileFading")]
    private static extern ref bool LoopWhileFading(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "songWasForced")]
    private static extern ref bool SongWasForced(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ignorePrefsVolumeThisSong")]
    private static extern ref bool IgnorePrefsVolume(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fadeoutFactor")]
    private static extern ref float FadeoutFactor(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fadeinFactor")]
    private static extern ref float FadeinFactor(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fadeInStarted")]
    private static extern ref float FadeInStarted(MusicManagerPlay music);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "musicOverridenFadeFactor")]
    private static extern ref float OverriddenFadeFactor(MusicManagerPlay music);

    internal static string? Check(string? defName) =>
        defName == null || DefDatabase<SongDef>.GetNamedSilentFail(defName) != null
            ? null
            : "镜头声明的音乐「" + defName + "」不是已加载的 SongDef";

    internal static void Hold()
    {
        if (Find.MusicManagerPlay is not { } music)
        {
            return;
        }

        if (!held)
        {
            wasDisabled = music.disabled;
            held = true;
        }

        music.disabled = true;
        if (!GameObjectCreated(music))
        {
            Initialize(music);
        }

        Silence(music);
        music.subtleAmbienceSoundVolumeMultiplier = 1f;
    }

    internal static void Start(string? defName)
    {
        if (!held || defName == null || Find.MusicManagerPlay is not { } music
            || DefDatabase<SongDef>.GetNamedSilentFail(defName) is not { } song)
        {
            return;
        }

        music.ForcePlaySong(song, ignorePrefsVolume: false);
    }

    internal static void Release()
    {
        if (!held)
        {
            return;
        }

        held = false;
        if (Find.MusicManagerPlay is { } music)
        {
            Silence(music);
            music.disabled = wasDisabled;
        }
    }

    private static void Silence(MusicManagerPlay music)
    {
        Source(music)?.Stop();
        SequenceWorker(music) = null;
        TriggeredTransition(music) = null;
        ManuallyTriggered(music) = false;
        LoopWhileFading(music) = false;
        State(music) = MusicManagerPlay.MusicManagerState.Normal;
        IsPlaying(music) = false;
        CurrentSong(music) = null;
        SongWasForced(music) = false;
        IgnorePrefsVolume(music) = false;
        FadeoutFactor(music) = 1f;
        FadeinFactor(music) = 1f;
        FadeInStarted(music) = 0f;
        OverriddenFadeFactor(music) = 1f;
    }
}
