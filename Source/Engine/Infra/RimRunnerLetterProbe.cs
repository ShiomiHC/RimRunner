using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerLetterProbe
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "letters")]
    private static extern ref List<Letter> Letters(LetterStack stack);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "letterQueue")]
    private static extern ref List<Letter> LetterQueue(LetterStack stack);

    private static readonly Dictionary<Type, FieldInfo[]> PawnFieldCache = [];

    internal static int RemoveLettersAbout(Pawn pawn)
    {
        return DropMatching(letter => Holds(letter, candidate => candidate == pawn), null);
    }

    internal static int ReportDangling(string caseId)
    {
        return DropMatching(
            letter => Holds(letter, pawn => pawn.Destroyed || pawn.Discarded),
            caseId);
    }

    internal static HashSet<Letter> SnapshotAll()
    {
        LetterStack stack = Find.LetterStack;
        HashSet<Letter> all = [.. Letters(stack)];
        all.UnionWith(LetterQueue(stack));
        return all;
    }

    internal static int DropArrivedSince(HashSet<Letter> before)
    {
        return DropMatching(letter => !before.Contains(letter), null);
    }

    internal static bool Holds(Letter letter, Func<Pawn, bool> predicate)
    {
        if (letter == null || predicate == null)
        {
            return false;
        }

        LookTargets lookTargets = letter.lookTargets;
        if (lookTargets != null && lookTargets.targets != null)
        {
            for (int i = 0; i < lookTargets.targets.Count; i++)
            {
                if (lookTargets.targets[i].Thing is Pawn pawn && predicate(pawn))
                {
                    return true;
                }
            }
        }

        FieldInfo[] fields = PawnFields(letter.GetType());
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].GetValue(letter) is Pawn pawn && predicate(pawn))
            {
                return true;
            }
        }

        return false;
    }

    private static int DropMatching(Func<Letter, bool> match, string? reportCaseId)
    {
        LetterStack stack = Find.LetterStack;
        List<Letter> letters = Letters(stack);
        List<Letter> queue = LetterQueue(stack);
        List<Letter> matched = [];
        CollectMatches(letters, match, matched);
        CollectMatches(queue, match, matched);
        if (matched.Count == 0)
        {
            return 0;
        }

        if (reportCaseId != null)
        {
            for (int i = 0; i < matched.Count; i++)
            {
                RimRunnerSentinel.Warn(RimRunnerWarningKey.LetterResidue, reportCaseId + " 收尾后信堆里仍有信持有已回收的 pawn（已摘除）",
                    matched[i].Label.ToString());
            }
        }

        for (int i = 0; i < matched.Count; i++)
        {
            Drop(stack, letters, queue, matched[i]);
        }

        return matched.Count;
    }

    private static void CollectMatches(List<Letter> source, Func<Letter, bool> match, List<Letter> matched)
    {
        for (int i = 0; i < source.Count; i++)
        {
            Letter letter = source[i];
            if (letter != null && match(letter) && !matched.Contains(letter))
            {
                matched.Add(letter);
            }
        }
    }

    private static void Drop(LetterStack stack, List<Letter> letters, List<Letter> queue, Letter letter)
    {
        if (letters.Contains(letter))
        {
            stack.RemoveLetter(letter);
        }

        queue.Remove(letter);
    }

    private static FieldInfo[] PawnFields(Type letterType)
    {
        if (PawnFieldCache.TryGetValue(letterType, out FieldInfo[]? cached))
        {
            return cached;
        }

        List<FieldInfo> fields = [];
        Type? type = letterType;
        while (type != null && typeof(Letter).IsAssignableFrom(type))
        {
            FieldInfo[] declared = RimRunnerAbsentReferences.ReadableFields(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < declared.Length; i++)
            {
                if (typeof(Pawn).IsAssignableFrom(declared[i].FieldType))
                {
                    fields.Add(declared[i]);
                }
            }

            type = type.BaseType;
        }

        FieldInfo[] array = fields.ToArray();
        PawnFieldCache[letterType] = array;
        return array;
    }
}
