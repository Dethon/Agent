using Terminal.Gui;

namespace Infrastructure.CliGui.Ui;

internal sealed class ThinkingIndicator(Label titleLabel, string agentName) : IDisposable
{
    private static readonly string[] _spinnerFrames = ["◐", "◓", "◑", "◒"];
    private static readonly TimeSpan _animationInterval = TimeSpan.FromMilliseconds(120);

    private readonly string _baseTitle = $"  ◆ {agentName}";
    private readonly Lock _lock = new();
    private int _frameIndex;
    private volatile bool _isVisible;

    private object? _timerToken;

    public void Dispose()
    {
        Hide();
    }

    public void Show()
    {
        lock (_lock)
        {
            if (_isVisible)
            {
                return;
            }

            _isVisible = true;
            _frameIndex = 0;
        }

        UpdateDisplay();

        var mainLoop = Application.MainLoop;
        if (mainLoop is not null)
        {
            _timerToken = mainLoop.AddTimeout(_animationInterval, OnTimer);
        }
    }

    public void Hide()
    {
        lock (_lock)
        {
            if (!_isVisible)
            {
                return;
            }

            _isVisible = false;

            if (_timerToken is not null)
            {
                Application.MainLoop?.RemoveTimeout(_timerToken);
                _timerToken = null;
            }
        }

        ResetTitle();
    }

    private void ResetTitle()
    {
        var mainLoop = Application.MainLoop;
        if (mainLoop is not null)
        {
            mainLoop.Invoke(() => titleLabel.Text = _baseTitle);
        }
        else
        {
            // Fallback: direct update if no main loop
            titleLabel.Text = _baseTitle;
        }
    }

    private bool OnTimer(MainLoop _)
    {
        if (!_isVisible)
        {
            return false;
        }

        _frameIndex = (_frameIndex + 1) % _spinnerFrames.Length;
        UpdateDisplay();
        return true;
    }

    private void UpdateDisplay()
    {
        if (!_isVisible)
        {
            return;
        }

        var spinner = _spinnerFrames[_frameIndex];
        var mainLoop = Application.MainLoop;

        mainLoop?.Invoke(() =>
        {
            if (!_isVisible)
            {
                return;
            }

            var thinkingText = $"{spinner} Thinking...";
            var availableWidth = titleLabel.Bounds.Width;
            var padding = availableWidth - _baseTitle.Length - thinkingText.Length - 1;
            titleLabel.Text = padding > 0
                ? $"{_baseTitle}{new string(' ', padding)}{thinkingText}"
                : $"{_baseTitle}  {thinkingText}";
        });
    }
}