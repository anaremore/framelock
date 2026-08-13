using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameLock.Core;
using FrameLock.Windows;

namespace FrameLock.App;

public sealed class WindowChoice(WindowTarget target) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WindowTarget Target { get; } = target;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            OnPropertyChanged();
        }
    }

    public string ApplicationName => Target.ApplicationName;

    public string Title => Target.Title;

    public string Initial => ApplicationName.Length > 0 ? ApplicationName[..1].ToUpperInvariant() : "?";

    public string AccessibleName => Target.AccessibleName;

    internal void SetIcon(ImageSource? icon) => Icon = icon;

    internal static ImageSource? LoadIcon(WindowTarget target)
    {
        var iconHandle = NativeIconService.AcquireIcon(target.Handle, target.ExecutablePath);
        if (iconHandle == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            source.Freeze();
            return source;
        }
        finally
        {
            NativeIconService.ReleaseIcon(iconHandle);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
