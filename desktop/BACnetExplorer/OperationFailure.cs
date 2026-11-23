using System.IO;

namespace BACnetExplorer;

internal sealed class OperationFailure : IOException
{
    internal string Summary { get; }
    internal string Guidance { get; }
    internal string TechnicalDetails { get; }
    internal OperationFailure(string operation, string summary, string guidance, string raw)
        : base(summary + (guidance.Length == 0 ? "" : "\n\n" + guidance))
    {
        Summary = summary;
        Guidance = guidance;
        TechnicalDetails = $"Engine operation: {operation}\n{raw}";
    }
}
