using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using ClonFences.Services;

namespace ClonFences.Models;

/// <summary>Elemento visible dentro de un fence (archivo, carpeta o elemento del sistema).</summary>
public sealed class FenceItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    private FenceItem(string path, string name, bool isFolder, bool isVirtual, DateTime modified, long size)
    {
        Path = path;
        Name = name;
        IsFolder = isFolder;
        IsVirtual = isVirtual;
        Modified = modified;
        Size = size;
        Extension = isFolder || isVirtual ? "" : System.IO.Path.GetExtension(path).ToLowerInvariant();
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsFolder { get; }
    public bool IsVirtual { get; }
    public DateTime Modified { get; }
    public long Size { get; }
    public string Extension { get; }
    public string ToolTipText => IsVirtual ? Name : Path;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static FenceItem? TryCreate(string path)
    {
        try
        {
            if (ShellPaths.IsVirtual(path))
                return new FenceItem(path, ShellPaths.DisplayName(path), true, true, DateTime.MinValue, 0);

            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return new FenceItem(path, ShellPaths.DisplayName(path), true, false, di.LastWriteTime, 0);
            }
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return new FenceItem(path, ShellPaths.DisplayName(path), false, false, fi.LastWriteTime, fi.Length);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
        return null;
    }
}
