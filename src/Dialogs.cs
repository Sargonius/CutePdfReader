using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutePdf
{
    static class Dialogs
    {
        // Small "type a name" dialog. Returns null when cancelled.
        public static string AskText(Window owner, string title, string prompt, string initial)
        {
            var window = new Window();
            window.Title = title;
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Background = Ui.Brush("SidebarBrush");
            window.FontFamily = new FontFamily("Segoe UI");
            window.FontSize = 13;
            window.Foreground = Ui.Brush("InkBrush");

            var panel = new StackPanel();
            panel.Margin = new Thickness(20, 16, 20, 16);
            var label = new TextBlock();
            label.Text = prompt;
            panel.Children.Add(label);

            var box = new TextBox();
            box.Text = initial ?? "";
            box.Width = 380;
            box.Margin = new Thickness(0, 8, 0, 16);
            box.Padding = new Thickness(6, 4, 6, 4);
            panel.Children.Add(box);

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var cancel = new Button();
            cancel.Content = "Отмена";
            cancel.Style = Ui.Style("FlatButton");
            cancel.Padding = new Thickness(14, 6, 14, 6);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += delegate { window.DialogResult = false; };
            var ok = new Button();
            ok.Content = "Сохранить";
            ok.Style = Ui.Style("PillButton");
            ok.Padding = new Thickness(18, 6, 18, 6);
            ok.Click += delegate { window.DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            window.Content = panel;

            box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) window.DialogResult = true;
                else if (e.Key == Key.Escape) window.DialogResult = false;
            };
            window.Loaded += delegate
            {
                box.Focus();
                box.SelectAll();
            };
            return window.ShowDialog() == true ? box.Text.Trim() : null;
        }

        public static void Tell(Window owner, string text)
        {
            MessageBox.Show(owner, text, MainWindow.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static bool Confirm(Window owner, string text)
        {
            return MessageBox.Show(owner, text, MainWindow.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
    }
}
