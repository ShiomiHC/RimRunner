using System.Reflection.Metadata;

namespace RimRunnerCli;

internal readonly struct IlInstruction(int offset, int opCode, long operand)
{
    public int Offset { get; } = offset;

    public int OpCode { get; } = opCode;

    public long Operand { get; } = operand;

    public int Token => (int)Operand;

    public string Display => RosterIl.Name(OpCode);
}

internal static class RosterIl
{
    internal const int LdArg0 = 0x02;
    internal const int LdNull = 0x14;
    internal const int LdcI4M1 = 0x15;
    internal const int LdcI4_0 = 0x16;
    internal const int LdcI4_8 = 0x1E;
    internal const int LdcI4S = 0x1F;
    internal const int LdcI4 = 0x20;
    internal const int Dup = 0x25;
    internal const int Pop = 0x26;
    internal const int Call = 0x28;
    internal const int Ret = 0x2A;
    internal const int BrTrueS = 0x2D;
    internal const int Ldstr = 0x72;
    internal const int Newobj = 0x73;
    internal const int Ldfld = 0x7B;
    internal const int Ldsfld = 0x7E;
    internal const int Stsfld = 0x80;
    internal const int Newarr = 0x8D;
    internal const int StelemRef = 0xA2;
    internal const int Ldftn = 0xFE06;

    internal static bool TryRead(ref BlobReader reader, out IlInstruction instruction)
    {
        int offset = reader.Offset;
        int first = reader.ReadByte();
        int opCode = first == 0xFE ? 0xFE00 | reader.ReadByte() : first;
        switch (opCode)
        {
            case LdArg0:
            case LdNull:
            case Dup:
            case Pop:
            case Ret:
            case StelemRef:
                instruction = new IlInstruction(offset, opCode, 0);
                return true;
            case LdcI4M1:
                instruction = new IlInstruction(offset, opCode, -1);
                return true;
            case >= LdcI4_0 and <= LdcI4_8:
                instruction = new IlInstruction(offset, opCode, opCode - LdcI4_0);
                return true;
            case LdcI4S:
                instruction = new IlInstruction(offset, opCode, reader.ReadSByte());
                return true;
            case LdcI4:
                instruction = new IlInstruction(offset, opCode, reader.ReadInt32());
                return true;
            case BrTrueS:
                reader.ReadSByte();
                instruction = new IlInstruction(offset, opCode, 0);
                return true;
            case Call:
            case Ldstr:
            case Newobj:
            case Ldfld:
            case Ldsfld:
            case Stsfld:
            case Newarr:
            case Ldftn:
                instruction = new IlInstruction(offset, opCode, reader.ReadInt32());
                return true;
            default:
                instruction = new IlInstruction(offset, opCode, 0);
                return false;
        }
    }

    internal static bool IsI4(IlInstruction instruction) =>
        instruction.OpCode is LdcI4M1 or >= LdcI4_0 and <= LdcI4_8 or LdcI4S or LdcI4;

    internal static bool IsConstantLoad(IlInstruction instruction) =>
        IsI4(instruction) || instruction.OpCode is Ldstr or LdNull;

    internal static string Name(int opCode) => opCode switch
    {
        0x00 => "nop",
        LdArg0 => "ldarg.0",
        0x03 => "ldarg.1",
        0x04 => "ldarg.2",
        0x05 => "ldarg.3",
        0x06 => "ldloc.0",
        0x07 => "ldloc.1",
        0x08 => "ldloc.2",
        0x09 => "ldloc.3",
        0x0A => "stloc.0",
        0x0B => "stloc.1",
        0x0C => "stloc.2",
        0x0D => "stloc.3",
        LdNull => "ldnull",
        LdcI4M1 => "ldc.i4.m1",
        >= LdcI4_0 and <= LdcI4_8 => "ldc.i4." + (opCode - LdcI4_0),
        LdcI4S => "ldc.i4.s",
        LdcI4 => "ldc.i4",
        Dup => "dup",
        Pop => "pop",
        Call => "call",
        Ret => "ret",
        0x2B => "br.s",
        BrTrueS => "brtrue.s",
        Ldstr => "ldstr",
        Newobj => "newobj",
        Ldfld => "ldfld",
        Ldsfld => "ldsfld",
        Stsfld => "stsfld",
        Newarr => "newarr",
        StelemRef => "stelem.ref",
        Ldftn => "ldftn",
        _ => "0x" + opCode.ToString(opCode > 0xFF ? "X4" : "X2"),
    };
}
