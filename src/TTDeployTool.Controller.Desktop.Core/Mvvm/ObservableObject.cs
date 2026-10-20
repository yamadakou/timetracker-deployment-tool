using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TTDeployTool.Controller.Desktop.Core.Mvvm;

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

    /// <summary>値が変わった場合だけ設定して通知し、値から決まるプロパティ（<paramref name="dependent"/>）も通知する。</summary>
    protected bool SetProperty<T>(ref T field, T value, Dependent dependent, [CallerMemberName] string propertyName = "")
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;
        OnPropertyChanged(dependent.PropertyName);
        return true;
    }

    /// <summary>値から決まるプロパティの名前（<see cref="SetProperty{T}(ref T, T, Dependent, string)"/> で、通知するプロパティの名前と区別する）。</summary>
    protected readonly record struct Dependent(string PropertyName);

    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
