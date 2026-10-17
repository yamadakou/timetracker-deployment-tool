using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Timetracker.Controller.Desktop.Core.Mvvm;

/// <summary>プロパティの変更を画面に知らせる ViewModel の基底。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>値が変わった場合だけ設定して通知する。</summary>
    /// <returns>値が変わったか</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
