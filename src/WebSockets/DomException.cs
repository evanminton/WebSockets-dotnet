namespace WebSockets;

/// <summary>
/// The .NET counterpart of the Web IDL <c>DOMException</c>. The <see cref="Name"/> property
/// carries the exception name used by the standard (for example <c>"SyntaxError"</c>).
/// </summary>
public sealed class DomException : Exception
{
    /// <summary>The string is not a valid URL, subprotocol list or close reason.</summary>
    public const string SyntaxError = "SyntaxError";

    /// <summary>The object is in an invalid state for the operation.</summary>
    public const string InvalidStateError = "InvalidStateError";

    /// <summary>The close code is not 1000 or in the range 3000 to 4999.</summary>
    public const string InvalidAccessError = "InvalidAccessError";

    /// <summary>Creates a new exception with the given name and message.</summary>
    public DomException(string name, string message)
        : base(message)
    {
        Name = name;
    }

    /// <summary>The <c>DOMException</c> name, such as <see cref="SyntaxError"/>.</summary>
    public string Name { get; }

    /// <summary>The legacy numeric code for <see cref="Name"/>, or 0 when it has none.</summary>
    public int Code => Name switch
    {
        InvalidStateError => 11,
        SyntaxError => 12,
        InvalidAccessError => 15,
        _ => 0,
    };
}
