namespace Anchor;

/// <summary>
/// A StatusNotifierItem reports one Activate per left click, and a left click
/// opens Status.
/// </summary>
internal sealed class TrayClickFilter
{
    /// <summary>True when this click should open Status.</summary>
    public bool IsActivation() => true;
}
