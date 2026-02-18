using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MSNShared;


namespace MSNClient
{
    // ── Add Contact Dialog ────────────────────────────────────────────────────
    public partial class AddContactDialog : Window
    {
        public string Username { get; private set; } = "";

        public AddContactDialog()
        {
            Title = "Add Contact";
            Width = 340; Height = 160;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.AliceBlue;
            FontFamily = new System.Windows.Media.FontFamily("Tahoma");

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Enter username:", FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            var input = new TextBox { Style = (Style)Application.Current.FindResource("TextBoxStyle"), Height = 26, Margin = new Thickness(0, 0, 0, 12) };
            input.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Return) { Username = input.Text.Trim(); DialogResult = true; } };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtnPrimary"), Content = "Add", Width = 65, Margin = new Thickness(0, 0, 6, 0) };
            okBtn.Click += (s, e) => { Username = input.Text.Trim(); DialogResult = true; };
            var cancelBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = "Cancel", Width = 65 };
            cancelBtn.Click += (s, e) => { DialogResult = false; };
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);

            Grid.SetRow(label, 0); Grid.SetRow(input, 1); Grid.SetRow(btnPanel, 2);
            grid.Children.Add(label); grid.Children.Add(input); grid.Children.Add(btnPanel);
            Content = grid;

            Loaded += (s, e) => input.Focus();
        }
    }

    // ── Create Group Dialog ───────────────────────────────────────────────────
    public partial class CreateGroupDialog : Window
    {
        public string GroupName { get; private set; } = "";
        public string GroupDescription { get; private set; } = "";
        public List<string> SelectedMembers { get; private set; } = new();
        private readonly List<ContactVm> _contacts;

        public CreateGroupDialog(List<ContactVm> contacts)
        {
            _contacts = contacts.Where(c => c.Status != UserStatus.Offline).ToList();
            Title = "Create Group Chat";
            Width = 380; Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.AliceBlue;
            FontFamily = new System.Windows.Media.FontFamily("Tahoma");

            var grid = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 7; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var title = new TextBlock { Text = "Create a Group Chat", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.DarkBlue, Margin = new Thickness(0, 0, 0, 12) };

            var nameLabel = new TextBlock { Text = "Group Name:", FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            var nameInput = new TextBox { Style = (Style)Application.Current.FindResource("TextBoxStyle"), Height = 26, Margin = new Thickness(0, 0, 0, 10) };

            var descLabel = new TextBlock { Text = "Description (optional):", FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            var descInput = new TextBox { Style = (Style)Application.Current.FindResource("TextBoxStyle"), Height = 26, Margin = new Thickness(0, 0, 0, 10) };

            var memberLabel = new TextBlock { Text = "Add Members:", FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            var memberScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = System.Windows.Media.Brushes.LightSteelBlue, BorderThickness = new Thickness(1) };
            var memberStack = new StackPanel { Margin = new Thickness(4) };

            var checkboxes = new List<CheckBox>();
            foreach (var c in _contacts)
            {
                var cb = new CheckBox { Content = $"{c.AvatarEmoji} {c.DisplayName} ({c.Username})", Tag = c.Username, FontSize = 11, Margin = new Thickness(2, 2, 0, 2) };
                memberStack.Children.Add(cb);
                checkboxes.Add(cb);
            }
            if (_contacts.Count == 0)
                memberStack.Children.Add(new TextBlock { Text = "No online contacts to add.", FontSize = 10, Foreground = System.Windows.Media.Brushes.Gray, FontStyle = FontStyles.Italic });

            memberScroll.Content = memberStack;

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var createBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtnPrimary"), Content = "Create Group", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
            createBtn.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(nameInput.Text)) { System.Windows.MessageBox.Show("Enter a group name.", "Error"); return; }
                GroupName = nameInput.Text.Trim();
                GroupDescription = descInput.Text.Trim();
                SelectedMembers = checkboxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag!.ToString()!).ToList();
                DialogResult = true;
            };
            var cancelBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = "Cancel", Padding = new Thickness(12, 4, 12, 4) };
            cancelBtn.Click += (s, e) => { DialogResult = false; };
            btnPanel.Children.Add(createBtn); btnPanel.Children.Add(cancelBtn);

            Grid.SetRow(title, 0); Grid.SetRow(nameLabel, 1); Grid.SetRow(nameInput, 2);
            Grid.SetRow(descLabel, 3);
            // Shift desc down
            var descGrid = new Grid();
            descGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            descGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            descGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            descGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            descGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(descLabel, 0); Grid.SetRow(descInput, 1); Grid.SetRow(memberLabel, 2); Grid.SetRow(memberScroll, 3); Grid.SetRow(btnPanel, 4);
            descGrid.Children.Add(descLabel); descGrid.Children.Add(descInput); descGrid.Children.Add(memberLabel); descGrid.Children.Add(memberScroll); descGrid.Children.Add(btnPanel);

            var outerGrid = new Grid { Margin = new Thickness(14) };
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(title, 0);
            var nameSection = new StackPanel();
            nameSection.Children.Add(nameLabel); nameSection.Children.Add(nameInput);
            Grid.SetRow(nameSection, 1); Grid.SetRow(descGrid, 2);
            outerGrid.Children.Add(title); outerGrid.Children.Add(nameSection); outerGrid.Children.Add(descGrid);
            Content = outerGrid;
        }
    }

    // ── Simple Input Dialog ───────────────────────────────────────────────────
    public partial class SimpleInputDialog : Window
    {
        public string Value { get; private set; } = "";

        public SimpleInputDialog(string title, string label, string defaultValue = "")
        {
            Title = title;
            Width = 340; Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.AliceBlue;
            FontFamily = new System.Windows.Media.FontFamily("Tahoma");

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            var input = new TextBox { Style = (Style)Application.Current.FindResource("TextBoxStyle"), Height = 26, Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
            input.SelectAll();
            input.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Return) { Value = input.Text; DialogResult = true; } };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Style = (Style)Application.Current.FindResource("MSNBtnPrimary"), Content = "OK", Width = 60, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (s, e) => { Value = input.Text; DialogResult = true; };
            var cancel = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = "Cancel", Width = 60 };
            cancel.Click += (s, e) => { DialogResult = false; };
            btnPanel.Children.Add(ok); btnPanel.Children.Add(cancel);

            Grid.SetRow(lbl, 0); Grid.SetRow(input, 1); Grid.SetRow(btnPanel, 2);
            grid.Children.Add(lbl); grid.Children.Add(input); grid.Children.Add(btnPanel);
            Content = grid;
            Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
        }
    }
}
