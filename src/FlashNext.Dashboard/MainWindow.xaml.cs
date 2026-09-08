using System.IO;

namespace FlashNext.Dashboard;

public partial class MainWindow : System.Windows.Window
{
    private readonly DashboardSession _session;
    private bool _ratingSave;

    public MainWindow(DashboardSession session)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        _session.TranscriptUpdated += ScrollTranscript;
        Closed += (_, _) => _session.TranscriptUpdated -= ScrollTranscript;
    }

    private async void StartClick(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitBindings();
        await _session.StartServerAsync();
    }
    private async void StopClick(object sender, System.Windows.RoutedEventArgs e) => await _session.StopServerAsync();
    private async void RestartClick(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitBindings();
        await _session.RestartServerAsync();
    }
    private async void SaveClick(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitBindings();
        try { await _session.SaveSettingsAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }
    private async void ResetProfileClick(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitBindings();
        System.Windows.MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            "Restore the selected profile's factory defaults? This keeps your model path and system prompt, but replaces that profile's sampling and server settings.",
            "FlashNext",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmation == System.Windows.MessageBoxResult.Yes) await _session.ResetActiveProfileToFactoryAsync();
    }

    private void CommitBindings()
    {
        System.Windows.Input.Keyboard.ClearFocus();
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, static () => { });
    }
    private async void ReportsSelected(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MainTabs) && ReportsTab?.IsSelected == true) await _session.RefreshReportsAsync();
    }
    private async void RefreshReportsClick(object sender, System.Windows.RoutedEventArgs e) => await _session.RefreshReportsAsync();
    private async void ReportRatingChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ratingSave || sender is not System.Windows.Controls.ComboBox { SelectedItem: int rating } || _session.SelectedReportRun is not { } row || rating == row.RatingValue) return;
        _ratingSave = true;
        ReportRatingBox.IsEnabled = false;
        try { await _session.RateRunAsync(row, rating); }
        finally
        {
            ReportRatingBox.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.UpdateTarget();
            ReportRatingBox.IsEnabled = true;
            _ratingSave = false;
        }
    }
    private async void OpenReportClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_session.SelectedReportRun is not { } row || !_session.CanChangeSession) return;
        ChatLine? line = await _session.OpenReportInChatAsync(row);
        if (row.SessionName is not null && row.SessionName != _session.ActiveSession) return;
        MainTabs.SelectedIndex = 0;
        UpdateLayout();
        if (line is not null && Transcript.ItemContainerGenerator.ContainerFromItem(line) is System.Windows.FrameworkElement element)
            element.BringIntoView();
    }
    private async void SendClick(object sender, System.Windows.RoutedEventArgs e) => await _session.SendAsync(DraftBox.Text);
    private void AttachImageClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "Attach image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { _session.AttachImage(dialog.FileName); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }
    private void ClearImageClick(object sender, System.Windows.RoutedEventArgs e) => _session.ClearAttachedImage();
    private void StopChatClick(object sender, System.Windows.RoutedEventArgs e) => _session.StopChat();
    private async void NewChatClick(object sender, System.Windows.RoutedEventArgs e) => await _session.ChangeSessionAsync(null);
    private async void SessionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { SelectedItem: string name })
            await _session.ChangeSessionAsync(name);
    }
    private void CopyLastClick(object sender, System.Windows.RoutedEventArgs e) => CopyResult(_session.CopyLastAnswer());
    private void SaveLastClick(object sender, System.Windows.RoutedEventArgs e) => SaveAnswer(_session.LastAnswerText());
    private void CopyMessageClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.DataContext is ChatLine line)
            CopyResult(_session.CopyAnswer(line));
    }

    private async void DraftKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Shift)
        {
            e.Handled = true;
            await _session.SendAsync(DraftBox.Text);
        }
    }

    private void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control;
        bool ctrlShift = System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
        if (e.Key == System.Windows.Input.Key.C && ctrlShift)
        {
            e.Handled = true;
            CopyResult(_session.CopyLastAnswer());
            return;
        }
        if (e.Key == System.Windows.Input.Key.S && ctrl)
        {
            e.Handled = true;
            SaveAnswer(_session.LastAnswerText());
            return;
        }
        if (e.Key == System.Windows.Input.Key.C && ctrl)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox box && box.SelectionLength > 0) return;
            if (_session.CopyLastAnswer()) e.Handled = true;
        }
    }

    private void CopyResult(bool copied)
    {
        if (!copied) System.Windows.MessageBox.Show("Nothing to copy yet.", "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void SaveAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            System.Windows.MessageBox.Show("Nothing to save yet.", "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        bool html = text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Save last answer",
            FileName = html ? "flashnext-output.html" : "flashnext-output.txt",
            Filter = html ? "HTML (*.html)|*.html|Text (*.txt)|*.txt|All files (*.*)|*.*" : "Text (*.txt)|*.txt|HTML (*.html)|*.html|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, text);
            _session.NoteCopied("Saved " + dialog.FileName);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void ScrollTranscript()
    {
        TranscriptScroll.ScrollToEnd();
    }
}
