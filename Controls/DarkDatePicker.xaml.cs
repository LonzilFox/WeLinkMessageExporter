using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WeLinkMessageExporter.Controls;

/// <summary>
/// Dark, keyboard-editable ISO date input with a wholly themed calendar popup.
/// Avoids WPF DatePicker's OS-dependent white input and CalendarItem templates.
/// </summary>
public partial class DarkDatePicker : UserControl
{
    private static readonly CultureInfo DateCulture = CultureInfo.InvariantCulture;
    private static readonly string[] WeekdayLabels = { "一", "二", "三", "四", "五", "六", "日" };
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _updatingText;

    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(DarkDatePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value?.Date);
    }

    public DarkDatePicker()
    {
        InitializeComponent();
        foreach (var day in WeekdayLabels)
        {
            WeekdaysGrid.Children.Add(new TextBlock
            {
                Text = day, TextAlignment = TextAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 11, Margin = new Thickness(0, 2, 0, 5)
            });
        }
        UpdateInputText();
    }

    private static void OnSelectedDateChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not DarkDatePicker picker || picker.DateInput is null) return;
        if (picker.SelectedDate is DateTime date)
            picker._displayMonth = new DateTime(date.Year, date.Month, 1);
        picker.UpdateInputText();
        if (picker.CalendarPopup?.IsOpen == true) picker.RebuildCalendar();
    }

    private void UpdateInputText()
    {
        _updatingText = true;
        DateInput.Text = SelectedDate?.ToString("yyyy-MM-dd", DateCulture) ?? "";
        _updatingText = false;
        DateInput.BorderBrush = (Brush)FindResource("InputBorderBrush");
        DateInput.ToolTip = "日期格式：YYYY-MM-DD";
    }

    public bool TryCommit()
    {
        var text = DateInput.Text.Trim();
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", DateCulture, DateTimeStyles.None, out var date))
        {
            SelectedDate = date.Date;
            UpdateInputText();
            return true;
        }
        DateInput.BorderBrush = (Brush)FindResource("WarningBrush");
        DateInput.ToolTip = "日期无效，请输入 YYYY-MM-DD（例如 2026-09-13）";
        return false;
    }

    private void DateInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        DateInput.BorderBrush = (Brush)FindResource("InputBorderBrush");
    }

    private void DateInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        TryCommit(); // On invalid input keep the text and warning; export validates again.
    }

    private void DateInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryCommit();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateInputText();
            e.Handled = true;
        }
    }

    private void OpenCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (CalendarPopup.IsOpen)
        {
            CalendarPopup.IsOpen = false;
            return;
        }
        _displayMonth = SelectedDate is DateTime selected
            ? new DateTime(selected.Year, selected.Month, 1)
            : new DateTime(DateTime.UtcNow.AddHours(8).Year, DateTime.UtcNow.AddHours(8).Month, 1);
        RebuildCalendar();
        CalendarPopup.IsOpen = true;
    }

    private void CalendarPopup_Opened(object sender, EventArgs e) => RebuildCalendar();

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RebuildCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        RebuildCalendar();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        SelectedDate = DateTime.UtcNow.AddHours(8).Date;
        UpdateInputText();
        CalendarPopup.IsOpen = false;
    }

    private void RebuildCalendar()
    {
        if (DaysGrid is null) return;
        MonthTitle.Text = _displayMonth.ToString("yyyy 年 M 月", CultureInfo.GetCultureInfo("zh-CN"));
        DaysGrid.Children.Clear();
        int mondayOffset = ((int)_displayMonth.DayOfWeek + 6) % 7;
        var firstDay = _displayMonth.AddDays(-mondayOffset);
        var today = DateTime.UtcNow.AddHours(8).Date;
        for (int cell = 0; cell < 42; cell++)
        {
            var date = firstDay.AddDays(cell);
            bool inMonth = date.Month == _displayMonth.Month && date.Year == _displayMonth.Year;
            bool selected = SelectedDate?.Date == date.Date;
            bool isToday = date.Date == today;
            var button = new Button
            {
                Content = date.Day.ToString(CultureInfo.InvariantCulture),
                Tag = date,
                Margin = new Thickness(1),
                Padding = new Thickness(1),
                MinHeight = 34,
                Style = (Style)FindResource("CalendarDayButtonStyle"),
                Background = selected ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
                Foreground = selected ? (Brush)FindResource("BackgroundBrush") :
                    inMonth ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedBrush"),
                FontWeight = selected || isToday ? FontWeights.SemiBold : FontWeights.Normal,
                BorderBrush = isToday && !selected ? (Brush)FindResource("BlueBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = inMonth ? 1 : 0.55
            };
            button.Click += (_, _) =>
            {
                SelectedDate = date.Date;
                UpdateInputText();
                CalendarPopup.IsOpen = false;
            };
            DaysGrid.Children.Add(button);
        }
    }
}
