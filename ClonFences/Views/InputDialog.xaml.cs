using System;
using System.Windows;
using System.Windows.Interop;
using ClonFences.Native;

namespace ClonFences.Views;

public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial, int selectLength)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
            Activate();
            InputBox.Focus();
            InputBox.Select(0, selectLength < 0 ? initial.Length : Math.Min(selectLength, initial.Length));
        };
    }

    public static string? Ask(string title, string prompt, string initial = "", int selectLength = -1)
    {
        var dlg = new InputDialog(title, prompt, initial, selectLength);
        return dlg.ShowDialog() == true ? dlg.InputBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
