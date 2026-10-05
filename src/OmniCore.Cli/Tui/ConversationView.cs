using Terminal.Gui.Views;
using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

// The pinned Terminal.Gui release still provides its cell-aware TextView. Isolate
// this compatibility use rather than add a new editor dependency for a read-only transcript.
#pragma warning disable CS0618
internal sealed class ConversationView : TextView
{
    public ConversationView()
    {
        ReadOnly = true;
        WordWrap = true;
        ScrollBars = true;
    }

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }

    protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else base.OnDrawNormalColor(line, idxCol, idxRow);
    }
}
#pragma warning restore CS0618
