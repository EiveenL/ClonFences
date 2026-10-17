using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ClonFences.Models;
using ClonFences.Services;

namespace ClonFences.Views;

public partial class SettingsWindow : Window
{
    private static readonly (int Size, string Label)[] IconSizes =
        { (32, "Pequeños (32 px)"), (48, "Medianos (48 px)"), (64, "Grandes (64 px)"), (96, "Muy grandes (96 px)") };

    private readonly FenceManager _mgr;
    private string _color;

    internal SettingsWindow(FenceManager mgr)
    {
        _mgr = mgr;
        InitializeComponent();
        var s = mgr.Settings;

        QuickHideBox.IsChecked = s.QuickHide;
        RightDragBox.IsChecked = s.RightDragCreate;
        HideIconsBox.IsChecked = s.HideDesktopIcons;
        AutoAddBox.IsChecked = s.AutoAddNewItems;
        SnapBox.IsChecked = s.SnapEnabled;
        PeekBox.IsChecked = s.PeekRolledUp;
        LockAllBox.IsChecked = s.LockAll;
        StartupBox.IsChecked = StartupService.IsEnabled();
        BlurBox.IsChecked = s.BlurBehind;

        var fences = mgr.Config.Fences.Where(f => !f.IsPortal).ToList();
        DefaultFenceBox.ItemsSource = fences;
        DefaultFenceBox.SelectedItem = fences.FirstOrDefault(f => f.Id == s.DefaultFenceId);

        _color = s.DefaultColor;
        UpdateSwatch();
        OpacitySlider.Value = Math.Round(s.DefaultOpacity * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";

        foreach (var (_, label) in IconSizes) IconSizeBox.Items.Add(label);
        int idx = Array.FindIndex(IconSizes, x => x.Size == s.DefaultIconSize);
        IconSizeBox.SelectedIndex = idx >= 0 ? idx : 1;
    }

    private void UpdateSwatch() => ColorSwatch.Background = new SolidColorBrush(ColorUtil.Parse(_color, Colors.Black));

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorUtil.PickColor(_color);
        if (picked == null) return;
        _color = picked;
        UpdateSwatch();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText != null) OpacityText.Text = $"{e.NewValue:0}%";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _mgr.Settings;
        bool previousHide = s.HideDesktopIcons;

        s.QuickHide = QuickHideBox.IsChecked == true;
        s.RightDragCreate = RightDragBox.IsChecked == true;
        s.HideDesktopIcons = HideIconsBox.IsChecked == true;
        s.AutoAddNewItems = AutoAddBox.IsChecked == true;
        s.SnapEnabled = SnapBox.IsChecked == true;
        s.PeekRolledUp = PeekBox.IsChecked == true;
        s.LockAll = LockAllBox.IsChecked == true;
        s.BlurBehind = BlurBox.IsChecked == true;
        s.DefaultFenceId = (DefaultFenceBox.SelectedItem as FenceModel)?.Id ?? s.DefaultFenceId;
        s.DefaultColor = _color;
        s.DefaultOpacity = OpacitySlider.Value / 100.0;
        if (IconSizeBox.SelectedIndex >= 0) s.DefaultIconSize = IconSizes[IconSizeBox.SelectedIndex].Size;

        if (ApplyAllBox.IsChecked == true)
            foreach (var f in _mgr.Config.Fences)
            {
                f.Color = null;
                f.Opacity = null;
                f.IconSize = null;
            }

        StartupService.SetEnabled(StartupBox.IsChecked == true);
        _mgr.ApplySettings(previousHide);
        Close();
    }
}
