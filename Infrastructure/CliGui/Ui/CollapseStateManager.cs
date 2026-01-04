namespace Infrastructure.CliGui.Ui;

public sealed class CollapseStateManager
{
    private readonly HashSet<string> _collapsedGroups = [];

    public bool IsCollapsed(string groupId)
    {
        return _collapsedGroups.Contains(groupId);
    }

    public void ToggleGroup(string groupId)
    {
        if (!_collapsedGroups.Remove(groupId))
        {
            _collapsedGroups.Add(groupId);
        }
    }

    public void SetCollapsed(string groupId, bool collapsed)
    {
        if (collapsed)
        {
            _collapsedGroups.Add(groupId);
        }
        else
        {
            _collapsedGroups.Remove(groupId);
        }
    }
}