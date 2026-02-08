namespace Infrastructure.CliGui.Ui;

public sealed class CollapseStateManager
{
    private readonly HashSet<string> _collapsedGroups = [];
    private readonly HashSet<string> _userToggledGroups = [];

    public bool IsCollapsed(string groupId)
    {
        return _collapsedGroups.Contains(groupId);
    }

    public void ToggleGroup(string groupId)
    {
        _userToggledGroups.Add(groupId);

        if (!_collapsedGroups.Remove(groupId))
        {
            _collapsedGroups.Add(groupId);
        }
    }

    public void SetCollapsedIfNew(string groupId, bool collapsed)
    {
        if (_userToggledGroups.Contains(groupId))
        {
            return;
        }

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