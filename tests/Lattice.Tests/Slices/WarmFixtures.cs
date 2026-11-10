using System.Collections.Generic;

namespace Lattice.Tests.Slices.Warm;

public enum Colour
{
    Red,
    Green,
}

public sealed class Box<T>
{
    public T? Value { get; set; }

    public T? Get() => Value;
}

public sealed class Holder
{
    public Box<int> Valued() => new();

    public Box<string> Shared() => new();

    public Box<Colour> Enumerated() => new();

    public int? Optional() => null;

    public List<KeyValuePair<int, string>> Pairs() => [];
}

public sealed class EnumHolder
{
    public HashSet<Colour> Set() => [];

    public Dictionary<string, List<Colour>> Map() => [];

    public HashSet<int> Ints() => [];
}

public sealed class Wide<T>
{
    public T? Value { get; set; }

    public T? M1() => Value;

    public T? M2() => Value;

    public T? M3() => Value;

    public T? M4() => Value;

    public T? M5() => Value;

    public T? M6() => Value;

    public T? M7() => Value;

    public T? M8() => Value;

    public T? M9() => Value;

    public T? M10() => Value;

    public T? M11() => Value;

    public T? M12() => Value;

    public T? M13() => Value;

    public T? M14() => Value;

    public T? M15() => Value;

    public T? M16() => Value;

    public T? M17() => Value;

    public T? M18() => Value;

    public T? M19() => Value;

    public T? M20() => Value;

    public T? M21() => Value;

    public T? M22() => Value;

    public T? M23() => Value;

    public T? M24() => Value;
}

public sealed class Plain
{

    public int P1(int x) => x + 1;

    public int P2(int x) => x + 2;

    public int P3(int x) => x + 3;

    public int P4(int x) => x + 4;

    public int P5(int x) => x + 5;

    public int P6(int x) => x + 6;

    public int P7(int x) => x + 7;

    public int P8(int x) => x + 8;

    public int P9(int x) => x + 9;

    public int P10(int x) => x + 10;

    public int P11(int x) => x + 11;

    public int P12(int x) => x + 12;

    public int P13(int x) => x + 13;

    public int P14(int x) => x + 14;

    public int P15(int x) => x + 15;

    public int P16(int x) => x + 16;

    public int P17(int x) => x + 17;

    public int P18(int x) => x + 18;

    public int P19(int x) => x + 19;

    public int P20(int x) => x + 20;

    public int P21(int x) => x + 21;

    public int P22(int x) => x + 22;

    public int P23(int x) => x + 23;

    public int P24(int x) => x + 24;

    public int P25(int x) => x + 25;

    public int P26(int x) => x + 26;

    public int P27(int x) => x + 27;

    public int P28(int x) => x + 28;

    public int P29(int x) => x + 29;

    public int P30(int x) => x + 30;

    public int P31(int x) => x + 31;

    public int P32(int x) => x + 32;

    public int P33(int x) => x + 33;

    public int P34(int x) => x + 34;

    public int P35(int x) => x + 35;

    public int P36(int x) => x + 36;

    public int P37(int x) => x + 37;

    public int P38(int x) => x + 38;

    public int P39(int x) => x + 39;

    public int P40(int x) => x + 40;
}

public sealed class WideHolder
{
    public Wide<int> A() => new();

    public Wide<long> B() => new();

    public Wide<Colour> C() => new();
}
