using Lattice.Api;

namespace Lattice.Core.Modules;

/// <summary>
/// A game member a module uses, checked at load so a module whose members are missing turns itself off. A method names
/// its parameter types, so an overload whose parameters changed counts as missing.
/// </summary>
internal abstract record GameMember
{
    private GameMember(string typeName, string memberName)
    {
        TypeName = typeName;
        MemberName = memberName;
    }

    /// <summary>The full name of the declaring type (of the type itself for <see cref="Type"/>).</summary>
    public string TypeName { get; }

    /// <summary>The member's name; the type's own full name for <see cref="Type"/>.</summary>
    public string MemberName { get; }

    /// <summary>A type.</summary>
    /// <param name="FullName">Its full name.</param>
    public sealed record Type(string FullName) : GameMember(FullName, FullName);

    /// <summary>A field.</summary>
    public sealed record Field(string DeclaringType, string Name) : GameMember(DeclaringType, Name);

    /// <summary>A property.</summary>
    public sealed record Property(string DeclaringType, string Name) : GameMember(DeclaringType, Name);

    /// <summary>An event.</summary>
    public sealed record Event(string DeclaringType, string Name) : GameMember(DeclaringType, Name);

    /// <summary>A method and the full names of its parameter types, in order; matched exactly.</summary>
    public sealed record Method(string DeclaringType, string Name, ValueList<string> Parameters) : GameMember(DeclaringType, Name);
}

/// <summary>
/// A module's name, the modules it needs and the game members it uses, so the entry point can switch modules on and
/// off: a module whose members are missing, or whose needed module is off, is off with a logged reason.
/// </summary>
/// <param name="Name">The module.</param>
/// <param name="Needs">The modules it needs.</param>
/// <param name="Uses">The game members it uses.</param>
internal sealed record ModuleManifest(ModuleName Name, ValueList<ModuleName> Needs, ValueList<GameMember> Uses);
